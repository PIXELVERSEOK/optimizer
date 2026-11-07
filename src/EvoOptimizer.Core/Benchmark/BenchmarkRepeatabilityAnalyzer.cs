namespace EvoOptimizer.Core.Benchmark;

public sealed record BenchmarkRunResult(
    string GameId,
    string HardwareId,
    string EnvironmentId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    DateTimeOffset CapturedAt,
    double AverageFps,
    double OnePercentLow,
    double PointOnePercentLow,
    double FrametimeP99Ms,
    double StutterRate,
    BenchmarkConfidenceReport Confidence);

public enum BenchmarkRepeatabilityLevel
{
    InsufficientRuns,
    Inconsistent,
    Acceptable,
    HighlyRepeatable
}

public sealed record BenchmarkRepeatabilityReport(
    BenchmarkRepeatabilityLevel Level,
    int RunCount,
    double AverageFpsVariationPercent,
    double OnePercentLowVariationPercent,
    double PointOnePercentLowVariationPercent,
    double P99VariationPercent,
    double StutterRateRange,
    bool SameContext,
    IReadOnlyList<string> Warnings,
    DateTimeOffset AnalyzedAt);

public sealed class BenchmarkRepeatabilityAnalyzer
{
    public BenchmarkRepeatabilityReport Evaluate(
        IReadOnlyList<BenchmarkRunResult> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var warnings = new List<string>();

        if (runs.Count < 3)
        {
            warnings.Add("Se necesitan al menos tres ejecuciones.");

            return Empty(
                BenchmarkRepeatabilityLevel.InsufficientRuns,
                runs.Count,
                warnings);
        }

        if (runs.Any(x => x.Confidence.Score < 55))
        {
            warnings.Add("Hay capturas con confianza insuficiente.");

            return Empty(
                BenchmarkRepeatabilityLevel.Inconsistent,
                runs.Count,
                warnings);
        }

        var first = runs[0];

        bool sameContext = runs.All(x =>
            Same(first.GameId, x.GameId) &&
            Same(first.HardwareId, x.HardwareId) &&
            Same(first.EnvironmentId, x.EnvironmentId) &&
            Same(first.Scenario, x.Scenario) &&
            Same(first.Resolution, x.Resolution) &&
            Same(first.CaptureSource, x.CaptureSource));

        if (!sameContext)
        {
            warnings.Add("Las ejecuciones pertenecen a contextos diferentes.");

            return Empty(
                BenchmarkRepeatabilityLevel.Inconsistent,
                runs.Count,
                warnings);
        }

        var avgVariation = CoefficientOfVariation(
            runs.Select(x => x.AverageFps));

        var lowVariation = CoefficientOfVariation(
            runs.Select(x => x.OnePercentLow));

        var low01Variation = CoefficientOfVariation(
            runs.Select(x => x.PointOnePercentLow));

        var p99Variation = CoefficientOfVariation(
            runs.Select(x => x.FrametimeP99Ms));

        var stutterValues = runs.Select(x => x.StutterRate).ToArray();

        var stutterRange =
            stutterValues.Max() - stutterValues.Min();

        if (avgVariation > 0.08)
            warnings.Add("FPS promedio poco consistente.");

        if (lowVariation > 0.12)
            warnings.Add("1% Low poco consistente.");

        if (low01Variation > 0.18)
            warnings.Add("0.1% Low presenta variación elevada.");

        if (p99Variation > 0.15)
            warnings.Add("P99 de frametime poco consistente.");

        if (stutterRange > 3.0)
            warnings.Add("La tasa de stutters varía considerablemente.");

        bool acceptable =
            avgVariation <= 0.08 &&
            lowVariation <= 0.12 &&
            low01Variation <= 0.18 &&
            p99Variation <= 0.15 &&
            stutterRange <= 3.0;

        bool highlyRepeatable =
            avgVariation <= 0.04 &&
            lowVariation <= 0.06 &&
            low01Variation <= 0.10 &&
            p99Variation <= 0.08 &&
            stutterRange <= 1.5;

        var level = highlyRepeatable
            ? BenchmarkRepeatabilityLevel.HighlyRepeatable
            : acceptable
                ? BenchmarkRepeatabilityLevel.Acceptable
                : BenchmarkRepeatabilityLevel.Inconsistent;

        return new BenchmarkRepeatabilityReport(
            level,
            runs.Count,
            avgVariation * 100,
            lowVariation * 100,
            low01Variation * 100,
            p99Variation * 100,
            stutterRange,
            true,
            warnings,
            DateTimeOffset.UtcNow);
    }

    private static double CoefficientOfVariation(
        IEnumerable<double> source)
    {
        var values = source.ToArray();

        if (values.Length < 2 ||
            values.Any(x => !double.IsFinite(x) || x <= 0))
            return double.PositiveInfinity;

        var average = values.Average();

        if (average <= 0)
            return double.PositiveInfinity;

        var variance = values.Sum(x =>
            Math.Pow(x - average, 2)) / values.Length;

        return Math.Sqrt(variance) / average;
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static BenchmarkRepeatabilityReport Empty(
        BenchmarkRepeatabilityLevel level,
        int count,
        IReadOnlyList<string> warnings)
    {
        return new BenchmarkRepeatabilityReport(
            level, count,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            false,
            warnings,
            DateTimeOffset.UtcNow);
    }
}
