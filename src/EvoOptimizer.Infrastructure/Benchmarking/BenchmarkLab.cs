using System.Text.Json;
using EvoOptimizer.Infrastructure.Analysis;

namespace EvoOptimizer.Infrastructure.Benchmarking;

public enum BenchmarkVerdict
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    Inconclusive
}

public sealed record BenchmarkMetric(
    string Name,
    double Baseline,
    double Candidate,
    double AbsoluteChange,
    double PercentChange,
    bool HigherIsBetter,
    string Assessment);

public sealed record BenchmarkComparison(
    Guid Id,
    DateTimeOffset ComparedAt,
    string BaselineName,
    string CandidateName,
    BenchmarkVerdict Verdict,
    IReadOnlyList<BenchmarkMetric> Metrics,
    IReadOnlyList<string> Warnings,
    bool IsComparable);

public sealed class BenchmarkLab
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public BenchmarkComparison Compare(
        string baselineName,
        FrametimeAnalysisReport baseline,
        string candidateName,
        FrametimeAnalysisReport candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateName);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var warnings = new List<string>();

        if (baseline.FrameCount < 1000 || candidate.FrameCount < 1000)
            warnings.Add("Una o ambas capturas tienen menos de 1000 frames.");

        if (baseline.DurationSeconds < 30 || candidate.DurationSeconds < 30)
            warnings.Add("Una o ambas capturas duran menos de 30 segundos.");

        if (baseline.DurationSeconds > 0 &&
            candidate.DurationSeconds > 0)
        {
            var durationDifference = Math.Abs(
                baseline.DurationSeconds - candidate.DurationSeconds) /
                Math.Max(baseline.DurationSeconds, candidate.DurationSeconds);

            if (durationDifference > 0.20)
                warnings.Add(
                    "Las duraciones difieren más del 20%; la comparación puede estar sesgada.");
        }

        if (baseline.SourceColumn != candidate.SourceColumn)
            warnings.Add(
                "Las capturas utilizan columnas de frametime diferentes.");

        var metrics = new List<BenchmarkMetric>
        {
            CreateMetric("FPS promedio",
                baseline.AverageFps, candidate.AverageFps, true),

            CreateMetric("1% Low",
                baseline.OnePercentLowFps, candidate.OnePercentLowFps, true),

            CreateMetric("0.1% Low",
                baseline.PointOnePercentLowFps,
                candidate.PointOnePercentLowFps, true),

            CreateMetric("Frametime medio (ms)",
                baseline.AverageFrametimeMs,
                candidate.AverageFrametimeMs, false),

            CreateMetric("Frametime P99 (ms)",
                baseline.P99FrametimeMs,
                candidate.P99FrametimeMs, false),

            CreateMetric("Frametime máximo (ms)",
                baseline.MaximumFrametimeMs,
                candidate.MaximumFrametimeMs, false),

            CreateMetric("Variabilidad (ms)",
                baseline.FrametimeStandardDeviationMs,
                candidate.FrametimeStandardDeviationMs, false),

            CreateMetric("Tasa de stutters (%)",
                baseline.StutterRatePercent,
                candidate.StutterRatePercent, false),

            CreateMetric("Frames descartados",
                baseline.DroppedFrameCount,
                candidate.DroppedFrameCount, false)
        };

        var comparable = warnings.Count == 0;

        var improved = metrics.Count(x => x.Assessment == "Mejora");
        var regressed = metrics.Count(x => x.Assessment == "Empeora");
        var unchanged = metrics.Count(x => x.Assessment == "Sin cambio");

        var verdict = !comparable
            ? BenchmarkVerdict.Inconclusive
            : improved > 0 && regressed == 0
                ? BenchmarkVerdict.Improved
                : regressed > 0 && improved == 0
                    ? BenchmarkVerdict.Regressed
                    : improved > 0 && regressed > 0
                        ? BenchmarkVerdict.Mixed
                        : unchanged == metrics.Count
                            ? BenchmarkVerdict.Unchanged
                            : BenchmarkVerdict.Mixed;

        return new BenchmarkComparison(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            baselineName,
            candidateName,
            verdict,
            metrics,
            warnings,
            comparable);
    }

    public async Task<string> ExportAsync(
        BenchmarkComparison comparison,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        var fileName = $"benchmark-{comparison.Id:N}.json";
        var path = Path.Combine(outputDirectory, fileName);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true);

        await JsonSerializer.SerializeAsync(
            stream,
            comparison,
            JsonOptions,
            cancellationToken);

        return path;
    }

    private static BenchmarkMetric CreateMetric(
        string name,
        double baseline,
        double candidate,
        bool higherIsBetter)
    {
        var change = candidate - baseline;

        var percentChange = Math.Abs(baseline) < 0.000001
            ? 0
            : change / Math.Abs(baseline) * 100d;

        var improvement = higherIsBetter ? change : -change;

        var assessment = Math.Abs(percentChange) < 1d
            ? "Sin cambio"
            : improvement > 0
                ? "Mejora"
                : "Empeora";

        return new BenchmarkMetric(
            name,
            baseline,
            candidate,
            change,
            percentChange,
            higherIsBetter,
            assessment);
    }
}
