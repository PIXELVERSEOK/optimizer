using EvoOptimizer.Core.Audit;

namespace EvoOptimizer.Infrastructure.History;

public sealed record HistoryCategorySummary(
    string Category,
    int Count,
    DateTimeOffset? LastActivity);

public sealed record HistoryDashboardSnapshot(
    DateTimeOffset GeneratedAt,
    int TotalRecords,
    int Last24Hours,
    int Last7Days,
    IReadOnlyList<HistoryCategorySummary> Categories,
    IReadOnlyList<HistoryRecord> RecentRecords);

public sealed class HistoryDashboardService
{
    private readonly EvoHistoryStore _historyStore;

    public HistoryDashboardService(EvoHistoryStore historyStore)
    {
        _historyStore = historyStore
            ?? throw new ArgumentNullException(nameof(historyStore));
    }

    public async Task<HistoryDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Se consultan hasta 10.000 registros recientes para construir
        // un resumen local sin modificar el historial.
        var records = await _historyStore.GetLatestAsync(
            limit: 10000,
            cancellationToken: cancellationToken);

        var last24Hours = now.AddHours(-24);
        var last7Days = now.AddDays(-7);

        var categories = records
            .GroupBy(record => record.Category)
            .Select(group => new HistoryCategorySummary(
                group.Key,
                group.Count(),
                group.Max(record => record.CapturedAt)))
            .OrderByDescending(item => item.Count)
            .ToArray();

        var recentRecords = records
            .Take(20)
            .ToArray();

        return new HistoryDashboardSnapshot(
            GeneratedAt: now,
            TotalRecords: records.Count,
            Last24Hours: records.Count(
                record => record.CapturedAt >= last24Hours),
            Last7Days: records.Count(
                record => record.CapturedAt >= last7Days),
            Categories: categories,
            RecentRecords: recentRecords);
    }

    public Task<Guid> SaveAuditAsync(
        SystemAuditReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return _historyStore.SaveAuditAsync(report, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryRecord>> LoadCategoryAsync(
        string category,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return await _historyStore.GetLatestAsync(
            category,
            limit,
            cancellationToken);
    }

    public async Task<T?> LoadRecordAsync<T>(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _historyStore.GetDataAsync<T>(
            id,
            cancellationToken);
    }

    public async Task<int> CleanHistoryAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        return await _historyStore.DeleteOlderThanAsync(
            olderThan,
            cancellationToken);
    }
}
