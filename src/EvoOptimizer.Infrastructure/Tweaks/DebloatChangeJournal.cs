using System.Text.Json;
using System.Text.Json.Serialization;
using EvoOptimizer.Core.Tweaks;

namespace EvoOptimizer.Infrastructure.Tweaks;

/// <summary>
/// Diario de aplicaciones UWP quitadas. Guarda PackageFullName e InstallLocation
/// para poder volver a registrar la app con Add-AppxPackage -Register.
/// </summary>
public sealed class DebloatChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _journalPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DebloatChangeJournal(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        _journalPath = Path.GetFullPath(journalPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EvoFpsOptimizer",
        "debloat-changes.json");

    public async Task<IReadOnlyList<DebloatChange>> GetAllAsync(
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

    /// <summary>Cambios que siguen quitados del sistema (pendientes o confirmados).</summary>
    public async Task<IReadOnlyList<DebloatChange>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DebloatChange> entries = await GetAllAsync(cancellationToken);

        return entries
            .Where(entry => entry.Status is DebloatChangeStatus.Pending or DebloatChangeStatus.Removed)
            .ToArray();
    }

    public async Task AppendAsync(
        DebloatChange change,
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
        DebloatChange change,
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
                    $"No existe un cambio con Id {change.Id} en el diario de debloat.");
            }

            entries[index] = change;

            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<IReadOnlyList<DebloatChange>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return Array.Empty<DebloatChange>();

        await using var stream = File.OpenRead(_journalPath);

        return await JsonSerializer.DeserializeAsync<List<DebloatChange>>(
            stream,
            JsonOptions,
            cancellationToken) ?? new List<DebloatChange>();
    }

    private async Task SaveAsync(
        IReadOnlyList<DebloatChange> entries,
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
