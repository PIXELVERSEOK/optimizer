using System.Text.Json;
using EvoOptimizer.Core.Audit;
using EvoOptimizer.Core.Recovery;
using EvoOptimizer.Infrastructure.Analysis;
using EvoOptimizer.Infrastructure.Benchmarking;
using EvoOptimizer.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;

namespace EvoOptimizer.Infrastructure.History;

public sealed record HistoryRecord(
    Guid Id,
    string Category,
    DateTimeOffset CapturedAt,
    string Payload);

public sealed class EvoHistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public EvoHistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

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

    public Task<Guid> SaveAuditAsync(
        SystemAuditReport report,
        CancellationToken cancellationToken = default) =>
        SaveAsync("SystemAudit", report, report.CapturedAt, cancellationToken);

    public Task<Guid> SaveTelemetryAsync(
        TelemetryCaptureResult result,
        CancellationToken cancellationToken = default) =>
        SaveAsync("Telemetry", result, result.FinishedAt, cancellationToken);

    public Task<Guid> SaveFrametimeAsync(
        FrametimeAnalysisReport report,
        CancellationToken cancellationToken = default) =>
        SaveAsync("Frametime", report, report.AnalyzedAt, cancellationToken);

    public Task<Guid> SaveBenchmarkAsync(
        BenchmarkComparison comparison,
        CancellationToken cancellationToken = default) =>
        SaveAsync("Benchmark", comparison, comparison.ComparedAt, cancellationToken);

    public Task<Guid> SaveRestoreEntryAsync(
        RestoreEntry entry,
        CancellationToken cancellationToken = default) =>
        SaveAsync("Optimization", entry, entry.CreatedAt, cancellationToken);

    public async Task<Guid> SaveAsync<T>(
        string category,
        T data,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(data);

        await EnsureInitializedAsync(cancellationToken);

        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(data, JsonOptions);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO HistoryRecords
                (Id, Category, CapturedAt, Payload)
            VALUES
                ($id, $category, $capturedAt, $payload);
            """;

        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue(
            "$capturedAt",
            capturedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return id;
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetLatestAsync(
        string? category = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = category is null
            ? """
              SELECT Id, Category, CapturedAt, Payload
              FROM HistoryRecords
              ORDER BY CapturedAt DESC
              LIMIT $limit;
              """
            : """
              SELECT Id, Category, CapturedAt, Payload
              FROM HistoryRecords
              WHERE Category = $category
              ORDER BY CapturedAt DESC
              LIMIT $limit;
              """;

        command.Parameters.AddWithValue("$limit", limit);

        if (category is not null)
            command.Parameters.AddWithValue("$category", category);

        var records = new List<HistoryRecord>();

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new HistoryRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetString(3)));
        }

        return records;
    }

    public async Task<T?> GetDataAsync<T>(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT Payload
            FROM HistoryRecords
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", id.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is not string payload)
            return default;

        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public async Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM HistoryRecords
            WHERE CapturedAt < $cutoff;
            """;

        command.Parameters.AddWithValue(
            "$cutoff",
            cutoff.ToUniversalTime().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
                return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();

            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS HistoryRecords
                (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Category TEXT NOT NULL,
                    CapturedAt TEXT NOT NULL,
                    Payload TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_HistoryRecords_Category
                ON HistoryRecords(Category);

                CREATE INDEX IF NOT EXISTS IX_HistoryRecords_CapturedAt
                ON HistoryRecords(CapturedAt DESC);

                CREATE INDEX IF NOT EXISTS IX_HistoryRecords_Category_Date
                ON HistoryRecords(Category, CapturedAt DESC);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
