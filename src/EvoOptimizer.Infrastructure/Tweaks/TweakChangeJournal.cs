using System.Text.Json;
using System.Text.Json.Serialization;
using EvoOptimizer.Core.Tweaks;

namespace EvoOptimizer.Infrastructure.Tweaks;

/// <summary>
/// Diario write-ahead de tweaks de registro. Guarda el valor original ANTES de
/// escribir, con tmp + File.Move para que un cierre inesperado no corrompa el
/// historial de rollback.
/// </summary>
public sealed class TweakChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _journalPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public TweakChangeJournal(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        _journalPath = Path.GetFullPath(journalPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EvoFpsOptimizer",
        "tweaks-changes.json");

    public async Task<IReadOnlyList<TweakChange>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<TweakChange>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TweakChange> entries = await GetAllAsync(cancellationToken);

        return entries.Where(entry => entry.IsActive).ToArray();
    }

    public async Task AppendAsync(
        TweakChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();

            entries.Add(change);

            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpdateAsync(
        TweakChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            int index = entries.FindIndex(entry => entry.Id == change.Id);

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"No existe un cambio con Id {change.Id} en el diario de tweaks.");
            }

            entries[index] = change;

            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<IReadOnlyList<TweakChange>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return Array.Empty<TweakChange>();

        await using var stream = File.OpenRead(_journalPath);

        return await JsonSerializer.DeserializeAsync<List<TweakChange>>(
            stream,
            JsonOptions,
            cancellationToken) ?? new List<TweakChange>();
    }

    private async Task SaveAsync(
        IReadOnlyList<TweakChange> entries,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_journalPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _journalPath + ".tmp";

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _journalPath, overwrite: true);
    }
}
