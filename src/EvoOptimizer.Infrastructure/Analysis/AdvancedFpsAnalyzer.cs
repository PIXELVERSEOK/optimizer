namespace EvoOptimizer.Infrastructure.Analysis;

public enum FpsStabilityLevel
{
    Excellent,
    Good,
    Moderate,
    Poor,
    Critical,
    InsufficientData
}

public sealed record FrametimeEvent(
    double TimeSeconds,
    double FrametimeMs,
    string Severity,
    string Description);

public sealed record FrametimeStutterCluster(
    double StartSeconds,
    double EndSeconds,
    int EventCount,
    double MaximumFrametimeMs);

public sealed record AdvancedFpsReport(
    DateTimeOffset AnalyzedAt,
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double MedianFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double MedianFrametimeMs,
    double P95FrametimeMs,
    double P99FrametimeMs,
    double P999FrametimeMs,
    double MaximumFrametimeMs,
    double StandardDeviationMs,
    double CoefficientOfVariationPercent,
    double MicroStutterThresholdMs,
    int MicroStutterCount,
    int SevereStutterCount,
    int DroppedFrameCount,
    double StutterRatePercent,
    double StableFrameRatePercent,
    FpsStabilityLevel Stability,
    IReadOnlyList<FrametimeEvent> WorstEvents,
    IReadOnlyList<FrametimeStutterCluster> StutterClusters,
    IReadOnlyList<string> Findings);

public sealed class AdvancedFpsAnalyzer
{
    public AdvancedFpsReport Analyze(PresentMonReadResult input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var frames = input.Frames
            .Where(frame =>
                double.IsFinite(frame.FrametimeMs) &&
                frame.FrametimeMs > 0 &&
                frame.FrametimeMs <= 10000)
            .OrderBy(frame => frame.TimeSeconds)
            .ToArray();

        if (frames.Length < 30)
            return EmptyReport(frames.Length);

        var times = frames.Select(x => x.FrametimeMs)
            .OrderBy(x => x)
            .ToArray();

        var count = times.Length;
        var mean = times.Average();
        var median = Percentile(times, 0.50);
        var p95 = Percentile(times, 0.95);
        var p99 = Percentile(times, 0.99);
        var p999 = Percentile(times, 0.999);
        var maximum = times[^1];

        var variance = times.Average(value =>
            Math.Pow(value - mean, 2));

        var standardDeviation = Math.Sqrt(variance);
        var coefficient = mean > 0
            ? standardDeviation / mean * 100
            : 0;

        var microThreshold = Math.Max(median * 1.5, median + 3.0);
        var severeThreshold = Math.Max(median * 2.5, median + 8.0);

        var events = frames
            .Where(frame => frame.FrametimeMs >= microThreshold)
            .Select(frame => new FrametimeEvent(
                frame.TimeSeconds,
                frame.FrametimeMs,
                frame.FrametimeMs >= severeThreshold
                    ? "Severe"
                    : "Microstutter",
                frame.FrametimeMs >= severeThreshold
                    ? "Pico severo de frametime"
                    : "Microstutter detectado"))
            .ToArray();

        var severeCount = events.Count(x => x.Severity == "Severe");
        var droppedCount = frames.Count(x => x.Dropped);

        var stutterRate = count > 0
            ? events.Length * 100d / count
            : 0;

        var stableFrames = frames.Count(
            frame => frame.FrametimeMs < microThreshold);

        var stableRate = stableFrames * 100d / count;

        var duration = CalculateDuration(frames);

        var averageFps = mean > 0 ? 1000d / mean : 0;
        var medianFps = median > 0 ? 1000d / median : 0;

        var onePercentLow = 1000d /
            AverageSlowest(times, 0.01);

        var pointOnePercentLow = 1000d /
            AverageSlowest(times, 0.001);

        var clusters = BuildClusters(events);

        var findings = BuildFindings(
            coefficient,
            stutterRate,
            severeCount,
            droppedCount,
            p99,
            duration);

        return new AdvancedFpsReport(
            AnalyzedAt: DateTimeOffset.UtcNow,
            FrameCount: count,
            DurationSeconds: duration,
            AverageFps: averageFps,
            MedianFps: medianFps,
            OnePercentLowFps: onePercentLow,
            PointOnePercentLowFps: pointOnePercentLow,
            MedianFrametimeMs: median,
            P95FrametimeMs: p95,
            P99FrametimeMs: p99,
            P999FrametimeMs: p999,
            MaximumFrametimeMs: maximum,
            StandardDeviationMs: standardDeviation,
            CoefficientOfVariationPercent: coefficient,
            MicroStutterThresholdMs: microThreshold,
            MicroStutterCount: events.Length,
            SevereStutterCount: severeCount,
            DroppedFrameCount: droppedCount,
            StutterRatePercent: stutterRate,
            StableFrameRatePercent: stableRate,
            Stability: Classify(
                coefficient,
                stutterRate,
                severeCount,
                duration),
            WorstEvents: events
                .OrderByDescending(x => x.FrametimeMs)
                .Take(20)
                .ToArray(),
            StutterClusters: clusters,
            Findings: findings);
    }

    private static double Percentile(
        double[] sorted,
        double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
            return sorted[lower];

        var fraction = position - lower;

        return sorted[lower] +
               (sorted[upper] - sorted[lower]) * fraction;
    }

    private static double AverageSlowest(
        double[] sortedAscending,
        double fraction)
    {
        var amount = Math.Max(
            1,
            (int)Math.Ceiling(sortedAscending.Length * fraction));

        return sortedAscending
            .TakeLast(amount)
            .Average();
    }

    private static double CalculateDuration(
        PresentMonFrame[] frames)
    {
        if (frames.Length < 2)
            return frames.Length == 1
                ? frames[0].FrametimeMs / 1000d
                : 0;

        var first = frames[0].TimeSeconds;
        var last = frames[^1].TimeSeconds;
        var elapsed = last - first;

        if (double.IsFinite(elapsed) && elapsed > 0)
            return elapsed;

        return frames.Sum(x => x.FrametimeMs) / 1000d;
    }

    private static IReadOnlyList<FrametimeStutterCluster>
        BuildClusters(FrametimeEvent[] events)
    {
        if (events.Length == 0)
            return Array.Empty<FrametimeStutterCluster>();

        var result = new List<FrametimeStutterCluster>();

        var start = events[0].TimeSeconds;
        var previous = start;
        var maximum = events[0].FrametimeMs;
        var count = 1;

        foreach (var item in events.Skip(1))
        {
            if (item.TimeSeconds - previous <= 1.0)
            {
                count++;
                previous = item.TimeSeconds;
                maximum = Math.Max(maximum, item.FrametimeMs);
                continue;
            }

            result.Add(new FrametimeStutterCluster(
                start, previous, count, maximum));

            start = item.TimeSeconds;
            previous = item.TimeSeconds;
            maximum = item.FrametimeMs;
            count = 1;
        }

        result.Add(new FrametimeStutterCluster(
            start, previous, count, maximum));

        return result;
    }

    private static FpsStabilityLevel Classify(
        double coefficient,
        double stutterRate,
        int severeCount,
        double duration)
    {
        if (duration < 10)
            return FpsStabilityLevel.InsufficientData;

        if (severeCount >= 20 ||
            stutterRate >= 10 ||
            coefficient >= 35)
            return FpsStabilityLevel.Critical;

        if (severeCount >= 8 ||
            stutterRate >= 5 ||
            coefficient >= 25)
            return FpsStabilityLevel.Poor;

        if (stutterRate >= 2 ||
            coefficient >= 15)
            return FpsStabilityLevel.Moderate;

        if (stutterRate >= 0.5 ||
            coefficient >= 8)
            return FpsStabilityLevel.Good;

        return FpsStabilityLevel.Excellent;
    }

    private static IReadOnlyList<string> BuildFindings(
        double coefficient,
        double stutterRate,
        int severeCount,
        int droppedCount,
        double p99,
        double duration)
    {
        var findings = new List<string>();

        if (duration < 30)
            findings.Add(
                "Captura corta: se recomienda una sesión de al menos 30 segundos.");

        if (coefficient >= 15)
            findings.Add(
                "Variabilidad elevada: los frametimes presentan dispersión importante.");

        if (stutterRate >= 2)
            findings.Add(
                "Microstutters frecuentes: revisar procesos en segundo plano y carga del sistema.");

        if (severeCount > 0)
            findings.Add(
                $"Se detectaron {severeCount} picos severos de frametime.");

        if (droppedCount > 0)
            findings.Add(
                $"La fuente informa {droppedCount} frames descartados.");

        if (p99 > 25)
            findings.Add(
                "El percentil 99 supera 25 ms: investigar los peores momentos de la sesión.");

        if (findings.Count == 0)
            findings.Add(
                "No se detectaron anomalías importantes según los umbrales actuales.");

        return findings;
    }

    private static AdvancedFpsReport EmptyReport(int frameCount)
    {
        return new AdvancedFpsReport(
            AnalyzedAt: DateTimeOffset.UtcNow,
            FrameCount: frameCount,
            DurationSeconds: 0,
            AverageFps: 0,
            MedianFps: 0,
            OnePercentLowFps: 0,
            PointOnePercentLowFps: 0,
            MedianFrametimeMs: 0,
            P95FrametimeMs: 0,
            P99FrametimeMs: 0,
            P999FrametimeMs: 0,
            MaximumFrametimeMs: 0,
            StandardDeviationMs: 0,
            CoefficientOfVariationPercent: 0,
            MicroStutterThresholdMs: 0,
            MicroStutterCount: 0,
            SevereStutterCount: 0,
            DroppedFrameCount: 0,
            StutterRatePercent: 0,
            StableFrameRatePercent: 0,
            Stability: FpsStabilityLevel.InsufficientData,
            WorstEvents: Array.Empty<FrametimeEvent>(),
            StutterClusters: Array.Empty<FrametimeStutterCluster>(),
            Findings: new[]
            {
                "Datos insuficientes: se necesitan al menos 30 frames válidos."
            });
    }
}
