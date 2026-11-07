namespace EvoOptimizer.Infrastructure.Analysis;

public sealed record FrametimeAnalysisReport(
    DateTimeOffset AnalyzedAt,
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double AverageFrametimeMs,
    double MedianFrametimeMs,
    double P99FrametimeMs,
    double MaximumFrametimeMs,
    double FrametimeStandardDeviationMs,
    int StutterCount,
    double StutterRatePercent,
    int DroppedFrameCount,
    string SourceColumn,
    IReadOnlyList<string> Warnings);

public sealed class FrametimeAnalyzer
{
    public FrametimeAnalysisReport Analyze(PresentMonReadResult input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var frames = input.Frames
            .Where(x => double.IsFinite(x.FrametimeMs) && x.FrametimeMs > 0)
            .ToArray();

        if (frames.Length == 0)
            throw new ArgumentException("No hay frames válidos.", nameof(input));

        var times = frames
            .Select(x => x.FrametimeMs)
            .OrderBy(x => x)
            .ToArray();

        var averageFrameTime = times.Average();
        var median = Percentile(times, 0.50);
        var p99 = Percentile(times, 0.99);
        var maximum = times[^1];

        var duration = frames.Length > 1
            ? frames.Max(x => x.TimeSeconds) - frames.Min(x => x.TimeSeconds)
            : 0;

        if (duration <= 0)
            duration = times.Sum() / 1000d;

        // Low FPS: promedio de los frametimes más lentos.
        var slowestOnePercent = Math.Max(1, (int)Math.Ceiling(times.Length * 0.01));
        var slowestPointOnePercent = Math.Max(1, (int)Math.Ceiling(times.Length * 0.001));

        var onePercentLow = 1000d /
            times.Skip(times.Length - slowestOnePercent).Average();

        var pointOnePercentLow = 1000d /
            times.Skip(times.Length - slowestPointOnePercent).Average();

        var stutterThreshold = Math.Max(median * 1.5, median + 8d);

        var stutters = frames.Count(
            x => x.FrametimeMs > stutterThreshold);

        var standardDeviation = Math.Sqrt(
            times.Average(x => Math.Pow(x - averageFrameTime, 2)));

        var warnings = input.Warnings.ToList();

        if (frames.Length < 1000)
            warnings.Add(
                "Muestra corta: los percentiles bajos pueden ser poco representativos.");

        if (duration < 30)
            warnings.Add(
                "Duración inferior a 30 segundos: se recomienda una captura más larga.");

        if (input.FrametimeColumn.Equals(
            "MsBetweenPresents",
            StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                "Se utilizó MsBetweenPresents: mide intervalos entre presents, no necesariamente frames mostrados en pantalla.");
        }

        return new FrametimeAnalysisReport(
            DateTimeOffset.UtcNow,
            frames.Length,
            duration,
            1000d / averageFrameTime,
            onePercentLow,
            pointOnePercentLow,
            averageFrameTime,
            median,
            p99,
            maximum,
            standardDeviation,
            stutters,
            stutters * 100d / frames.Length,
            frames.Count(x => x.Dropped),
            input.FrametimeColumn,
            warnings);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
            return sorted[0];

        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
            return sorted[lower];

        var fraction = position - lower;

        return sorted[lower] +
               (sorted[upper] - sorted[lower]) * fraction;
    }
}
