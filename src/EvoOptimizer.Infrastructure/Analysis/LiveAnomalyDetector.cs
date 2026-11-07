using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.Infrastructure.Analysis;

public enum LiveAnomalySeverity
{
    Information,
    Warning,
    Critical
}

public sealed record LiveAnomaly(
    string Code,
    string Title,
    LiveAnomalySeverity Severity,
    double Confidence,
    int AffectedSamples,
    int EvaluatedSamples,
    string Evidence,
    string Recommendation);

public sealed record LiveAnomalyReport(
    DateTimeOffset AnalyzedAt,
    int SamplesAnalyzed,
    bool HasEnoughData,
    IReadOnlyList<LiveAnomaly> Anomalies,
    IReadOnlyList<OptimizationSignal> OptimizationSignals,
    IReadOnlyList<string> Limitations);

public sealed class LiveAnomalyDetector
{
    private const int MinimumSamples = 5;
    private const int RequiredOccurrences = 3;

    public LiveAnomalyReport Analyze(
        IEnumerable<LivePerformanceSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var samples = snapshots
            .Where(IsValid)
            .OrderBy(x => x.Timestamp)
            .TakeLast(60)
            .ToArray();

        var limitations = new List<string>
        {
            "La monitorización actual no mide directamente el uso de GPU.",
            "La presión de almacenamiento y las temperaturas requieren sensores adicionales.",
            "Las anomalías indican correlación temporal, no causalidad."
        };

        if (samples.Length < MinimumSamples)
        {
            return new LiveAnomalyReport(
                DateTimeOffset.UtcNow,
                samples.Length,
                false,
                Array.Empty<LiveAnomaly>(),
                Array.Empty<OptimizationSignal>(),
                limitations.Append(
                    "Se necesitan al menos cinco muestras válidas.").ToArray());
        }

        var anomalies = new List<LiveAnomaly>();

        Detect(
            samples,
            "HIGH_CPU_PRESSURE",
            "Presión elevada de CPU",
            x => x.SystemCpuPercent >= 85,
            LiveAnomalySeverity.Warning,
            "Revisar procesos de fondo y carga concurrente.",
            anomalies);

        Detect(
            samples,
            "HIGH_MEMORY_PRESSURE",
            "Presión elevada de memoria",
            x => x.MemoryUsagePercent >= 90 ||
                 (x.TotalMemoryMb > 0 &&
                  x.AvailableMemoryMb / x.TotalMemoryMb <= 0.10),
            LiveAnomalySeverity.Critical,
            "Identificar aplicaciones con alto consumo de memoria.",
            anomalies);

        Detect(
            samples,
            "HIGH_CS2_CPU",
            "Consumo elevado de CPU por CS2",
            x => x.Cs2CpuPercent.HasValue &&
                 x.Cs2CpuPercent.Value >= 85,
            LiveAnomalySeverity.Warning,
            "Analizar la carga del juego y sus procesos concurrentes.",
            anomalies);

        Detect(
            samples,
            "CS2_MEMORY_PRESSURE",
            "Consumo elevado de memoria por CS2",
            x => x.Cs2MemoryMb >= 2048,
            LiveAnomalySeverity.Information,
            "Comprobar la evolución del consumo de memoria durante la sesión.",
            anomalies);

        DetectBackgroundActivity(samples, anomalies);

        var signals = BuildOptimizationSignals(anomalies);

        return new LiveAnomalyReport(
            DateTimeOffset.UtcNow,
            samples.Length,
            true,
            anomalies,
            signals,
            limitations);
    }

    private static void Detect(
        LivePerformanceSnapshot[] samples,
        string code,
        string title,
        Func<LivePerformanceSnapshot, bool> predicate,
        LiveAnomalySeverity severity,
        string recommendation,
        List<LiveAnomaly> anomalies)
    {
        var affected = samples.Count(predicate);

        if (affected < RequiredOccurrences)
            return;

        var confidence = Math.Min(
            0.98,
            0.65 + (double)affected / samples.Length * 0.30);

        var percentages = samples
            .Where(predicate)
            .Select(x => x.Timestamp.ToString("HH:mm:ss"))
            .Take(5);

        anomalies.Add(new LiveAnomaly(
            code,
            title,
            severity,
            Math.Round(confidence, 2),
            affected,
            samples.Length,
            $"{affected} de {samples.Length} muestras afectadas. " +
            $"Momentos: {string.Join(", ", percentages)}.",
            recommendation));
    }

    private static void DetectBackgroundActivity(
        LivePerformanceSnapshot[] samples,
        List<LiveAnomaly> anomalies)
    {
        if (samples.Length < 10)
            return;

        var baseline = samples
            .Take(samples.Length / 2)
            .Select(x => x.ActiveProcessCount)
            .OrderBy(x => x)
            .ToArray();

        var recent = samples
            .Skip(samples.Length / 2)
            .ToArray();

        if (baseline.Length == 0 || recent.Length == 0)
            return;

        var median = baseline[baseline.Length / 2];
        var recentAverage = recent.Average(x => x.ActiveProcessCount);

        if (median <= 0 ||
            recentAverage < median * 1.5 ||
            recentAverage - median < 10)
            return;

        anomalies.Add(new LiveAnomaly(
            "BACKGROUND_ACTIVITY_SPIKE",
            "Aumento de actividad de procesos",
            LiveAnomalySeverity.Warning,
            0.75,
            recent.Length,
            samples.Length,
            $"Mediana inicial: {median} procesos. " +
            $"Promedio reciente: {recentAverage:0.0}.",
            "Identificar los procesos que comenzaron a consumir recursos."));
    }

    private static IReadOnlyList<OptimizationSignal>
        BuildOptimizationSignals(
            IReadOnlyList<LiveAnomaly> anomalies)
    {
        var signals = new List<OptimizationSignal>();

        AddSignal(
            signals,
            anomalies,
            "HIGH_CPU_PRESSURE",
            "HIGH_CPU_PRESSURE");

        AddSignal(
            signals,
            anomalies,
            "HIGH_MEMORY_PRESSURE",
            "HIGH_MEMORY_PRESSURE");

        if (anomalies.Any(x => x.Code == "BACKGROUND_ACTIVITY_SPIKE"))
        {
            var anomaly = anomalies.First(
                x => x.Code == "BACKGROUND_ACTIVITY_SPIKE");

            signals.Add(new OptimizationSignal(
                "BACKGROUND_ACTIVITY_SPIKE",
                true,
                anomaly.Confidence,
                anomaly.Evidence));
        }

        return signals;
    }

    private static void AddSignal(
        List<OptimizationSignal> signals,
        IReadOnlyList<LiveAnomaly> anomalies,
        string anomalyCode,
        string signalCode)
    {
        var anomaly = anomalies.FirstOrDefault(
            x => x.Code == anomalyCode);

        if (anomaly is null)
            return;

        signals.Add(new OptimizationSignal(
            signalCode,
            true,
            anomaly.Confidence,
            anomaly.Evidence));
    }

    private static bool IsValid(LivePerformanceSnapshot x)
    {
        return double.IsFinite(x.SystemCpuPercent) &&
               double.IsFinite(x.MemoryUsagePercent) &&
               double.IsFinite(x.AvailableMemoryMb) &&
               double.IsFinite(x.TotalMemoryMb) &&
               x.SystemCpuPercent is >= 0 and <= 100 &&
               x.MemoryUsagePercent is >= 0 and <= 100 &&
               x.AvailableMemoryMb >= 0 &&
               x.TotalMemoryMb >= 0 &&
               x.ActiveProcessCount >= 0 &&
               x.Cs2MemoryMb >= 0 &&
               (!x.Cs2CpuPercent.HasValue ||
                (double.IsFinite(x.Cs2CpuPercent.Value) &&
                 x.Cs2CpuPercent.Value is >= 0 and <= 100));
    }
}
