namespace EvoOptimizer.Infrastructure.Analysis;

public sealed record PerformanceTelemetryPoint(
    DateTimeOffset Timestamp,
    double SystemCpuPercent,
    double? Cs2CpuPercent,
    double MemoryUsagePercent,
    double AvailableMemoryMb,
    double Cs2MemoryMb,
    int ActiveProcessCount);

public enum PerformanceCause
{
    CpuPressure,
    MemoryPressure,
    BackgroundActivity,
    FrametimeSpikeWithoutTelemetryEvidence,
    InsufficientTelemetry
}

public sealed record PerformanceCorrelation(
    double FrameTimeSeconds,
    double FrametimeMs,
    DateTimeOffset MatchedTelemetryTime,
    double TimeDifferenceSeconds,
    PerformanceCause Cause,
    double ConfidencePercent,
    string Explanation,
    IReadOnlyList<string> Evidence);

public sealed record PerformanceCorrelationReport(
    DateTimeOffset AnalyzedAt,
    int AnalyzedStutters,
    int CorrelatedStutters,
    int UnmatchedStutters,
    int CpuPressureEvents,
    int MemoryPressureEvents,
    int BackgroundActivityEvents,
    IReadOnlyList<PerformanceCorrelation> Events,
    IReadOnlyList<string> Findings);

public sealed class Cs2PerformanceCorrelator
{
    public PerformanceCorrelationReport Analyze(
        PresentMonReadResult frameData,
        IEnumerable<PerformanceTelemetryPoint> telemetry,
        DateTimeOffset captureStartedAt,
        double matchingWindowSeconds = 2.0)
    {
        ArgumentNullException.ThrowIfNull(frameData);
        ArgumentNullException.ThrowIfNull(telemetry);

        if (!double.IsFinite(matchingWindowSeconds) ||
            matchingWindowSeconds <= 0 ||
            matchingWindowSeconds > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchingWindowSeconds));
        }

        var samples = telemetry
            .Where(x =>
                double.IsFinite(x.SystemCpuPercent) &&
                (!x.Cs2CpuPercent.HasValue ||
                    double.IsFinite(x.Cs2CpuPercent.Value)) &&
                double.IsFinite(x.MemoryUsagePercent) &&
                double.IsFinite(x.AvailableMemoryMb) &&
                double.IsFinite(x.Cs2MemoryMb))
            .OrderBy(x => x.Timestamp)
            .ToArray();

        var frames = frameData.Frames
            .Where(x =>
                double.IsFinite(x.FrametimeMs) &&
                x.FrametimeMs > 0 &&
                x.FrametimeMs <= 10000)
            .OrderBy(x => x.TimeSeconds)
            .ToArray();

        if (frames.Length < 30 || samples.Length < 2)
        {
            return EmptyReport(
                "Se necesitan al menos 30 frames y 2 muestras de telemetría.");
        }

        var sortedFrametimes = frames
            .Select(x => x.FrametimeMs)
            .OrderBy(x => x)
            .ToArray();

        var median = Percentile(sortedFrametimes, 0.50);
        var threshold = Math.Max(median * 1.5, median + 3.0);

        var stutters = frames
            .Where(x => x.FrametimeMs >= threshold)
            .ToArray();

        var events = new List<PerformanceCorrelation>();

        foreach (var frame in stutters)
        {
            var expectedTime = captureStartedAt.AddSeconds(
                frame.TimeSeconds);

            var nearest = samples
                .Select(sample => new
                {
                    Sample = sample,
                    Difference = Math.Abs(
                        (sample.Timestamp - expectedTime).TotalSeconds)
                })
                .OrderBy(x => x.Difference)
                .First();

            if (nearest.Difference > matchingWindowSeconds)
                continue;

            var sample = nearest.Sample;
            var evidence = new List<string>();

            var cpuPressure = sample.SystemCpuPercent >= 90 ||
                              (sample.Cs2CpuPercent.HasValue &&
                               sample.Cs2CpuPercent.Value >= 90);

            var memoryPressure = sample.MemoryUsagePercent >= 90 ||
                                 sample.AvailableMemoryMb <= 1024;

            var backgroundPressure =
                sample.ActiveProcessCount > 0 &&
                sample.SystemCpuPercent >= 75 &&
                sample.Cs2CpuPercent.HasValue &&
                sample.Cs2CpuPercent.Value < 65;

            PerformanceCause cause;
            double confidence;
            string explanation;

            if (cpuPressure && memoryPressure)
            {
                cause = PerformanceCause.CpuPressure;
                confidence = 85;
                explanation =
                    "Coinciden presión elevada de CPU y memoria.";
                evidence.Add($"CPU del sistema: {sample.SystemCpuPercent:F1}%.");

                if (sample.Cs2CpuPercent.HasValue)
                    evidence.Add($"CPU de CS2: {sample.Cs2CpuPercent.Value:F1}%.");
                else
                    evidence.Add("CPU de CS2: dato no disponible en esta muestra.");

                evidence.Add("Memoria utilizada elevada o RAM disponible baja.");
            }
            else if (cpuPressure)
            {
                cause = PerformanceCause.CpuPressure;
                confidence = 80;
                explanation =
                    "El stutter coincide con una carga elevada de CPU.";
                evidence.Add(
                    $"CPU del sistema: {sample.SystemCpuPercent:F1}%.");
                if (sample.Cs2CpuPercent.HasValue)
                    evidence.Add(
                        $"CPU de CS2: {sample.Cs2CpuPercent.Value:F1}%.");
                else
                    evidence.Add(
                        "CPU de CS2: dato no disponible en esta muestra.");
            }
            else if (memoryPressure)
            {
                cause = PerformanceCause.MemoryPressure;
                confidence = 80;
                explanation =
                    "El stutter coincide con presión de memoria.";
                evidence.Add(
                    $"Memoria utilizada: {sample.MemoryUsagePercent:F1}%.");
                evidence.Add(
                    $"RAM disponible: {sample.AvailableMemoryMb:F0} MB.");
            }
            else if (backgroundPressure)
            {
                cause = PerformanceCause.BackgroundActivity;
                confidence = 65;
                explanation =
                    "Hay carga del sistema no explicada por la CPU de CS2.";
                evidence.Add(
                    $"CPU del sistema: {sample.SystemCpuPercent:F1}%.");
                if (sample.Cs2CpuPercent.HasValue)
                    evidence.Add(
                        $"CPU de CS2: {sample.Cs2CpuPercent.Value:F1}%.");
                else
                    evidence.Add(
                        "CPU de CS2: dato no disponible en esta muestra.");
                evidence.Add(
                    $"Procesos activos: {sample.ActiveProcessCount}.");
            }
            else
            {
                cause =
                    PerformanceCause.FrametimeSpikeWithoutTelemetryEvidence;
                confidence = 35;
                explanation =
                    "No hay evidencia suficiente de presión de CPU o RAM en esta muestra.";
                evidence.Add(
                    "Revisar GPU, almacenamiento, drivers, temperatura y latencia.");
            }

            events.Add(new PerformanceCorrelation(
                frame.TimeSeconds,
                frame.FrametimeMs,
                sample.Timestamp,
                nearest.Difference,
                cause,
                confidence,
                explanation,
                evidence));
        }

        var cpuEvents = events.Count(x =>
            x.Cause == PerformanceCause.CpuPressure);

        var memoryEvents = events.Count(x =>
            x.Cause == PerformanceCause.MemoryPressure);

        var backgroundEvents = events.Count(x =>
            x.Cause == PerformanceCause.BackgroundActivity);

        var findings = BuildFindings(
            stutters.Length,
            events.Count,
            cpuEvents,
            memoryEvents,
            backgroundEvents,
            samples.Length);

        return new PerformanceCorrelationReport(
            DateTimeOffset.UtcNow,
            stutters.Length,
            events.Count,
            Math.Max(0, stutters.Length - events.Count),
            cpuEvents,
            memoryEvents,
            backgroundEvents,
            events
                .OrderByDescending(x => x.FrametimeMs)
                .Take(100)
                .ToArray(),
            findings);
    }

    private static double Percentile(
        double[] sorted,
        double percentile)
    {
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
            return sorted[lower];

        var fraction = position - lower;

        return sorted[lower] +
               (sorted[upper] - sorted[lower]) * fraction;
    }

    private static IReadOnlyList<string> BuildFindings(
        int stutters,
        int correlated,
        int cpu,
        int memory,
        int background,
        int telemetrySamples)
    {
        var findings = new List<string>();

        if (stutters == 0)
        {
            findings.Add(
                "No se detectaron stutters según el umbral utilizado.");
            return findings;
        }

        if (correlated == 0)
        {
            findings.Add(
                "No hay coincidencias temporales suficientes. Mejorar la cobertura de telemetría.");
        }

        if (cpu > 0)
        {
            findings.Add(
                $"{cpu} stutters coincidieron con presión de CPU.");
        }

        if (memory > 0)
        {
            findings.Add(
                $"{memory} stutters coincidieron con presión de memoria.");
        }

        if (background > 0)
        {
            findings.Add(
                $"{background} stutters coincidieron con actividad elevada del sistema no explicada por CS2.");
        }

        if (telemetrySamples < 10)
        {
            findings.Add(
                "Pocas muestras: las conclusiones tienen cobertura limitada.");
        }

        findings.Add(
            "Las coincidencias son indicios, no pruebas de causalidad.");

        return findings;
    }

    private static PerformanceCorrelationReport EmptyReport(
        string reason)
    {
        return new PerformanceCorrelationReport(
            DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0,
            Array.Empty<PerformanceCorrelation>(),
            new[] { reason });
    }
}
