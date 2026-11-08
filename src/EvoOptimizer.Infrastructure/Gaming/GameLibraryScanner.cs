using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EvoOptimizer.Core.Gaming;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Gaming;

public sealed record GameLibraryScanResult(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<string> Notes);

/// <summary>
/// Detección de solo lectura de juegos instalados: Steam (registro +
/// libraryfolders.vdf + appmanifest_*.acf), Epic Games Launcher (manifiestos
/// .item) y juegos agregados manualmente por la usuaria. Nunca asume una
/// ubicación universal: cada lanzador se consulta en su propia fuente.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameLibraryScanner
{
    private readonly string _manualStorePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Ejecutables conocidos por AppId cuando el lanzador no los expone.</summary>
    private static readonly Dictionary<string, string> KnownSteamExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["730"] = @"game\bin\win64\cs2.exe"
        };

    private static readonly string[] ExcludedExecutableFragments =
    [
        "uninstall", "unins", "redist", "vcredist", "dxsetup", "dxwebsetup",
        "crashreport", "crashhandler", "unitycrashhandler", "setup",
        "install", "cleanup", "helper", "launcher_e", "eac", "battleye",
        "beclient", "beservice", "easyanticheat", "notification", "updater"
    ];

    public GameLibraryScanner(string? storageDirectory = null)
    {
        string directory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EvoFpsOptimizer",
            "Gaming");

        Directory.CreateDirectory(directory);
        _manualStorePath = Path.Combine(directory, "manual-games.json");
    }

    public async Task<GameLibraryScanResult> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        var games = new List<InstalledGame>();
        var notes = new List<string>();

        try
        {
            games.AddRange(ScanSteam(notes));
        }
        catch (Exception ex)
        {
            notes.Add($"Steam: error de lectura ({ex.Message}). No se asumió ninguna biblioteca.");
        }

        try
        {
            games.AddRange(ScanEpic(notes));
        }
        catch (Exception ex)
        {
            notes.Add($"Epic: error de lectura ({ex.Message}).");
        }

        try
        {
            games.AddRange(await LoadManualGamesAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            notes.Add($"Juegos manuales: error de lectura ({ex.Message}).");
        }

        var unique = games
            .Where(InstalledGameRules.IsValid)
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(game => game.Name)
            .ToArray();

        notes.Add($"Detección completada: {unique.Length} juegos " +
                  $"({unique.Count(g => g.Launcher == GameLauncher.Steam)} Steam, " +
                  $"{unique.Count(g => g.Launcher == GameLauncher.Epic)} Epic, " +
                  $"{unique.Count(g => g.Launcher == GameLauncher.Manual)} manuales).");

        return new GameLibraryScanResult(unique, notes);
    }

    // ── Steam ────────────────────────────────────────────────────────────────

    private IEnumerable<InstalledGame> ScanSteam(List<string> notes)
    {
        string? steamPath = ReadSteamPathFromRegistry();

        if (steamPath is null || !Directory.Exists(steamPath))
        {
            notes.Add("Steam no detectado: sin clave de registro SteamPath válida.");
            yield break;
        }

        string libraryFoldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steamPath };

        if (File.Exists(libraryFoldersFile))
        {
            libraries.AddRange(ParseSteamLibraryFolders(
                File.ReadAllText(libraryFoldersFile)));
        }

        libraries = libraries
            .Select(NormalizePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int found = 0;

        foreach (string library in libraries)
        {
            string steamApps = Path.Combine(library, "steamapps");

            if (!Directory.Exists(steamApps))
                continue;

            foreach (string manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                InstalledGame? game = ParseSteamManifest(
                    File.ReadAllText(manifest), library);

                if (game is not null)
                {
                    found++;
                    yield return game;
                }
            }
        }

        notes.Add($"Steam: {libraries.Count} bibliotecas, {found} manifiestos leídos.");
    }

    private static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");

            if (key?.GetValue("SteamPath") is string path &&
                !string.IsNullOrWhiteSpace(path))
                return NormalizePath(path);
        }
        catch
        {
            // Sin acceso al registro: no se asume ninguna ruta.
        }

        return null;
    }

    /// <summary>Parser puro del libraryfolders.vdf (formato nuevo y legacy).</summary>
    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdfContent)
    {
        var paths = new List<string>();

        foreach (Match match in Regex.Matches(
            vdfContent, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase))
        {
            paths.Add(match.Groups[1].Value);
        }

        // Formato legacy: "1" "D:\\SteamLibrary"
        foreach (Match match in Regex.Matches(
            vdfContent, @"""\d+""\s*""([A-Za-z]:[^""]+)"""))
        {
            paths.Add(match.Groups[1].Value);
        }

        return paths
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Parser puro de un appmanifest_*.acf de Steam.</summary>
    public static InstalledGame? ParseSteamManifest(string acfContent, string libraryRoot)
    {
        string? appId = ExtractAcfValue(acfContent, "appid");
        string? name = ExtractAcfValue(acfContent, "name");
        string? installDir = ExtractAcfValue(acfContent, "installdir");

        if (appId is null || string.IsNullOrWhiteSpace(name) || installDir is null)
            return null;

        // Los DLC no tienen installdir propio ejecutable; se omiten si comparten
        // nombre con el juego base (mismo id de instalación no ocurre: appid único).
        string installPath = Path.Combine(libraryRoot, "steamapps", "common", installDir);

        if (!Directory.Exists(installPath))
            return null;

        string? executablePath = ResolveSteamExecutable(appId, installPath);
        string executableName = executablePath is not null
            ? Path.GetFileName(executablePath)
            : installDir + ".exe";

        return new InstalledGame(
            InstalledGameRules.MakeId(GameLauncher.Steam, appId, name),
            name,
            executableName,
            executablePath,
            installPath,
            GameLauncher.Steam,
            appId);
    }

    private static string? ExtractAcfValue(string content, string key)
    {
        var match = Regex.Match(
            content,
            $@"""{Regex.Escape(key)}""\s*""([^""]*)""",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ResolveSteamExecutable(string appId, string installPath)
    {
        if (KnownSteamExecutables.TryGetValue(appId, out string? known))
        {
            string knownPath = Path.Combine(installPath, known);

            if (File.Exists(knownPath))
                return knownPath;
        }

        // Heurística conservadora: un único .exe evidente en la raíz de la
        // instalación. Si hay ambigüedad, la usuaria elige el ejecutable.
        try
        {
            var candidates = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !ExcludedExecutableFragments.Any(fragment =>
                    Path.GetFileName(path).Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                .Take(2)
                .ToArray();

            return candidates.Length == 1 ? candidates[0] : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Epic Games ───────────────────────────────────────────────────────────

    private IEnumerable<InstalledGame> ScanEpic(List<string> notes)
    {
        string manifestsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsDirectory))
        {
            notes.Add("Epic Games Launcher no detectado: sin carpeta de manifiestos.");
            yield break;
        }

        int found = 0;

        foreach (string manifestFile in Directory.EnumerateFiles(manifestsDirectory, "*.item"))
        {
            InstalledGame? game = null;

            try
            {
                game = ParseEpicManifest(File.ReadAllText(manifestFile));
            }
            catch (JsonException)
            {
                // Manifiesto dañado: se omite sin inventar datos.
            }

            if (game is not null)
            {
                found++;
                yield return game;
            }
        }

        notes.Add($"Epic: {found} manifiestos leídos.");
    }

    /// <summary>Parser puro de un manifiesto .item de Epic.</summary>
    public static InstalledGame? ParseEpicManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<EpicManifest>(json, JsonOptions);

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.DisplayName) ||
            string.IsNullOrWhiteSpace(manifest.InstallLocation) ||
            string.IsNullOrWhiteSpace(manifest.LaunchExecutable))
            return null;

        if (!Directory.Exists(manifest.InstallLocation))
            return null;

        string executablePath = Path.GetFullPath(
            Path.Combine(manifest.InstallLocation, manifest.LaunchExecutable));

        if (!File.Exists(executablePath))
            return null;

        return new InstalledGame(
            InstalledGameRules.MakeId(GameLauncher.Epic, manifest.CatalogItemId, manifest.DisplayName),
            manifest.DisplayName,
            Path.GetFileName(executablePath),
            executablePath,
            manifest.InstallLocation,
            GameLauncher.Epic,
            manifest.CatalogItemId);
    }

    private sealed record EpicManifest(
        [property: JsonPropertyName("DisplayName")] string? DisplayName,
        [property: JsonPropertyName("InstallLocation")] string? InstallLocation,
        [property: JsonPropertyName("LaunchExecutable")] string? LaunchExecutable,
        [property: JsonPropertyName("CatalogItemId")] string? CatalogItemId);

    // ── Juegos manuales ──────────────────────────────────────────────────────

    public async Task<InstalledGame?> AddManualGameAsync(
        string executablePath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath) ||
            !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        string fullPath = Path.GetFullPath(executablePath);
        string name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : displayName.Trim();

        var game = new InstalledGame(
            InstalledGameRules.MakeId(GameLauncher.Manual, null, fullPath),
            name,
            Path.GetFileName(fullPath),
            fullPath,
            Path.GetDirectoryName(fullPath),
            GameLauncher.Manual,
            LauncherAppId: null);

        var games = (await LoadManualGamesAsync(cancellationToken)).ToList();
        games.RemoveAll(existing => string.Equals(existing.Id, game.Id, StringComparison.OrdinalIgnoreCase));
        games.Add(game);

        await SaveManualGamesAsync(games, cancellationToken);

        return game;
    }

    public async Task<bool> RemoveManualGameAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var games = (await LoadManualGamesAsync(cancellationToken)).ToList();
        int removed = games.RemoveAll(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
            await SaveManualGamesAsync(games, cancellationToken);

        return removed > 0;
    }

    private async Task<IReadOnlyList<InstalledGame>> LoadManualGamesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_manualStorePath))
            return Array.Empty<InstalledGame>();

        string json = await File.ReadAllTextAsync(_manualStorePath, cancellationToken);
        var games = JsonSerializer.Deserialize<List<InstalledGame>>(json, JsonOptions);

        return games?
            .Where(InstalledGameRules.IsValid)
            .Where(game => game.ExecutablePath is null || File.Exists(game.ExecutablePath))
            .ToArray()
            ?? Array.Empty<InstalledGame>();
    }

    private async Task SaveManualGamesAsync(
        IReadOnlyList<InstalledGame> games,
        CancellationToken cancellationToken)
    {
        string temporary = _manualStorePath + ".tmp";
        string json = JsonSerializer.Serialize(games, JsonOptions);

        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, _manualStorePath, overwrite: true);
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');

        // "D:\" no debe perder la barra: "D:" significa el directorio actual.
        return normalized.Length == 2 && normalized[1] == ':'
            ? normalized + "\\"
            : normalized;
    }
}