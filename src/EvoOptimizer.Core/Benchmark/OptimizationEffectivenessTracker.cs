namespace EvoOptimizer.Core.Benchmark;

public enum OptimizationEffectiveness
{
    Unknown,
    Beneficial,
    Neutral,
    Harmful,
    Inconclusive
}

public sealed record OptimizationEffectivenessObservation(
    Guid Id,
    string OptimizationId,
    string GameId,
    string HardwareId,
    string EnvironmentId,
    string Scenario,
    string Resolution,
    double AverageFpsChangePercent,
    double OnePercentLowChangePercent,
    double PointOnePercentLowChangePercent,
    double FrametimeP99ChangePercent,
    double StutterRateChange,
    OptimizationEffectiveness Outcome,
    DateTimeOffset EvaluatedAt);

public sealed record OptimizationEffectivenessSummary(
    string OptimizationId,
    int TotalObservations,
    int BeneficialCount,
    int NeutralCount,
    int HarmfulCount,
    int InconclusiveCount,
    double BeneficialRatePercent,
    double AverageOnePercentLowChange,
    double AverageP99Change,
    OptimizationEffectiveness Overall,
    DateTimeOffset CalculatedAt);

public sealed class OptimizationEffectivenessTracker
{
    public OptimizationEffectivenessObservation Evaluate(
        string optimizationId,
        BenchmarkRunResult baseline,
        BenchmarkRunResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(optimizationId))
            throw new ArgumentException(
                "Falta el identificador de la optimización.");

        if (!SameContext(baseline, candidate))
            throw new InvalidOperationException(
                "No se puede medir efectividad entre contextos diferentes.");

        if (!Valid(baseline) || !Valid(candidate))
            throw new InvalidOperationException(
                "Las métricas de comparación no son válidas.");

        double avg = PercentChange(
            baseline.AverageFps, candidate.AverageFps);

        double low = PercentChange(
            baseline.OnePercentLow, candidate.OnePercentLow);

        double low01 = PercentChange(
            baseline.PointOnePercentLow, candidate.PointOnePercentLow);

        double p99 = PercentChange(
            baseline.FrametimeP99Ms, candidate.FrametimeP99Ms);

        double stutter = candidate.StutterRate - baseline.StutterRate;

        // Prioriza 1% low, 0.1% low y P99.
        // FPS promedio no puede compensar por sí solo una caída grave.
        bool harmful =
            low <= -5 ||
            low01 <= -5 ||
            p99 >= 10 ||
            stutter >= 2;

        bool beneficial =
            low >= 3 &&
            low01 >= 3 &&
            p99 <= -3 &&
            stutter <= 0.5;

        var outcome = harmful
            ? OptimizationEffectiveness.Harmful
            : beneficial
                ? OptimizationEffectiveness.Beneficial
                : Math.Abs(low) <= 2 &&
                  Math.Abs(low01) <= 2 &&
                  Math.Abs(p99) <= 2 &&
                  Math.Abs(stutter) <= 0.5
                    ? OptimizationEffectiveness.Neutral
                    : OptimizationEffectiveness.Inconclusive;

        return new OptimizationEffectivenessObservation(
            Guid.NewGuid(),
            optimizationId.Trim(),
            baseline.GameId,
            baseline.HardwareId,
            baseline.EnvironmentId,
            baseline.Scenario,
            baseline.Resolution,
            avg,
            low,
            low01,
            p99,
            stutter,
            outcome,
            DateTimeOffset.UtcNow);
    }

    public OptimizationEffectivenessSummary Summarize(
        string optimizationId,
        IEnumerable<OptimizationEffectivenessObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var items = observations
            .Where(x => string.Equals(
                x.OptimizationId,
                optimizationId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        int beneficial = items.Count(
            x => x.Outcome == OptimizationEffectiveness.Beneficial);

        int neutral = items.Count(
            x => x.Outcome == OptimizationEffectiveness.Neutral);

        int harmful = items.Count(
            x => x.Outcome == OptimizationEffectiveness.Harmful);

        int inconclusive = items.Count(
            x => x.Outcome == OptimizationEffectiveness.Inconclusive);

        int total = items.Length;

        var overall = total == 0
            ? OptimizationEffectiveness.Unknown
            : harmful > 0
                ? OptimizationEffectiveness.Harmful
                : beneficial >= 3 && beneficial > neutral + inconclusive
                    ? OptimizationEffectiveness.Beneficial
                    : neutral >= 3 && neutral > beneficial + harmful
                        ? OptimizationEffectiveness.Neutral
                        : OptimizationEffectiveness.Inconclusive;

        return new OptimizationEffectivenessSummary(
            optimizationId,
            total,
            beneficial,
            neutral,
            harmful,
            inconclusive,
            total == 0 ? 0 : beneficial * 100.0 / total,
            total == 0 ? 0 : items.Average(
                x => x.OnePercentLowChangePercent),
            total == 0 ? 0 : items.Average(
                x => x.FrametimeP99ChangePercent),
            overall,
            DateTimeOffset.UtcNow);
    }

    private static bool Valid(BenchmarkRunResult x) =>
        double.IsFinite(x.AverageFps) && x.AverageFps > 0 &&
        double.IsFinite(x.OnePercentLow) && x.OnePercentLow > 0 &&
        double.IsFinite(x.PointOnePercentLow) && x.PointOnePercentLow > 0 &&
        double.IsFinite(x.FrametimeP99Ms) && x.FrametimeP99Ms > 0 &&
        double.IsFinite(x.StutterRate) && x.StutterRate >= 0;

    private static double PercentChange(double before, double after) =>
        (after - before) / before * 100.0;

    private static bool SameContext(
        BenchmarkRunResult a,
        BenchmarkRunResult b) =>
        Same(a.GameId, b.GameId) &&
        Same(a.HardwareId, b.HardwareId) &&
        Same(a.EnvironmentId, b.EnvironmentId) &&
        Same(a.Scenario, b.Scenario) &&
        Same(a.Resolution, b.Resolution) &&
        Same(a.CaptureSource, b.CaptureSource);

    private static bool Same(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) &&
        !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
