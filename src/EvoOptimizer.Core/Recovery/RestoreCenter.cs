using System.Text.Json;

namespace EvoOptimizer.Core.Recovery;

public sealed class RestoreCenter
{
    private readonly string _journalPath;
    private readonly ISettingStore _store;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private static readonly HashSet<(string Path, string Name)> AllowedSettings =
        new(StringTupleComparer.OrdinalIgnoreCase)
        {
            (
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
                "AppCaptureEnabled"
            ),
            (
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
                "HistoricalCaptureEnabled"
            ),
            (
                @"Software\Microsoft\GameBar",
                "AutoGameModeEnabled"
            )
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public RestoreCenter(string journalPath, ISettingStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(store);

        _journalPath = journalPath;
        _store = store;
    }

    public async Task<RestoreJournal> GetJournalAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            return await LoadJournalAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<RestoreEntry> ApplyAsync(
        string path,
        string valueName,
        StoredSetting requestedValue,
        CancellationToken cancellationToken = default)
    {
        ValidateAllowedSetting(path, valueName);
        ValidateValue(requestedValue);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var journal = await LoadJournalAsync(cancellationToken);

            var original = await _store.ReadAsync(
                path, valueName, cancellationToken);

            var entry = new RestoreEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "Windows",
                path,
                valueName,
                original,
                requestedValue,
                "Prepared",
                null);

            // Write-ahead journal: persistimos antes de tocar el sistema.
            var entries = journal.Entries.Append(entry).ToArray();

            await SaveJournalAsync(
                journal with { Entries = entries },
                cancellationToken);

            try
            {
                await _store.WriteAsync(
                    path, valueName, requestedValue, cancellationToken);

                entry = entry with { Status = "Applied" };

                entries[^1] = entry;

                await SaveJournalAsync(
                    journal with { Entries = entries },
                    cancellationToken);

                return entry;
            }
            catch (Exception ex)
            {
                entry = entry with
                {
                    Status = "Failed",
                    Error = ex.Message
                };

                entries[^1] = entry;

                await SaveJournalAsync(
                    journal with { Entries = entries },
                    cancellationToken);

                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> RestoreAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var journal = await LoadJournalAsync(cancellationToken);
            var entries = journal.Entries.ToArray();

            var index = Array.FindIndex(entries, x => x.Id == entryId);

            if (index < 0)
                return false;

            var entry = entries[index];

            if (entry.Status != "Applied" &&
                entry.Status != "Prepared")
                return false;

            ValidateAllowedSetting(entry.SettingPath, entry.ValueName);

            // No sobrescribir cambios posteriores de otro programa.
            var current = await _store.ReadAsync(
                entry.SettingPath,
                entry.ValueName,
                cancellationToken);

            if (entry.Status == "Applied" &&
                current != entry.RequestedValue)
            {
                entries[index] = entry with
                {
                    Status = "Conflict",
                    Error = "El valor actual cambió desde que se aplicó este ajuste."
                };

                await SaveJournalAsync(
                    journal with { Entries = entries },
                    cancellationToken);

                return false;
            }

            if (entry.OriginalValue.Exists)
            {
                await _store.WriteAsync(
                    entry.SettingPath,
                    entry.ValueName,
                    entry.OriginalValue,
                    cancellationToken);
            }
            else
            {
                await _store.DeleteAsync(
                    entry.SettingPath,
                    entry.ValueName,
                    cancellationToken);
            }

            var restored = await _store.ReadAsync(
                entry.SettingPath,
                entry.ValueName,
                cancellationToken);

            if (restored != entry.OriginalValue)
                throw new InvalidOperationException(
                    "La restauración no pudo verificarse.");

            entries[index] = entry with
            {
                Status = "Restored",
                Error = null
            };

            await SaveJournalAsync(
                journal with { Entries = entries },
                cancellationToken);

            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static void ValidateAllowedSetting(string path, string name)
    {
        if (!AllowedSettings.Contains((path, name)))
        {
            throw new InvalidOperationException(
                $"Ajuste fuera de la lista permitida: {path}\\{name}");
        }
    }

    private static void ValidateValue(StoredSetting value)
    {
        if (!value.Exists)
            throw new ArgumentException(
                "El valor solicitado debe existir. Para eliminar un valor se usa el mecanismo de restauración.");

        if (value.Kind != StoredValueKind.DWord)
            throw new ArgumentException(
                "En esta versión solo se permiten valores DWORD.");

        if (!int.TryParse(value.Value, out _))
            throw new ArgumentException("El valor DWORD no es válido.");
    }

    private async Task<RestoreJournal> LoadJournalAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return new RestoreJournal(
                1,
                DateTimeOffset.UtcNow,
                Array.Empty<RestoreEntry>());
        }

        await using var stream = File.OpenRead(_journalPath);

        return await JsonSerializer.DeserializeAsync<RestoreJournal>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? new RestoreJournal(
                1,
                DateTimeOffset.UtcNow,
                Array.Empty<RestoreEntry>());
    }

    private async Task SaveJournalAsync(
        RestoreJournal journal,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_journalPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _journalPath + ".tmp";

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                journal with { UpdatedAt = DateTimeOffset.UtcNow },
                JsonOptions,
                cancellationToken);

            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _journalPath, overwrite: true);
    }

    private sealed class StringTupleComparer :
        IEqualityComparer<(string Path, string Name)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals(
            (string Path, string Name) x,
            (string Path, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Path, string Name) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
