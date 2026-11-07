namespace EvoOptimizer.Core.Benchmark;

public enum PerformanceRegressionSeverity
{
    None,
    Information,
    Warning,
    Severe,
    Critical
}

public sealed record RegressionMetricFinding(
    string Metric,
    double BaselineValue,
    double CandidateValue,
    double ChangePercent,
    PerformanceRegressionSeverity Severity,
    string Explanation);

public sealed record PerformanceRegressionReport(
    PerformanceRegressionSeverity OverallSeverity,
    bool IsRegression,
    IReadOnlyList<RegressionMetricFinding> Findings,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ClassifiedAt);

public sealed class PerformanceRegressionClassifier
{
    public PerformanceRegressionReport Classify(
        BenchmarkRunResult baseline,
        BenchmarkRunResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var warnings = new List<string>();

        if (!SameContext(baseline, candidate))
        {
            warnings.Add(
                "Los resultados no comparten el mismo contexto de benchmark.");

            return new PerformanceRegressionReport(
                PerformanceRegressionSeverity.Information,
                false,
                Array.Empty<RegressionMetricFinding>(),
                warnings,
                DateTimeOffset.UtcNow);
        }

        var findings = new List<RegressionMetricFinding>
        {
            Evaluate(
                "Average FPS",
                baseline.AverageFps,
                candidate.AverageFps,
                lowerIsWorse: true,
                warningThreshold: 5,
                severeThreshold: 10,
                criticalThreshold: 20),

            Evaluate(
                "1% Low",
                baseline.OnePercentLow,
                candidate.OnePercentLow,
                lowerIsWorse: true,
                warningThreshold: 5,
                severeThreshold: 10,
                criticalThreshold: 20),

            Evaluate(
                "0.1% Low",
                baseline.PointOnePercentLow,
                candidate.PointOnePercentLow,
                lowerIsWorse: true,
                warningThreshold: 5,
                severeThreshold: 10,
                criticalThreshold: 20),

            Evaluate(
                "Frametime P99",
                baseline.FrametimeP99Ms,
                candidate.FrametimeP99Ms,
                lowerIsWorse: false,
                warningThreshold: 10,
                severeThreshold: 20,
                criticalThreshold: 35),

            Evaluate(
                "Stutter rate",
                baseline.StutterRate,
                candidate.StutterRate,
                lowerIsWorse: false,
                warningThreshold: 2,
                severeThreshold: 5,
                criticalThreshold: 10,
                absoluteDifference: true)
        };

        var overall = findings
            .Select(x => x.Severity)
            .OrderByDescending(x => (int)x)
            .First();

        bool regression = overall >= PerformanceRegressionSeverity.Warning;

        return new PerformanceRegressionReport(
            overall,
            regression,
            findings,
            warnings,
            DateTimeOffset.UtcNow);
    }

    private static RegressionMetricFinding Evaluate(
        string name,
        double baseline,
        double candidate,
        bool lowerIsWorse,
        double warningThreshold,
        double severeThreshold,
        double criticalThreshold,
        bool absoluteDifference = false)
    {
        if (!double.IsFinite(baseline) ||
            !double.IsFinite(candidate) ||
            baseline <= 0 ||
            candidate < 0)
        {
            return new RegressionMetricFinding(
                name, baseline, candidate, 0,
                PerformanceRegressionSeverity.Information,
                "Métrica inválida o no disponible.");
        }

        var change = absoluteDifference
            ? candidate - baseline
            : (candidate - baseline) / baseline * 100.0;

        var degradation = absoluteDifference
            ? change
            : lowerIsWorse ? -change : change;

        var severity = degradation >= criticalThreshold
            ? PerformanceRegressionSeverity.Critical
            : degradation >= severeThreshold
                ? PerformanceRegressionSeverity.Severe
                : degradation >= warningThreshold
                    ? PerformanceRegressionSeverity.Warning
                    : PerformanceRegressionSeverity.None;

        return new RegressionMetricFinding(
            name,
            baseline,
            candidate,
            absoluteDifference ? change : Math.Round(change, 2),
            severity,
            severity == PerformanceRegressionSeverity.None
                ? "Sin regresión relevante según el umbral configurado."
                : $"Degradación detectada: {degradation:F2}.");
    }

    private static bool SameContext(
        BenchmarkRunResult a,
        BenchmarkRunResult b)
    {
        return Same(a.GameId, b.GameId) &&
               Same(a.HardwareId, b.HardwareId) &&
               Same(a.EnvironmentId, b.EnvironmentId) &&
               Same(a.Scenario, b.Scenario) &&
               Same(a.Resolution, b.Resolution) &&
               Same(a.CaptureSource, b.CaptureSource);
    }

    private static bool Same(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) &&
        !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
