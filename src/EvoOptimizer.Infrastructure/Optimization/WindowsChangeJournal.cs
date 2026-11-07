using System.Text.Json;
using EvoOptimizer.Core.Optimization;

namespace EvoOptimizer.Infrastructure.Optimization;

/// <summary>
/// Diario de cambios de Windows. Persiste el estado original antes de tocar el
/// sistema (write-ahead) y permite reconstruir la operación de rollback aunque
/// la aplicación se cierre entre pasos.
/// </summary>
public sealed class WindowsChangeJournal
{
    private readonly string _journalPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public WindowsChangeJournal(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        _journalPath = Path.GetFullPath(journalPath);
    }

    public async Task<IReadOnlyList<WindowsOptimizationChange>> GetAllAsync(
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

    public async Task AppendAsync(
        WindowsOptimizationChange change,
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
        WindowsOptimizationChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            int index = entries.FindIndex(x => x.Id == change.Id);

            if (index < 0)
                throw new InvalidOperationException(
                    $"No existe un cambio con Id {change.Id} en el diario.");

            entries[index] = change;

            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<WindowsOptimizationChange>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await GetAllAsync(cancellationToken);

        return entries
            .Where(x => x.Status is WindowsOptimizationStatus.Pending
                or WindowsOptimizationStatus.Applied
                or WindowsOptimizationStatus.Verified
                or WindowsOptimizationStatus.PerformanceVerified
                or WindowsOptimizationStatus.PerformanceInconclusive)
            .ToArray();
    }

    private async Task<IReadOnlyList<WindowsOptimizationChange>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return Array.Empty<WindowsOptimizationChange>();

        await using var stream = File.OpenRead(_journalPath);

        return await JsonSerializer.DeserializeAsync<List<WindowsOptimizationChange>>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? new List<WindowsOptimizationChange>();
    }

    private async Task SaveAsync(
        IReadOnlyList<WindowsOptimizationChange> entries,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_journalPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = _journalPath + ".tmp";

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
                entries,
                JsonOptions,
                cancellationToken);

            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _journalPath, overwrite: true);
    }
}
