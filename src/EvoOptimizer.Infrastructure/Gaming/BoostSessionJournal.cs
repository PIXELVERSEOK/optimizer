using System.Text.Json;
using System.Text.Json.Serialization;
using EvoOptimizer.Core.Gaming;

namespace EvoOptimizer.Infrastructure.Gaming;

/// <summary>
/// Diario persistente de la sesión de Boost. Escritura atómica (tmp + move)
/// ANTES de cada modificación del sistema: si EVO se cierra inesperadamente,
/// la sesión sigue en disco con todo lo necesario para restaurar el equipo.
/// </summary>
public sealed class BoostSessionJournal
{
    private readonly string _journalPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BoostSessionJournal(string? journalPath = null)
    {
        _journalPath = journalPath ?? DefaultJournalPath();
        string? directory = Path.GetDirectoryName(_journalPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public static string DefaultJournalPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EvoFpsOptimizer",
        "boost-session.json");

    public string JournalPath => _journalPath;

    public async Task<BoostSession?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(
        BoostSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!BoostSessionRules.IsValid(session))
            throw new ArgumentException("La sesión de Boost contiene datos inválidos.", nameof(session));

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            string temporary = _journalPath + ".tmp";
            string json = JsonSerializer.Serialize(session, JsonOptions);

            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _journalPath, overwrite: true);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<BoostSession?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return null;

        try
        {
            string json = await File.ReadAllTextAsync(_journalPath, cancellationToken);
            var session = JsonSerializer.Deserialize<BoostSession>(json, JsonOptions);

            return session is not null && BoostSessionRules.IsValid(session)
                ? session
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}