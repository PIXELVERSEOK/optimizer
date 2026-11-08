using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EvoOptimizer.Infrastructure.GameProfiles;

public sealed record Cs2AutoexecSetting(string Name, string Value);

public sealed record Cs2AutoexecSnapshot(
    string Path,
    bool FileExists,
    IReadOnlyList<Cs2AutoexecSetting> Settings,
    string? BackupPath,
    bool CanRestore,
    string RestoreStatus);

public sealed class Cs2AutoexecConfigService
{
    private const string BeginMarker = "// EVO FPS OPTIMIZER BEGIN";
    private const string EndMarker = "// EVO FPS OPTIMIZER END";

    private static readonly HashSet<string> SupportedVariables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cl_hud_color",
            "cl_showfps",
            "fps_max",
            "fps_max_ui",
            "r_fullscreen_gamma"
        };

    private static readonly Regex SettingPattern = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9_]*)\s+(?<value>-?(?:\d+(?:\.\d*)?|\.\d+))$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _stateDirectory;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Cs2AutoexecConfigService(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        _stateDirectory = Path.GetFullPath(stateDirectory);
        _journalPath = Path.Combine(_stateDirectory, "cs2-autoexec-change.json");
    }

    public static IReadOnlyCollection<string> SupportedVariableNames =>
        SupportedVariables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<Cs2AutoexecSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string targetPath = ValidateTargetPath(path);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool exists = File.Exists(targetPath);
            byte[]? content = exists
                ? await File.ReadAllBytesAsync(targetPath, cancellationToken)
                : null;
            string text = content is null ? string.Empty : Decode(content);
            IReadOnlyList<Cs2AutoexecSetting> settings =
                ReadManagedSettings(text);

            var journal = await ReadJournalAsync(cancellationToken);
            bool isCurrentTarget = journal is not null &&
                string.Equals(
                    journal.TargetPath,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase);
            bool matchesAppliedState = isCurrentTarget &&
                exists &&
                IsRestorableContent(content!, journal!);

            string restoreStatus = !isCurrentTarget
                ? "No hay una operación EVO pendiente de restauración para este archivo."
                : matchesAppliedState
                    ? journal!.OriginalFileExisted
                        ? "El archivo coincide con el estado escrito por EVO; puede restaurarse el backup."
                        : "El archivo fue creado por EVO y no cambió; restaurar lo eliminará."
                    : "El archivo cambió fuera de EVO; se bloquea la restauración para no sobrescribir esos cambios.";

            return new Cs2AutoexecSnapshot(
                targetPath,
                exists,
                settings,
                isCurrentTarget ? journal!.BackupPath : null,
                matchesAppliedState,
                restoreStatus);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Cs2AutoexecSnapshot> ApplyAsync(
        string path,
        IReadOnlyCollection<Cs2AutoexecSetting> settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string targetPath = ValidateTargetPath(path);
        ValidateSettings(settings);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool originalExists = File.Exists(targetPath);
            byte[]? originalBytes = originalExists
                ? await File.ReadAllBytesAsync(targetPath, cancellationToken)
                : null;
            string originalText = originalBytes is null
                ? string.Empty
                : Decode(originalBytes);
            string newline = originalText.Contains("\r\n", StringComparison.Ordinal)
                ? "\r\n"
                : "\n";
            string updatedText = MergeManagedSettings(
                originalText,
                settings,
                newline);
            byte[] updatedBytes = Encode(updatedText, originalBytes);
            string updatedHash = Hash(updatedBytes);
            if ((originalBytes is null && updatedBytes.Length == 0) ||
                (originalBytes is not null &&
                 updatedBytes.AsSpan().SequenceEqual(originalBytes)))
            {
                return await LoadWithoutLockAsync(targetPath, cancellationToken);
            }

            string? backupPath = null;
            if (originalBytes is not null)
            {
                backupPath = targetPath +
                    ".evo-backup-" +
                    DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") +
                    "-" +
                    Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(
                    backupPath,
                    originalBytes,
                    cancellationToken);
                byte[] backupBytes = await File.ReadAllBytesAsync(
                    backupPath,
                    cancellationToken);
                if (!string.Equals(
                        Hash(backupBytes),
                        Hash(originalBytes),
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        "No se pudo verificar el backup de autoexec.cfg.");
                }
            }

            var journal = new ChangeJournal(
                targetPath,
                originalExists,
                originalBytes is null ? null : Hash(originalBytes),
                backupPath,
                updatedHash,
                DateTimeOffset.UtcNow);

            Directory.CreateDirectory(_stateDirectory);
            await WriteJournalAsync(journal, cancellationToken);

            EnsureTargetUnchanged(targetPath, originalExists, originalBytes);

            string temporaryPath = targetPath + ".evo-tmp-" +
                Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(
                    temporaryPath,
                    updatedBytes,
                    cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            byte[] appliedBytes = await File.ReadAllBytesAsync(
                targetPath,
                cancellationToken);
            if (!string.Equals(Hash(appliedBytes), updatedHash, StringComparison.Ordinal))
            {
                throw new IOException(
                    "La verificación del autoexec.cfg no coincide con los datos escritos.");
            }

            return await LoadWithoutLockAsync(targetPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Cs2AutoexecSnapshot> RestoreAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string targetPath = ValidateTargetPath(path);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var journal = await ReadJournalAsync(cancellationToken);
            if (journal is null ||
                !string.Equals(
                    journal.TargetPath,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "No hay un cambio EVO pendiente para este autoexec.cfg.");
            }

            if (!File.Exists(targetPath))
            {
                throw new IOException(
                    "El autoexec.cfg ya no existe; no se puede verificar el estado escrito por EVO.");
            }

            byte[] currentBytes = await File.ReadAllBytesAsync(
                targetPath,
                cancellationToken);
            if (!IsRestorableContent(currentBytes, journal))
            {
                throw new IOException(
                    "El archivo cambió fuera de EVO después de la aplicación. " +
                    "No se restauró para evitar sobrescribir cambios externos.");
            }

            if (journal.OriginalFileExisted)
            {
                string backupPath = ValidateBackupPath(
                    targetPath,
                    journal.BackupPath);
                if (!File.Exists(backupPath))
                    throw new FileNotFoundException(
                        "No se encontró el backup de configuración de EVO.",
                        backupPath);

                byte[] backupBytes = await File.ReadAllBytesAsync(
                    backupPath,
                    cancellationToken);
                if (!string.Equals(
                        Hash(backupBytes),
                        journal.OriginalHash,
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        "El backup de configuración no coincide con su huella original.");
                }

                string temporaryPath = targetPath + ".evo-restore-" +
                    Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllBytesAsync(
                        temporaryPath,
                        backupBytes,
                        cancellationToken);
                    File.Move(temporaryPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
            else
            {
                File.Delete(targetPath);
            }

            if (File.Exists(_journalPath))
                File.Delete(_journalPath);

            return await LoadWithoutLockAsync(targetPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void ValidateSettings(
        IReadOnlyCollection<Cs2AutoexecSetting> settings)
    {
        if (settings.Count > 64)
            throw new ArgumentException("El perfil admite hasta 64 variables.", nameof(settings));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting is null ||
                !SupportedVariables.Contains(setting.Name) ||
                !SettingPattern.IsMatch($"{setting.Name} {setting.Value}"))
            {
                throw new ArgumentException(
                    "Cada fila debe usar una variable permitida y un valor numérico simple. " +
                    $"Variables permitidas: {string.Join(", ", SupportedVariableNames)}.",
                    nameof(settings));
            }

            if (!names.Add(setting.Name))
                throw new ArgumentException(
                    $"La variable '{setting.Name}' está repetida.",
                    nameof(settings));
        }
    }

    private async Task<Cs2AutoexecSnapshot> LoadWithoutLockAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        bool exists = File.Exists(targetPath);
        byte[]? content = exists
            ? await File.ReadAllBytesAsync(targetPath, cancellationToken)
            : null;
        string text = content is null ? string.Empty : Decode(content);
        IReadOnlyList<Cs2AutoexecSetting> settings = ReadManagedSettings(text);
        var journal = await ReadJournalAsync(cancellationToken);
        bool isCurrentTarget = journal is not null &&
            string.Equals(
                journal.TargetPath,
                targetPath,
                StringComparison.OrdinalIgnoreCase);
        bool canRestore = isCurrentTarget &&
            exists &&
            IsRestorableContent(content!, journal!);

        string restoreStatus = !isCurrentTarget
            ? "No hay una operación EVO pendiente de restauración para este archivo."
            : canRestore
                ? journal!.OriginalFileExisted
                    ? "El archivo coincide con el estado escrito por EVO; puede restaurarse el backup."
                    : "El archivo fue creado por EVO y no cambió; restaurar lo eliminará."
                : "El archivo cambió fuera de EVO; se bloquea la restauración para no sobrescribir esos cambios.";

        return new Cs2AutoexecSnapshot(
            targetPath,
            exists,
            settings,
            isCurrentTarget ? journal!.BackupPath : null,
            canRestore,
            restoreStatus);
    }

    private async Task<ChangeJournal?> ReadJournalAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return null;

        string json = await File.ReadAllTextAsync(
            _journalPath,
            cancellationToken);
        var journal = JsonSerializer.Deserialize<ChangeJournal>(
            json,
            JsonOptions);
        if (journal is null ||
            string.IsNullOrWhiteSpace(journal.TargetPath) ||
            string.IsNullOrWhiteSpace(journal.AppliedHash))
        {
            throw new InvalidDataException(
                "El diario de cambios de configuración CS2 está dañado.");
        }

        string normalizedTarget = ValidateTargetPath(journal.TargetPath);
        if (!string.Equals(
                normalizedTarget,
                journal.TargetPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "La ruta registrada en el diario de configuración CS2 no es válida.");
        }

        if (journal.OriginalFileExisted)
            _ = ValidateBackupPath(normalizedTarget, journal.BackupPath);

        return journal;
    }

    private async Task WriteJournalAsync(
        ChangeJournal journal,
        CancellationToken cancellationToken)
    {
        string temporaryPath = _journalPath + ".tmp";
        string json = JsonSerializer.Serialize(journal, JsonOptions);
        await File.WriteAllTextAsync(
            temporaryPath,
            json,
            cancellationToken);
        File.Move(temporaryPath, _journalPath, overwrite: true);
    }

    private static string ValidateTargetPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        DirectoryInfo? cfg = file.Directory;
        DirectoryInfo? csgo = cfg?.Parent;
        DirectoryInfo? game = csgo?.Parent;

        if (!string.Equals(file.Name, "autoexec.cfg", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cfg?.Name, "cfg", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(csgo?.Name, "csgo", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(game?.Name, "game", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "La ruta debe apuntar a game\\csgo\\cfg\\autoexec.cfg dentro de la instalación de CS2.",
                nameof(path));
        }

        if (cfg is null || !cfg.Exists)
            throw new DirectoryNotFoundException(
                "La carpeta de configuración de CS2 no existe.");

        return fullPath;
    }

    private static string ValidateBackupPath(string targetPath, string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new InvalidDataException(
                "El diario no contiene una ruta de backup.");

        string fullTarget = Path.GetFullPath(targetPath);
        string fullBackup = Path.GetFullPath(backupPath);
        if (!fullBackup.StartsWith(
                fullTarget + ".evo-backup-",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "La ruta del backup no pertenece al autoexec.cfg administrado por EVO.");
        }

        return fullBackup;
    }

    private static void EnsureTargetUnchanged(
        string path,
        bool originalExists,
        byte[]? originalBytes)
    {
        bool currentExists = File.Exists(path);
        if (currentExists != originalExists)
            throw new IOException(
                "El archivo cambió mientras se preparaba la escritura; no se aplicó el perfil.");

        if (originalExists &&
            !string.Equals(
                Hash(File.ReadAllBytes(path)),
                Hash(originalBytes!),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "El archivo cambió mientras se preparaba la escritura; no se aplicó el perfil.");
        }
    }

    private static IReadOnlyList<Cs2AutoexecSetting> ReadManagedSettings(
        string content)
    {
        var block = FindManagedBlock(content);
        if (block is null)
            return Array.Empty<Cs2AutoexecSetting>();

        var settings = new List<Cs2AutoexecSetting>();
        foreach (string line in block.Value.Content.Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var match = SettingPattern.Match(line.Trim());
            if (!match.Success)
                throw new InvalidDataException(
                    "El bloque EVO de autoexec.cfg contiene una fila no válida. " +
                    "No se sobrescribió el archivo.");

            settings.Add(new Cs2AutoexecSetting(
                match.Groups["name"].Value,
                match.Groups["value"].Value));
        }

        ValidateSettings(settings);
        return settings;
    }

    private static string MergeManagedSettings(
        string content,
        IReadOnlyCollection<Cs2AutoexecSetting> settings,
        string newline)
    {
        var block = FindManagedBlock(content);
        string baseContent = block is null
            ? content
            : content[..block.Value.Start] +
              content[block.Value.End..];

        if (settings.Count == 0)
            return baseContent;

        string body = string.Join(
            newline,
            settings.Select(setting => $"{setting.Name} {setting.Value}"));
        string managedBlock = BeginMarker + newline +
            body + newline +
            EndMarker;

        if (block is not null)
            return baseContent.Insert(block.Value.Start, managedBlock);

        string separator = baseContent.Length == 0 ||
            baseContent.EndsWith("\n", StringComparison.Ordinal)
                ? string.Empty
                : newline;

        return baseContent + separator + managedBlock + newline;
    }

    private static ManagedBlock? FindManagedBlock(string content)
    {
        int begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = content.IndexOf(EndMarker, StringComparison.Ordinal);

        if (begin < 0 && end < 0)
            return null;

        if (begin < 0 ||
            end < 0 ||
            content.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) >= 0 ||
            content.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0 ||
            end <= begin)
        {
            throw new InvalidDataException(
                "Los marcadores de configuración EVO están incompletos o repetidos.");
        }

        int contentStart = begin + BeginMarker.Length;
        if (content.AsSpan(contentStart).StartsWith("\r\n", StringComparison.Ordinal))
            contentStart += 2;
        else if (content.AsSpan(contentStart).StartsWith("\n", StringComparison.Ordinal))
            contentStart += 1;

        int contentEnd = end;
        if (contentEnd > contentStart && content[contentEnd - 1] == '\n')
        {
            contentEnd--;
            if (contentEnd > contentStart && content[contentEnd - 1] == '\r')
                contentEnd--;
        }

        int blockEnd = end + EndMarker.Length;
        if (blockEnd < content.Length && content[blockEnd] == '\r')
            blockEnd++;
        if (blockEnd < content.Length && content[blockEnd] == '\n')
            blockEnd++;

        return new ManagedBlock(
            begin,
            blockEnd,
            content[contentStart..contentEnd]);
    }

    private static string Decode(byte[] bytes)
    {
        bool hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        var encoding = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        return encoding.GetString(
            bytes,
            hasUtf8Bom ? Encoding.UTF8.GetPreamble().Length : 0,
            bytes.Length - (hasUtf8Bom ? Encoding.UTF8.GetPreamble().Length : 0));
    }

    private static byte[] Encode(string text, byte[]? originalBytes)
    {
        byte[] body = new UTF8Encoding(false).GetBytes(text);
        if (originalBytes is null ||
            !originalBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return body;

        return Encoding.UTF8.GetPreamble().Concat(body).ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsRestorableContent(
        byte[] currentBytes,
        ChangeJournal journal)
    {
        string currentHash = Hash(currentBytes);
        return string.Equals(
                   currentHash,
                   journal.AppliedHash,
                   StringComparison.Ordinal) ||
               (journal.OriginalFileExisted &&
                string.Equals(
                    currentHash,
                    journal.OriginalHash,
                    StringComparison.Ordinal));
    }

    private sealed record ChangeJournal(
        string TargetPath,
        bool OriginalFileExisted,
        string? OriginalHash,
        string? BackupPath,
        string AppliedHash,
        DateTimeOffset AppliedAt);

    private readonly record struct ManagedBlock(
        int Start,
        int End,
        string Content);
}
