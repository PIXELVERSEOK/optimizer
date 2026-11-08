using EvoOptimizer.Infrastructure.Analysis;

namespace EvoOptimizer.Core.Tests;

public sealed class StartupBackgroundAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_DoesNotInferStartupImpactFromProductName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        StartupAnalysisReport report =
            await new StartupBackgroundAnalyzer().AnalyzeAsync();

        Assert.All(
            report.StartupItems,
            item => Assert.Equal(StartupImpact.Unknown, item.Impact));
        Assert.All(
            report.StartupItems,
            item => Assert.Contains("no medido", item.Recommendation, StringComparison.OrdinalIgnoreCase));
    }
}
