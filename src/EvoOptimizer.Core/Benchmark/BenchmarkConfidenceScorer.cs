namespace EvoOptimizer.Core.Benchmark;

public enum BenchmarkConfidenceLevel
{
    Insufficient,
    Low,
    Moderate,
    High,
    VeryHigh
}

public sealed record BenchmarkConfidenceReport(
    double Score,
    BenchmarkConfidenceLevel Level,
    int ValidFrameCount,
    double DurationSeconds,
    double AverageFrametimeMs,
    double FrametimeStandardDeviationMs,
    double CoefficientOfVariation,
    double P50FrametimeMs,
    double P99FrametimeMs,
    double SevereStutterRate,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CalculatedAt);

public sealed class BenchmarkConfidenceScorer
{
    public BenchmarkConfidenceReport Evaluate(
        BenchmarkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var warnings = new List<string>();

        var frames = evidence.Frames
            .Where(x =>
                double.IsFinite(x.TimestampSeconds) &&
                double.IsFinite(x.FrametimeMs) &&
                x.TimestampSeconds >= 0 &&
                x.FrametimeMs > 0)
            .OrderBy(x => x.TimestampSeconds)
            .ToArray();

        if (frames.Length == 0)
        {
            return Empty("No hay frames válidos.");
        }

        var duration = frames[^1].TimestampSeconds -
                       frames[0].TimestampSeconds;

        var values = frames
            .Select(x => x.FrametimeMs)
            .ToArray();

        var average = values.Average();
        var variance = values.Sum(x =>
            Math.Pow(x - average, 2)) / values.Length;

        var standardDeviation = Math.Sqrt(variance);
        var cv = average > 0
            ? standardDeviation / average
            : double.PositiveInfinity;

        var sorted = values.OrderBy(x => x).ToArray();
        var p50 = Percentile(sorted, 0.50);
        var p99 = Percentile(sorted, 0.99);

        var severeThreshold = Math.Max(p50 * 2.5, p50 + 8.0);
        var severeCount = values.Count(x => x >= severeThreshold);
        var severeRate = (double)severeCount / values.Length;

        if (frames.Length < 1000)
            warnings.Add("Menos de 1000 frames válidos.");

        if (duration < 30)
            warnings.Add("Duración inferior a 30 segundos.");

        if (cv > 0.35)
            warnings.Add("Variabilidad de frametime elevada.");

        if (severeRate > 0.05)
            warnings.Add("Frecuencia elevada de stutters severos.");

        var frameScore = Math.Min(1.0, frames.Length / 3000.0) * 20.0;
        var durationScore = Math.Min(1.0, duration / 60.0) * 20.0;

        var variabilityScore =
            double.IsFinite(cv)
                ? 25.0 * Math.Exp(-Math.Max(0, cv - 0.08) * 3.0)
                : 0;

        var tailScore = average > 0
            ? 15.0 * Math.Clamp(
                1.0 - Math.Max(0, p99 / average - 3.0) / 12.0,
                0, 1)
            : 0;

        var stutterScore =
            20.0 * Math.Clamp(1.0 - severeRate * 10.0, 0, 1);

        var score = Math.Round(
            frameScore +
            durationScore +
            variabilityScore +
            tailScore +
            stutterScore,
            2);

        var level = score switch
        {
            >= 90 => BenchmarkConfidenceLevel.VeryHigh,
            >= 75 => BenchmarkConfidenceLevel.High,
            >= 55 => BenchmarkConfidenceLevel.Moderate,
            >= 35 => BenchmarkConfidenceLevel.Low,
            _ => BenchmarkConfidenceLevel.Insufficient
        };

        return new BenchmarkConfidenceReport(
            score,
            level,
            frames.Length,
            duration,
            average,
            standardDeviation,
            cv,
            p50,
            p99,
            severeRate,
            warnings,
            DateTimeOffset.UtcNow);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var index = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);

        if (lower == upper)
            return sorted[lower];

        var fraction = index - lower;

        return sorted[lower] +
               (sorted[upper] - sorted[lower]) * fraction;
    }

    private static BenchmarkConfidenceReport Empty(string warning)
    {
        return new BenchmarkConfidenceReport(
            0,
            BenchmarkConfidenceLevel.Insufficient,
            0, 0, 0, 0, 0, 0, 0, 0,
            new[] { warning },
            DateTimeOffset.UtcNow);
    }
}
