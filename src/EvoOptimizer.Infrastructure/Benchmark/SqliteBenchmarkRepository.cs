using System.Text.Json;
using EvoOptimizer.Core.Benchmark;
using Microsoft.Data.Sqlite;

namespace EvoOptimizer.Infrastructure.Benchmark;

public sealed class SqliteBenchmarkRepository
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SqliteBenchmarkRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.");

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);

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

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS BenchmarkExperiments (
                Id TEXT PRIMARY KEY NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                Status TEXT NOT NULL,
                Payload TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_BenchmarkExperiments_UpdatedAt
            ON BenchmarkExperiments(UpdatedAt);

            CREATE TABLE IF NOT EXISTS BenchmarkEffectiveness (
                Id TEXT PRIMARY KEY NOT NULL,
                OptimizationId TEXT NOT NULL,
                EvaluatedAt TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                Payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BenchmarkLearningCandidates (
                Id TEXT PRIMARY KEY NOT NULL,
                CreatedAt TEXT NOT NULL,
                Payload TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_BenchmarkLearningCandidates_CreatedAt
            ON BenchmarkLearningCandidates(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_BenchmarkEffectiveness_Optimization
            ON BenchmarkEffectiveness(OptimizationId, EvaluatedAt);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveExperimentAsync(
        BenchmarkExperiment experiment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        var id = experiment.Definition.Id.ToString("D");
        var createdAt = experiment.Definition.CreatedAt.ToString("O");
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        var status = experiment.Status.ToString();
        var payload = JsonSerializer.Serialize(experiment);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO BenchmarkExperiments
                (Id, CreatedAt, UpdatedAt, Status, Payload)
            VALUES
                ($id, $createdAt, $updatedAt, $status, $payload)
            ON CONFLICT(Id) DO UPDATE SET
                UpdatedAt = excluded.UpdatedAt,
                Status = excluded.Status,
                Payload = excluded.Payload;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BenchmarkExperiment>>
        GetExperimentsAsync(
            int limit = 100,
            CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT Payload
            FROM BenchmarkExperiments
            ORDER BY UpdatedAt DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<BenchmarkExperiment>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(0);

            var item = JsonSerializer.Deserialize<BenchmarkExperiment>(
                payload, _jsonOptions);

            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    public async Task SaveEffectivenessAsync(
        OptimizationEffectivenessObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO BenchmarkEffectiveness
                (Id, OptimizationId, EvaluatedAt, Outcome, Payload)
            VALUES
                ($id, $optimizationId, $evaluatedAt, $outcome, $payload)
            ON CONFLICT(Id) DO NOTHING;
            """;

        command.Parameters.AddWithValue(
            "$id", observation.Id.ToString("D"));

        command.Parameters.AddWithValue(
            "$optimizationId", observation.OptimizationId);

        command.Parameters.AddWithValue(
            "$evaluatedAt", observation.EvaluatedAt.ToString("O"));

        command.Parameters.AddWithValue(
            "$outcome", observation.Outcome.ToString());

        command.Parameters.AddWithValue(
            "$payload", JsonSerializer.Serialize(observation));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OptimizationEffectivenessObservation>>
        GetEffectivenessAsync(
            string optimizationId,
            int limit = 500,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(optimizationId))
            throw new ArgumentException("Optimization ID is required.");

        limit = Math.Clamp(limit, 1, 10000);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT Payload
            FROM BenchmarkEffectiveness
            WHERE OptimizationId = $optimizationId
            ORDER BY EvaluatedAt DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$optimizationId", optimizationId);
        command.Parameters.AddWithValue("$limit", limit);

        var results =
            new List<OptimizationEffectivenessObservation>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var item =
                JsonSerializer.Deserialize<
                    OptimizationEffectivenessObservation>(
                        reader.GetString(0), _jsonOptions);

            if (item is not null)
                results.Add(item);
        }

        return results;
    }


    public async Task SaveLearningCandidateAsync(
        BenchmarkLearningCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var payload = JsonSerializer.Serialize(candidate);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO BenchmarkLearningCandidates
                (Id, CreatedAt, Payload)
            VALUES
                ($id, $createdAt, $payload)
            ON CONFLICT(Id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$id", candidate.Id.ToString("D"));
        command.Parameters.AddWithValue("$createdAt", candidate.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveExperimentRunAsync(
        BenchmarkExperiment experiment,
        BenchmarkExperimentRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(run);

        if (run.ExperimentId != experiment.Definition.Id ||
            !experiment.Runs.Any(x => x.Id == run.Id))
        {
            throw new InvalidOperationException(
                "La ejecución no pertenece al experimento indicado.");
        }

        // El objeto experimento ya contiene sus ejecuciones.
        // Se persiste el agregado completo en su registro existente.
        await SaveExperimentAsync(experiment, cancellationToken);
    }

    public async Task<BenchmarkExperiment?> GetExperimentAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT Payload
            FROM BenchmarkExperiments
            WHERE Id = $id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$id", experimentId.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is null || result is DBNull)
            return null;

        return JsonSerializer.Deserialize<BenchmarkExperiment>(
            Convert.ToString(result)!,
            _jsonOptions);
    }
    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
