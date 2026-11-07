using Microsoft.Data.Sqlite;

namespace EvoOptimizer.Infrastructure.Benchmark;

public sealed record StoredBenchmarkBaseline(
    string Id,
    string GameId,
    string Scenario,
    string Resolution,
    string HardwareId,
    string EnvironmentId,
    string EvidenceId,
    string MetricsJson,
    DateTimeOffset CreatedAt,
    bool IsActive);

public sealed class SqliteBenchmarkBaselineRepository
{
    private readonly string _connectionString;

    public SqliteBenchmarkBaselineRepository(string databasePath)
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

            CREATE TABLE IF NOT EXISTS BenchmarkBaselines
            (
                Id TEXT PRIMARY KEY,
                GameId TEXT NOT NULL,
                Scenario TEXT NOT NULL,
                Resolution TEXT NOT NULL,
                HardwareId TEXT NOT NULL,
                EnvironmentId TEXT NOT NULL,
                EvidenceId TEXT NOT NULL,
                MetricsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                IsActive INTEGER NOT NULL CHECK(IsActive IN (0, 1))
            );

            CREATE INDEX IF NOT EXISTS IX_BenchmarkBaselines_Context
            ON BenchmarkBaselines
            (
                GameId, Scenario, Resolution,
                HardwareId, EnvironmentId, IsActive
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Promueve una referencia nueva. Requiere aprobación explícita del usuario
    /// y desactiva el baseline anterior del mismo contexto en una transacción.
    /// </summary>
    public async Task<StoredBenchmarkBaseline> PromoteAsync(
        string gameId,
        string scenario,
        string resolution,
        string hardwareId,
        string environmentId,
        string evidenceId,
        string metricsJson,
        bool userApproved,
        CancellationToken cancellationToken = default)
    {
        Validate(gameId, nameof(gameId));
        Validate(scenario, nameof(scenario));
        Validate(resolution, nameof(resolution));
        Validate(hardwareId, nameof(hardwareId));
        Validate(environmentId, nameof(environmentId));
        Validate(evidenceId, nameof(evidenceId));
        Validate(metricsJson, nameof(metricsJson));

        if (!userApproved)
            throw new InvalidOperationException(
                "La promoción del baseline requiere aprobación explícita.");

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // No permitir baselines sin evidencia aceptada persistida.
            await using (var evidenceCheck = connection.CreateCommand())
            {
                evidenceCheck.Transaction = (SqliteTransaction)transaction;
                evidenceCheck.CommandText = """
                    SELECT EXISTS(
                        SELECT 1
                        FROM AcceptedBenchmarkEvidence
                        WHERE Id = $evidenceId
                    );
                    """;

                evidenceCheck.Parameters.AddWithValue("$evidenceId", evidenceId);

                object? exists = await evidenceCheck.ExecuteScalarAsync(cancellationToken);

                if (Convert.ToInt32(exists) != 1)
                    throw new InvalidOperationException(
                        "No se puede promover el baseline: la evidencia no existe en el historial aceptado.");
            }

            await using (var deactivate = connection.CreateCommand())
            {
                deactivate.Transaction = (SqliteTransaction)transaction;
                deactivate.CommandText = """
                    UPDATE BenchmarkBaselines
                    SET IsActive = 0
                    WHERE GameId = $game
                      AND Scenario = $scenario
                      AND Resolution = $resolution
                      AND HardwareId = $hardware
                      AND EnvironmentId = $environment
                      AND IsActive = 1;
                    """;

                deactivate.Parameters.AddWithValue("$game", gameId);
                deactivate.Parameters.AddWithValue("$scenario", scenario);
                deactivate.Parameters.AddWithValue("$resolution", resolution);
                deactivate.Parameters.AddWithValue("$hardware", hardwareId);
                deactivate.Parameters.AddWithValue("$environment", environmentId);

                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO BenchmarkBaselines
                    (
                        Id, GameId, Scenario, Resolution,
                        HardwareId, EnvironmentId, EvidenceId,
                        MetricsJson, CreatedAt, IsActive
                    )
                    VALUES
                    (
                        $id, $game, $scenario, $resolution,
                        $hardware, $environment, $evidence,
                        $metrics, $created, 1
                    );
                    """;

                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$game", gameId);
                insert.Parameters.AddWithValue("$scenario", scenario);
                insert.Parameters.AddWithValue("$resolution", resolution);
                insert.Parameters.AddWithValue("$hardware", hardwareId);
                insert.Parameters.AddWithValue("$environment", environmentId);
                insert.Parameters.AddWithValue("$evidence", evidenceId);
                insert.Parameters.AddWithValue("$metrics", metricsJson);
                insert.Parameters.AddWithValue("$created", createdAt.ToString("O"));

                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new StoredBenchmarkBaseline(
            id, gameId, scenario, resolution, hardwareId,
            environmentId, evidenceId, metricsJson, createdAt, true);
    }

    public async Task<StoredBenchmarkBaseline?> GetActiveAsync(
        string gameId,
        string scenario,
        string resolution,
        string hardwareId,
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GameId, Scenario, Resolution, HardwareId,
                   EnvironmentId, EvidenceId, MetricsJson, CreatedAt, IsActive
            FROM BenchmarkBaselines
            WHERE GameId = $game
              AND Scenario = $scenario
              AND Resolution = $resolution
              AND HardwareId = $hardware
              AND EnvironmentId = $environment
              AND IsActive = 1
            ORDER BY CreatedAt DESC
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$scenario", scenario);
        command.Parameters.AddWithValue("$resolution", resolution);
        command.Parameters.AddWithValue("$hardware", hardwareId);
        command.Parameters.AddWithValue("$environment", environmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadBaseline(reader);
    }

    public async Task<IReadOnlyList<StoredBenchmarkBaseline>> GetHistoryAsync(
        string gameId,
        string scenario,
        string resolution,
        string hardwareId,
        string environmentId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GameId, Scenario, Resolution, HardwareId,
                   EnvironmentId, EvidenceId, MetricsJson, CreatedAt, IsActive
            FROM BenchmarkBaselines
            WHERE GameId = $game
              AND Scenario = $scenario
              AND Resolution = $resolution
              AND HardwareId = $hardware
              AND EnvironmentId = $environment
            ORDER BY CreatedAt DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$scenario", scenario);
        command.Parameters.AddWithValue("$resolution", resolution);
        command.Parameters.AddWithValue("$hardware", hardwareId);
        command.Parameters.AddWithValue("$environment", environmentId);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<StoredBenchmarkBaseline>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadBaseline(reader));

        return results;
    }

    private static StoredBenchmarkBaseline ReadBaseline(SqliteDataReader reader)
    {
        return new StoredBenchmarkBaseline(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.GetInt32(9) == 1);
    }

    private static void Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("El valor es obligatorio.", parameterName);
    }
}