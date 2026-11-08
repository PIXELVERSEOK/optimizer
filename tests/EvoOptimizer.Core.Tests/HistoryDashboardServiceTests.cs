using EvoOptimizer.Core.Audit;
using EvoOptimizer.Infrastructure.History;
using Microsoft.Data.Sqlite;

namespace EvoOptimizer.Core.Tests;

public sealed class HistoryDashboardServiceTests
{
    [Fact]
    public async Task LoadAsyncBuildsSummaryFromPersistedRecords()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"EvoHistoryDashboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var store = new EvoHistoryStore(Path.Combine(directory, "history.db"));
            var now = DateTimeOffset.UtcNow;
            await store.SaveAsync("Optimization", new { Name = "recent" }, now);
            await store.SaveAsync("SystemAudit", new { Name = "yesterday" }, now.AddHours(-25));
            await store.SaveAsync("Optimization", new { Name = "old" }, now.AddDays(-8));

            var service = new HistoryDashboardService(store);
            HistoryDashboardSnapshot snapshot = await service.LoadAsync();

            Assert.Equal(3, snapshot.TotalRecords);
            Assert.Equal(1, snapshot.Last24Hours);
            Assert.Equal(2, snapshot.Last7Days);
            Assert.Equal(3, snapshot.RecentRecords.Count);
            Assert.Equal(
                new[] { "Optimization", "SystemAudit" },
                snapshot.Categories
                    .OrderBy(category => category.Category, StringComparer.Ordinal)
                    .Select(category => category.Category));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAuditAsyncMakesReadOnlyAuditVisibleInHistory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"EvoAuditHistory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var service = new HistoryDashboardService(
                new EvoHistoryStore(Path.Combine(directory, "history.db")));
            var audit = new SystemAuditReport(
                DateTimeOffset.UtcNow,
                "TEST-PC",
                [new AuditFact("CPU", "Name", "Test CPU", "Test", "High")],
                Array.Empty<string>());

            await service.SaveAuditAsync(audit);
            HistoryDashboardSnapshot snapshot = await service.LoadAsync();

            HistoryRecord record = Assert.Single(snapshot.RecentRecords);
            Assert.Equal("SystemAudit", record.Category);
            Assert.Contains("Test CPU", record.Payload, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
