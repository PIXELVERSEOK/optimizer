using System.Text.Json;
using EvoOptimizer.Core.Benchmark;
using Microsoft.Data.Sqlite;

namespace EvoOptimizer.Infrastructure.Benchmark;

/// <summary>
/// Almacena evidencia que ya fue aceptada por BenchmarkEvidenceGate.
/// No ejecuta ni sustituye la validación de calidad.
/// </summary>
public sealed class SqliteAcceptedBenchmarkEvidenceRepository
{
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public SqliteAcceptedBenchmarkEvidenceRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("La ruta de SQLite está vacía.", nameof(databasePath));

        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS AcceptedBenchmarkEvidence
            (
                Id TEXT PRIMARY KEY,
                GameId TEXT NOT NULL,
                Scenario TEXT NOT NULL,
                Resolution TEXT NOT NULL,
                CaptureSource TEXT NOT NULL,
                CapturedAt TEXT NOT NULL,
                FrameCount INTEGER NOT NULL,
                EvidenceJson TEXT NOT NULL,
                QualityReportJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AcceptedBenchmarkEvidence_Game
            ON AcceptedBenchmarkEvidence(GameId, CapturedAt DESC);

            CREATE INDEX IF NOT EXISTS IX_AcceptedBenchmarkEvidence_Context
            ON AcceptedBenchmarkEvidence(GameId, Scenario, Resolution, CapturedAt DESC);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Debe invocarse únicamente después de que BenchmarkEvidenceGate
    /// haya devuelto el estado Accepted.
    /// </summary>
    public async Task<string> SaveAcceptedAsync(
        BenchmarkEvidence evidence,
        string qualityReportJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(qualityReportJson))
            throw new ArgumentException(
                "El informe de calidad es obligatorio.",
                nameof(qualityReportJson));

        if (evidence.Frames is null || evidence.Frames.Count == 0)
            throw new InvalidDataException("La evidencia no contiene frames.");

        string id = Guid.NewGuid().ToString("N");
        string evidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
        string capturedAt = evidence.CapturedAt.ToString("O");
        string createdAt = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AcceptedBenchmarkEvidence
            (
                Id, GameId, Scenario, Resolution, CaptureSource,
                CapturedAt, FrameCount, EvidenceJson,
                QualityReportJson, CreatedAt
            )
            VALUES
            (
                $id, $game, $scenario, $resolution, $source,
                $captured, $frames, $evidence, $quality, $created
            );
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$game", evidence.GameId);
        command.Parameters.AddWithValue("$scenario", evidence.Scenario);
        command.Parameters.AddWithValue("$resolution", evidence.Resolution);
        command.Parameters.AddWithValue("$source", evidence.CaptureSource);
        command.Parameters.AddWithValue("$captured", capturedAt);
        command.Parameters.AddWithValue("$frames", evidence.Frames.Count);
        command.Parameters.AddWithValue("$evidence", evidenceJson);
        command.Parameters.AddWithValue("$quality", qualityReportJson);
        command.Parameters.AddWithValue("$created", createdAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<BenchmarkEvidence?> GetEvidenceAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EvidenceJson
            FROM AcceptedBenchmarkEvidence
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is not string json)
            return null;

        return JsonSerializer.Deserialize<BenchmarkEvidence>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<AcceptedBenchmarkEvidenceSummary>> GetLatestAsync(
        int limit = 50,
        string? gameId = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GameId, Scenario, Resolution, CaptureSource,
                   CapturedAt, FrameCount, CreatedAt
            FROM AcceptedBenchmarkEvidence
            WHERE ($game IS NULL OR GameId = $game)
            ORDER BY CapturedAt DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$game", (object?)gameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AcceptedBenchmarkEvidenceSummary>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AcceptedBenchmarkEvidenceSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public async Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM AcceptedBenchmarkEvidence WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

public sealed record AcceptedBenchmarkEvidenceSummary(
    string Id,
    string GameId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    DateTimeOffset CapturedAt,
    int FrameCount,
    DateTimeOffset CreatedAt);