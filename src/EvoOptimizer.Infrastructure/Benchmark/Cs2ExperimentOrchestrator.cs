using EvoOptimizer.Core.Benchmark;
using EvoOptimizer.Core.Optimization;

namespace EvoOptimizer.Infrastructure.Benchmark;

public enum ExperimentVerdict
{
    NotStarted,
    BaselineRecorded,
    CandidateRecorded,
    ImprovementVerified,
    RegressionDetected,
    Inconclusive,
    GpuBottleneckDominant
}

public sealed record Cs2BenchmarkMetrics(
    DateTimeOffset Timestamp,
    string ConfigurationName,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double P95FrametimeMs,
    double P99FrametimeMs,
    double StutterRatePercent,
    int FrameCount,
    double DurationSeconds,
    double? SystemCpuPercent,
    double? Cs2CpuPercent,
    double? GpuUsagePercent,
    double? GpuSharedMemoryMb,
    double? MemoryUsagePercent,
    double? CommitUsedMb);

public sealed record Cs2ComparisonResult(
    Cs2BenchmarkMetrics Baseline,
    Cs2BenchmarkMetrics Candidate,
    double DeltaAverageFpsPercent,
    double DeltaOnePercentLowPercent,
    double DeltaPointOnePercentLowPercent,
    double DeltaP99FrametimePercent,
    double DeltaStutterRatePercent,
    ExperimentVerdict Verdict,
    string Explanation,
    bool ShouldKeepOptimization,
    bool RequiresRollback,
    IReadOnlyList<string> EvidentiaryNotes);

public sealed class Cs2ExperimentOrchestrator
{
    private readonly RegressionGuard _regressionGuard;

    public Cs2BenchmarkMetrics? Baseline { get; private set; }
    public Cs2BenchmarkMetrics? Candidate { get; private set; }
    public Cs2ComparisonResult? LatestComparison { get; private set; }

    public Cs2ExperimentOrchestrator(RegressionGuard? regressionGuard = null)
    {
        _regressionGuard = regressionGuard ?? new RegressionGuard();
    }

    public void RecordBaseline(Cs2BenchmarkMetrics baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Baseline = baseline;
        Candidate = null;
        LatestComparison = null;
    }

    public void RecordCandidate(Cs2BenchmarkMetrics candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;

        if (Baseline != null)
        {
            LatestComparison = Compare(Baseline, candidate);
        }
    }

    public void Clear()
    {
        Baseline = null;
        Candidate = null;
        LatestComparison = null;
    }

    public Cs2ComparisonResult Compare(Cs2BenchmarkMetrics baseline, Cs2BenchmarkMetrics candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        double deltaAvgFps = CalculateDelta(baseline.AverageFps, candidate.AverageFps);
        double deltaOneLow = CalculateDelta(baseline.OnePercentLowFps, candidate.OnePercentLowFps);
        double deltaPointOneLow = CalculateDelta(baseline.PointOnePercentLowFps, candidate.PointOnePercentLowFps);
        double deltaP99 = CalculateDelta(baseline.P99FrametimeMs, candidate.P99FrametimeMs);
        double deltaStutter = candidate.StutterRatePercent - baseline.StutterRatePercent;

        var notes = new List<string>();

        // Correlación observacional con uso de GPU y commit.
        bool isGpuBound = (candidate.GpuUsagePercent.HasValue && candidate.GpuUsagePercent.Value >= 92) ||
                          (baseline.GpuUsagePercent.HasValue && baseline.GpuUsagePercent.Value >= 92);

        if (isGpuBound)
        {
            notes.Add("Uso de GPU >= 92% observado en una de las capturas; puede existir un límite gráfico. Confirmá con escenarios repetidos y comparables.");
        }

        if (candidate.CommitUsedMb is > 12000)
        {
            notes.Add($"Commit elevado observado ({candidate.CommitUsedMb.Value:F0} MB); EVO no mide page faults en esta captura.");
        }

        // Evaluación con RegressionGuard del Core
        var baselineMeasurement = new RegressionMeasurement(
            baseline.Timestamp,
            "CS2_Benchmark",
            "UNKNOWN",
            "PresentMon",
            baseline.AverageFps,
            baseline.OnePercentLowFps,
            baseline.PointOnePercentLowFps,
            baseline.P99FrametimeMs,
            baseline.StutterRatePercent,
            baseline.DurationSeconds,
            baseline.FrameCount);

        var candidateMeasurement = new RegressionMeasurement(
            candidate.Timestamp,
            "CS2_Benchmark",
            "UNKNOWN",
            "PresentMon",
            candidate.AverageFps,
            candidate.OnePercentLowFps,
            candidate.PointOnePercentLowFps,
            candidate.P99FrametimeMs,
            candidate.StutterRatePercent,
            candidate.DurationSeconds,
            candidate.FrameCount);

        var watch = new RegressionWatch(
            Guid.NewGuid(),
            "cs2-optimization-experiment",
            candidate.ConfigurationName,
            RestoreEntryId: null,
            baselineMeasurement,
            new[] { candidateMeasurement });

        var regressionResult = _regressionGuard.Evaluate(watch);

        ExperimentVerdict verdict;
        string explanation;
        bool shouldKeep = false;
        bool requiresRollback = false;

        if (regressionResult.Status == RegressionStatus.Confirmed || deltaOneLow <= -5.0 || deltaP99 >= 10.0)
        {
            verdict = ExperimentVerdict.RegressionDetected;
            explanation = $"REGRESIÓN DETECTADA: Caída del 1% Low ({deltaOneLow:+0.0;-0.0}%) o incremento en latencia P99 ({deltaP99:+0.0;-0.0}%). Se recomienda revertir de inmediato.";
            requiresRollback = true;
            shouldKeep = false;
        }
        else if (deltaOneLow >= 3.0 && deltaP99 <= 0)
        {
            verdict = ExperimentVerdict.ImprovementVerified;
            explanation = $"MEJORA OBSERVADA en una captura por condición: 1% Low cambió {deltaOneLow:+0.0;-0.0}% y P99 {deltaP99:+0.0;-0.0}%. Repetí el mismo escenario para confirmar; esta comparación aislada no demuestra causalidad.";
            shouldKeep = true;
            requiresRollback = false;
        }
        else if (isGpuBound && Math.Abs(deltaAvgFps) < 2.0 && deltaOneLow > 0)
        {
            verdict = ExperimentVerdict.GpuBottleneckDominant;
            explanation = $"USO DE GPU ELEVADO OBSERVADO: FPS promedio cambió {deltaAvgFps:+0.0;-0.0}% y 1% Low {deltaOneLow:+0.0;-0.0}%. El dato no identifica por sí solo un cuello de botella ni demuestra causalidad.";
            shouldKeep = deltaOneLow >= 2.0;
            requiresRollback = false;
        }
        else
        {
            verdict = ExperimentVerdict.Inconclusive;
            explanation = $"DIFERENCIA NO SIGNIFICATIVA: La variación ({deltaOneLow:+0.0;-0.0}% en 1% Low, {deltaAvgFps:+0.0;-0.0}% en promedio) se encuentra dentro del margen de ruido estadístico (±2%).";
            shouldKeep = false;
            requiresRollback = false;
        }

        notes.Add($"Variación FPS Promedio: {deltaAvgFps:+0.0;-0.0}%");
        notes.Add($"Variación 1% Low: {deltaOneLow:+0.0;-0.0}%");
        notes.Add($"Variación P99 Frametime: {deltaP99:+0.0;-0.0}%");
        notes.Add($"Variación Tasa de Stutter: {deltaStutter:+0.0;-0.0}%");

        return new Cs2ComparisonResult(
            baseline,
            candidate,
            deltaAvgFps,
            deltaOneLow,
            deltaPointOneLow,
            deltaP99,
            deltaStutter,
            verdict,
            explanation,
            shouldKeep,
            requiresRollback,
            notes);
    }

    private static double CalculateDelta(double baseline, double current)
    {
        if (baseline <= 0) return 0;
        return ((current - baseline) / baseline) * 100d;
    }
}
