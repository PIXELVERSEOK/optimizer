namespace EvoOptimizer.Core.Gaming;

public enum GameLauncher
{
    Steam,
    Epic,
    Manual,
    Unknown
}

/// <summary>
/// Juego instalado detectado en el equipo. ExecutablePath es null cuando el
/// lanzador no expone un ejecutable principal inequívoco: en ese caso la usuaria
/// lo elige manualmente y nunca se inventa una ruta.
/// </summary>
public sealed record InstalledGame(
    string Id,
    string Name,
    string ExecutableName,
    string? ExecutablePath,
    string? InstallDirectory,
    GameLauncher Launcher,
    string? LauncherAppId);

public static class InstalledGameRules
{
    public static bool IsValid(InstalledGame game)
    {
        if (string.IsNullOrWhiteSpace(game.Id) ||
            string.IsNullOrWhiteSpace(game.Name) ||
            string.IsNullOrWhiteSpace(game.ExecutableName))
            return false;

        if (game.Id.Length > 120 ||
            game.Id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            return false;

        if (!game.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            game.ExecutableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        if (game.ExecutablePath is not null &&
            !Path.IsPathRooted(game.ExecutablePath))
            return false;

        return true;
    }

    /// <summary>Identificador estable por lanzador + app id (o nombre normalizado).</summary>
    public static string MakeId(GameLauncher launcher, string? appId, string name)
    {
        string basis = string.IsNullOrWhiteSpace(appId)
            ? Normalize(name)
            : Normalize(appId);

        return $"{launcher.ToString().ToLowerInvariant()}-{basis}";
    }

    private static string Normalize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars).Trim('-');

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Length == 0 ? "game" : slug[..Math.Min(80, slug.Length)];
    }
}