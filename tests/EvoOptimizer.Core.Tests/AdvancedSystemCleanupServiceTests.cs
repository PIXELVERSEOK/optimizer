using EvoOptimizer.Infrastructure.Cleanup;

namespace EvoOptimizer.Core.Tests;

public sealed class AdvancedSystemCleanupServiceTests
{
    [Fact]
    public void IsPathWithinRoot_AcceptsDescendantAndRejectsRootItself()
    {
        string root = Path.Combine(Path.GetTempPath(), "EvoCleanupRoot");
        string child = Path.Combine(root, "nested", "cache.bin");

        Assert.True(AdvancedSystemCleanupService.IsPathWithinRoot(child, root));
        Assert.False(AdvancedSystemCleanupService.IsPathWithinRoot(root, root));
    }

    [Fact]
    public void IsPathWithinRoot_RejectsSiblingWithSharedPrefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "EvoCleanupRoot");
        string sibling = Path.Combine(Path.GetTempPath(), "EvoCleanupRoot-Outside", "cache.bin");

        Assert.False(AdvancedSystemCleanupService.IsPathWithinRoot(sibling, root));
    }

    [Theory]
    [InlineData("", "C:\\Temp")]
    [InlineData("C:\\Temp\\file.tmp", "")]
    public void IsPathWithinRoot_RejectsEmptyPaths(string candidate, string root) =>
        Assert.False(AdvancedSystemCleanupService.IsPathWithinRoot(candidate, root));

    [Fact]
    public async Task CleanAsync_RejectsAgeBelowSafeMinimum()
    {
        var service = new AdvancedSystemCleanupService(Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "history.jsonl"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CleanAsync([CleanupCategoryId.UserTemporaryFiles], TimeSpan.FromHours(12)));
    }

    [Fact]
    public async Task CleanAsync_RequiresAtLeastOneSelectedCategory()
    {
        var service = new AdvancedSystemCleanupService(Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "history.jsonl"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CleanAsync([], TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task ScanAsync_ReportsRealCategoriesAndKeepsRegistryReadOnly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new AdvancedSystemCleanupService(Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "history.jsonl"));

        CleanupScanReport report = await service.ScanAsync();

        Assert.Equal(Enum.GetValues<CleanupCategoryId>().Length, report.Categories.Count);
        Assert.Equal(report.Categories.Sum(category => category.FileCount), report.FileCount);
        Assert.Equal(report.Categories.Sum(category => category.EstimatedBytes), report.EstimatedBytes);
        Assert.Equal(
            report.Categories.SelectMany(category => category.Files).Count(),
            report.Categories.SelectMany(category => category.Files)
                .Select(file => file.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        CleanupCategoryScanResult registry = Assert.Single(report.Categories,
            category => category.Id == CleanupCategoryId.RegistryDiagnostics);
        Assert.False(registry.IsCleanable);
        Assert.False(registry.IsAvailable);
        Assert.Contains("No existe un verificador", registry.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_MakesUnavailableCleanableCategoriesUnselectable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new AdvancedSystemCleanupService(Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "history.jsonl"));

        CleanupScanReport report = await service.ScanAsync();

        Assert.All(
            report.Categories.Where(category => !category.IsAvailable && category.IsCleanable),
            category => Assert.Empty(category.Files));
    }

    [Fact]
    public void TryDeleteCandidate_DeletesOnlyTheValidatedTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(
            Path.GetTempPath(),
            "EvoCleanupTest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "old-cache.tmp");

        try
        {
            File.WriteAllBytes(path, new byte[8192]);
            DateTime oldWriteTime = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(path, oldWriteTime);

            var candidate = new CleanupFileCandidate(
                path,
                new FileInfo(path).Length,
                oldWriteTime);
            bool deleted = AdvancedSystemCleanupService.TryDeleteCandidate(
                candidate,
                [new AdvancedSystemCleanupService.DirectoryTarget(root)],
                DateTime.UtcNow.AddDays(-1),
                out long allocatedBytes,
                out string detail);

            Assert.True(deleted, detail);
            Assert.False(File.Exists(path));
            Assert.True(allocatedBytes > 0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
