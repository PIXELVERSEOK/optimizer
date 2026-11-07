namespace EvoOptimizer.Core.Optimization;

/// <summary>
/// Tipo de modificación sujeta a medición. La evaluación de evidencia es común
/// para Windows, CPU, GPU, RAM, procesos, almacenamiento y gaming.
/// </summary>
public enum OptimizationModificationKind
{
    Windows,
    Cpu,
    Gpu,
    Memory,
    Process,
    Storage,
    Gaming,
    Other
}

public enum OptimizationEvidenceVerdict
{
    InsufficientData,
    Inconclusive,
    ImprovementVerified,
    TradeOff,
    RegressionDetected
}

/// <summary>
/// Métricas disponibles de una ventana de medición. Los sensores ausentes
/// quedan en null: nunca se inventan valores.
/// </summary>
public sealed record OptimizationMetricValues(
    double? AverageFps = null,
    double? OnePercentLowFps = null,
    double? PointOnePercentLowFps = null,
    double? P95FrametimeMs = null,
    double? P99FrametimeMs = null,
    double? StutterRatePercent = null,
    double? CpuUsagePercent = null,
    double? GpuUsagePercent = null,
    double? MemoryUsagePercent = null,
    double? CommitUsedMb = null,
    int? ActiveProcessCount = null,
    double? DiskActivePercent = null,
    double? CpuTemperatureCelsius = null,
    double? GpuTemperatureCelsius = null,
    double? CpuPowerWatts = null,
    double? CpuFrequencyMhz = null,
    double? LatencyMs = null,
    int ErrorCount = 0,
    int FrameCount = 0,
    int SampleCount = 0,
    double DurationSeconds = 0);

public sealed record OptimizationMeasurementContext(
    Guid ModificationId,
    string ModificationKey,
    string ModificationName,
    OptimizationModificationKind Kind,
    string GameId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    string HardwareId,
    string EnvironmentId);

public sealed record OptimizationMeasurement(
    Guid Id,
    OptimizationMeasurementContext Context,
    DateTimeOffset CapturedAt,
    OptimizationMetricValues Metrics,
    IReadOnlyList<string> Notes);

public sealed record OptimizationMetricComparison(
    string Name,
    string Group,
    double Baseline,
    double Candidate,
    double AbsoluteChange,
    double PercentChange,
    bool HigherIsBetter,
    bool IsPrimary,
    bool IsObservational,
    bool IsImprovement,
    bool IsRegression,
    string Assessment);

public sealed record OptimizationEvidenceResult(
    Guid ModificationId,
    string ModificationKey,
    string ModificationName,
    OptimizationEvidenceVerdict Verdict,
    bool ShouldKeep,
    bool ShouldRollback,
    bool RequiresUserDecision,
    IReadOnlyList<OptimizationMetricComparison> Metrics,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings,
    DateTimeOffset EvaluatedAt)
{
    public string VerdictLabel => Verdict switch
    {
        OptimizationEvidenceVerdict.ImprovementVerified => "MEJORA VERIFICADA",
        OptimizationEvidenceVerdict.RegressionDetected => "REGRESIÓN DETECTADA",
        OptimizationEvidenceVerdict.TradeOff => "INTERCAMBIO (BENEFICIO CON PERJUICIO)",
        OptimizationEvidenceVerdict.Inconclusive => "INCONCLUSO",
        _ => "DATOS INSUFICIENTES"
    };
}

public sealed record OptimizationEvidenceOptions(
    int MinimumSamples = 5,
    double MinimumDurationSeconds = 10,
    int MinimumFpsFrames = 1000,
    double MinimumFpsDurationSeconds = 30,
    double FpsImprovementThresholdPercent = 2,
    double FpsRegressionThresholdPercent = 5,
    double LowImprovementThresholdPercent = 3,
    double LowRegressionThresholdPercent = 5,
    double FrametimeImprovementThresholdPercent = 3,
    double FrametimeRegressionThresholdPercent = 10,
    double ResourceImprovementThresholdPercent = 5,
    double ResourceRegressionThresholdPercent = 10,
    double TemperatureRegressionCelsius = 5,
    double LatencyRegressionPercent = 15,
    int ErrorRegressionCount = 1);

/// <summary>
/// Aplica la regla global de medición: sólo conserva un cambio si demuestra
/// beneficio consistente sin perjuicios. Si faltan sensores o muestras,
/// devuelve INSUFFICIENT_DATA/INCONCLUSIVE y no lo declara beneficioso.
/// </summary>
public sealed class OptimizationEvidenceEvaluator
{
    public OptimizationEvidenceResult Evaluate(
        OptimizationMeasurement baseline,
        OptimizationMeasurement candidate,
        OptimizationEvidenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        options ??= new OptimizationEvidenceOptions();

        var warnings = new List<string>();
        var evidence = new List<string>();
        var metrics = new List<OptimizationMetricComparison>();

        if (!Comparable(baseline.Context, candidate.Context))
        {
            warnings.Add(
                "La línea base y la medición posterior no comparten contexto " +
                "(juego, escenario, resolución, fuente, hardware o entorno). " +
                "No se puede atribuir el resultado al cambio.");

            return BuildResult(
                baseline,
                candidate,
                OptimizationEvidenceVerdict.InsufficientData,
                shouldKeep: false,
                shouldRollback: false,
                requiresUserDecision: true,
                metrics,
                evidence,
                warnings);
        }

        if (!HasEnoughSamples(baseline, options, warnings) ||
            !HasEnoughSamples(candidate, options, warnings))
        {
            return BuildResult(
                baseline,
                candidate,
                OptimizationEvidenceVerdict.InsufficientData,
                shouldKeep: false,
                shouldRollback: false,
                requiresUserDecision: true,
                metrics,
                evidence,
                warnings);
        }

        var kind = baseline.Context.Kind;

        bool fpsRelevant = kind is not (
            OptimizationModificationKind.Process or
            OptimizationModificationKind.Memory or
            OptimizationModificationKind.Storage);

        bool fpsEvidence = HasFpsEvidence(baseline, candidate, options);

        if (fpsRelevant && !fpsEvidence)
        {
            warnings.Add(
                "FPS no medidos o evidencia FPS insuficiente: se requieren " +
                $"{options.MinimumFpsFrames} frames y " +
                $"{options.MinimumFpsDurationSeconds:F0} s en ambas ventanas. " +
                "No se puede verificar una mejora de rendimiento en juegos.");
        }

        bool cpuPrimary = kind == OptimizationModificationKind.Process;
        bool memoryPrimary = kind is OptimizationModificationKind.Process
            or OptimizationModificationKind.Memory;
        bool commitPrimary = kind is OptimizationModificationKind.Process
            or OptimizationModificationKind.Memory;
        bool processCountPrimary = kind == OptimizationModificationKind.Process;
        bool diskPrimary = kind == OptimizationModificationKind.Storage;

        AddMetric(
            metrics, warnings,
            "FPS promedio", "FPS",
            baseline.Metrics.AverageFps, candidate.Metrics.AverageFps,
            higherIsBetter: true,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: options.FpsImprovementThresholdPercent,
            regressionThreshold: options.FpsRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "1% Low", "FPS",
            baseline.Metrics.OnePercentLowFps, candidate.Metrics.OnePercentLowFps,
            higherIsBetter: true,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: options.LowImprovementThresholdPercent,
            regressionThreshold: options.LowRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "0.1% Low", "FPS",
            baseline.Metrics.PointOnePercentLowFps,
            candidate.Metrics.PointOnePercentLowFps,
            higherIsBetter: true,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: options.LowImprovementThresholdPercent,
            regressionThreshold: options.LowRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "P95 Frametime", "Frametime",
            baseline.Metrics.P95FrametimeMs, candidate.Metrics.P95FrametimeMs,
            higherIsBetter: false,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: options.FrametimeImprovementThresholdPercent,
            regressionThreshold: options.FrametimeRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "P99 Frametime", "Frametime",
            baseline.Metrics.P99FrametimeMs, candidate.Metrics.P99FrametimeMs,
            higherIsBetter: false,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: options.FrametimeImprovementThresholdPercent,
            regressionThreshold: options.FrametimeRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "Stutter", "Estabilidad",
            baseline.Metrics.StutterRatePercent,
            candidate.Metrics.StutterRatePercent,
            higherIsBetter: false,
            primary: fpsRelevant && fpsEvidence,
            observational: !fpsEvidence,
            improvementThreshold: 0.5,
            regressionThreshold: 2.0,
            absolute: true);

        AddMetric(
            metrics, warnings,
            "CPU", "Sistema",
            baseline.Metrics.CpuUsagePercent, candidate.Metrics.CpuUsagePercent,
            higherIsBetter: false,
            primary: cpuPrimary,
            observational: false,
            improvementThreshold: options.ResourceImprovementThresholdPercent,
            regressionThreshold: options.ResourceRegressionThresholdPercent);

        // La utilización de GPU no se usa para declarar mejora ni regresión:
        // una GPU más ocupada puede significar mejor aprovechamiento o cuello
        // de botella, y sin contexto adicional sería una conclusión inventada.
        AddMetric(
            metrics, warnings,
            "GPU", "Sistema",
            baseline.Metrics.GpuUsagePercent, candidate.Metrics.GpuUsagePercent,
            higherIsBetter: false,
            primary: false,
            observational: true,
            improvementThreshold: double.PositiveInfinity,
            regressionThreshold: double.PositiveInfinity);

        AddMetric(
            metrics, warnings,
            "RAM", "Sistema",
            baseline.Metrics.MemoryUsagePercent,
            candidate.Metrics.MemoryUsagePercent,
            higherIsBetter: false,
            primary: memoryPrimary,
            observational: false,
            improvementThreshold: 3,
            regressionThreshold: 8);

        AddMetric(
            metrics, warnings,
            "Commit", "Sistema",
            baseline.Metrics.CommitUsedMb, candidate.Metrics.CommitUsedMb,
            higherIsBetter: false,
            primary: commitPrimary,
            observational: false,
            improvementThreshold: options.ResourceImprovementThresholdPercent,
            regressionThreshold: options.ResourceRegressionThresholdPercent);

        AddMetric(
            metrics, warnings,
            "Procesos activos", "Sistema",
            baseline.Metrics.ActiveProcessCount,
            candidate.Metrics.ActiveProcessCount,
            higherIsBetter: false,
            primary: processCountPrimary,
            observational: false,
            improvementThreshold: 1,
            regressionThreshold: 3,
            absolute: true);

        AddMetric(
            metrics, warnings,
            "Disco", "Almacenamiento",
            baseline.Metrics.DiskActivePercent,
            candidate.Metrics.DiskActivePercent,
            higherIsBetter: false,
            primary: diskPrimary,
            observational: false,
            improvementThreshold: options.ResourceImprovementThresholdPercent,
            regressionThreshold: 15);

        AddMetric(
            metrics, warnings,
            "Temperatura CPU", "Térmico",
            baseline.Metrics.CpuTemperatureCelsius,
            candidate.Metrics.CpuTemperatureCelsius,
            higherIsBetter: false,
            primary: false,
            observational: false,
            improvementThreshold: 2,
            regressionThreshold: options.TemperatureRegressionCelsius,
            absolute: true);

        AddMetric(
            metrics, warnings,
            "Temperatura GPU", "Térmico",
            baseline.Metrics.GpuTemperatureCelsius,
            candidate.Metrics.GpuTemperatureCelsius,
            higherIsBetter: false,
            primary: false,
            observational: false,
            improvementThreshold: 2,
            regressionThreshold: options.TemperatureRegressionCelsius,
            absolute: true);

        AddMetric(
            metrics, warnings,
            "Potencia CPU", "Energía",
            baseline.Metrics.CpuPowerWatts, candidate.Metrics.CpuPowerWatts,
            higherIsBetter: false,
            primary: false,
            observational: false,
            improvementThreshold: 5,
            regressionThreshold: 15);

        AddMetric(
            metrics, warnings,
            "Frecuencia CPU", "Energía",
            baseline.Metrics.CpuFrequencyMhz,
            candidate.Metrics.CpuFrequencyMhz,
            higherIsBetter: true,
            primary: false,
            observational: true,
            improvementThreshold: double.PositiveInfinity,
            regressionThreshold: double.PositiveInfinity);

        AddMetric(
            metrics, warnings,
            "Latencia", "Latencia",
            baseline.Metrics.LatencyMs, candidate.Metrics.LatencyMs,
            higherIsBetter: false,
            primary: false,
            observational: false,
            improvementThreshold: options.ResourceImprovementThresholdPercent,
            regressionThreshold: options.LatencyRegressionPercent);

        AddMetric(
            metrics, warnings,
            "Errores", "Estabilidad",
            baseline.Metrics.ErrorCount, candidate.Metrics.ErrorCount,
            higherIsBetter: false,
            primary: true,
            observational: false,
            improvementThreshold: 1,
            regressionThreshold: options.ErrorRegressionCount,
            absolute: true);

        var primaryImprovements = metrics
            .Where(x => x.IsPrimary && x.IsImprovement)
            .ToArray();

        var primaryRegressions = metrics
            .Where(x => x.IsPrimary && x.IsRegression)
            .ToArray();

        var secondaryRegressions = metrics
            .Where(x => !x.IsPrimary && x.IsRegression)
            .ToArray();

        bool hasPrimary = metrics.Any(x => x.IsPrimary);

        if (metrics.Count == 0)
        {
            warnings.Add(
                "No hay métricas comparables entre la línea base y la " +
                "medición posterior.");

            return BuildResult(
                baseline,
                candidate,
                OptimizationEvidenceVerdict.InsufficientData,
                shouldKeep: false,
                shouldRollback: false,
                requiresUserDecision: true,
                metrics,
                evidence,
                warnings);
        }

        OptimizationEvidenceVerdict verdict;
        bool shouldKeep = false;
        bool shouldRollback = false;
        bool requiresUserDecision = false;

        if (!hasPrimary)
        {
            if (secondaryRegressions.Length > 0)
            {
                verdict = OptimizationEvidenceVerdict.RegressionDetected;
                shouldRollback = true;
                warnings.Add(
                    "Se detectó un perjuicio en métricas secundarias aunque " +
                    "faltan métricas primarias para verificar una mejora.");
            }
            else
            {
                verdict = OptimizationEvidenceVerdict.InsufficientData;
                requiresUserDecision = true;
                warnings.Add(
                    "No hay métricas primarias disponibles. El cambio no se " +
                    "puede declarar beneficioso ni conservar automáticamente.");
            }
        }
        else if (primaryRegressions.Length > 0)
        {
            verdict = OptimizationEvidenceVerdict.RegressionDetected;
            shouldRollback = true;
        }
        else if (primaryImprovements.Length > 0 &&
                 secondaryRegressions.Length > 0)
        {
            verdict = OptimizationEvidenceVerdict.TradeOff;
            shouldRollback = true;
            requiresUserDecision = true;
        }
        else if (primaryImprovements.Length > 0)
        {
            verdict = OptimizationEvidenceVerdict.ImprovementVerified;
            shouldKeep = true;
        }
        else
        {
            verdict = OptimizationEvidenceVerdict.Inconclusive;
            requiresUserDecision = true;
        }

        evidence.Add(
            $"Contexto: {baseline.Context.GameId} · " +
            $"{baseline.Context.Scenario} · {baseline.Context.Resolution} · " +
            $"{baseline.Context.CaptureSource}");

        evidence.Add(
            $"Muestras: línea base {baseline.Metrics.SampleCount} / " +
            $"posterior {candidate.Metrics.SampleCount}; duración " +
            $"{baseline.Metrics.DurationSeconds:F1}s / " +
            $"{candidate.Metrics.DurationSeconds:F1}s.");

        if (fpsEvidence)
        {
            evidence.Add(
                $"Frames FPS: línea base {baseline.Metrics.FrameCount} / " +
                $"posterior {candidate.Metrics.FrameCount}.");
        }

        foreach (var metric in metrics)
        {
            evidence.Add(
                $"{metric.Name}: {metric.Baseline:F2} → " +
                $"{metric.Candidate:F2} " +
                $"({metric.PercentChange:+0.0;-0.0}%) · {metric.Assessment}");
        }

        foreach (var note in baseline.Notes.Concat(candidate.Notes)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            evidence.Add(note);
        }

        evidence.Add(
            $"Decisión: conservar={shouldKeep}, revertir={shouldRollback}, " +
            $"requiere decisión del usuario={requiresUserDecision}.");

        return BuildResult(
            baseline,
            candidate,
            verdict,
            shouldKeep,
            shouldRollback,
            requiresUserDecision,
            metrics,
            evidence,
            warnings);
    }

    private static void AddMetric(
        ICollection<OptimizationMetricComparison> output,
        ICollection<string> warnings,
        string name,
        string group,
        double? baseline,
        double? candidate,
        bool higherIsBetter,
        bool primary,
        bool observational,
        double improvementThreshold,
        double regressionThreshold,
        bool absolute = false)
    {
        if (!baseline.HasValue || !candidate.HasValue)
            return;

        if (!double.IsFinite(baseline.Value) ||
            !double.IsFinite(candidate.Value) ||
            baseline.Value < 0 ||
            candidate.Value < 0)
        {
            warnings.Add($"Métrica '{name}' inválida; se excluye de la evaluación.");
            return;
        }

        double change = candidate.Value - baseline.Value;

        double percent = Math.Abs(baseline.Value) > 0.000001
            ? change / Math.Abs(baseline.Value) * 100
            : 0;

        double magnitude = absolute ? Math.Abs(change) : Math.Abs(percent);

        bool goodDirection = higherIsBetter ? change > 0 : change < 0;

        bool improvement =
            !observational &&
            goodDirection &&
            magnitude >= improvementThreshold;

        bool regression =
            !observational &&
            !goodDirection &&
            magnitude >= regressionThreshold;

        string assessment = observational
            ? "Observacional"
            : improvement
                ? "Mejora"
                : regression
                    ? "Empeora"
                    : "Sin cambio significativo";

        output.Add(new OptimizationMetricComparison(
            name,
            group,
            baseline.Value,
            candidate.Value,
            change,
            percent,
            higherIsBetter,
            primary,
            observational,
            improvement,
            regression,
            assessment));
    }

    private static bool HasEnoughSamples(
        OptimizationMeasurement measurement,
        OptimizationEvidenceOptions options,
        ICollection<string> warnings)
    {
        if (measurement.Metrics.SampleCount < options.MinimumSamples)
        {
            warnings.Add(
                $"Muestras insuficientes: {measurement.Metrics.SampleCount}/" +
                $"{options.MinimumSamples}.");
            return false;
        }

        if (measurement.Metrics.DurationSeconds <
            options.MinimumDurationSeconds)
        {
            warnings.Add(
                $"Duración insuficiente: " +
                $"{measurement.Metrics.DurationSeconds:F1}s/" +
                $"{options.MinimumDurationSeconds:F0}s.");
            return false;
        }

        return true;
    }

    private static bool HasFpsEvidence(
        OptimizationMeasurement baseline,
        OptimizationMeasurement candidate,
        OptimizationEvidenceOptions options)
    {
        var b = baseline.Metrics;
        var c = candidate.Metrics;

        return b.AverageFps.HasValue &&
               c.AverageFps.HasValue &&
               b.OnePercentLowFps.HasValue &&
               c.OnePercentLowFps.HasValue &&
               b.PointOnePercentLowFps.HasValue &&
               c.PointOnePercentLowFps.HasValue &&
               b.P99FrametimeMs.HasValue &&
               c.P99FrametimeMs.HasValue &&
               b.FrameCount >= options.MinimumFpsFrames &&
               c.FrameCount >= options.MinimumFpsFrames &&
               b.DurationSeconds >= options.MinimumFpsDurationSeconds &&
               c.DurationSeconds >= options.MinimumFpsDurationSeconds;
    }

    private static bool Comparable(
        OptimizationMeasurementContext baseline,
        OptimizationMeasurementContext candidate) =>
        Same(baseline.GameId, candidate.GameId) &&
        Same(baseline.Scenario, candidate.Scenario) &&
        Same(baseline.Resolution, candidate.Resolution) &&
        Same(baseline.CaptureSource, candidate.CaptureSource) &&
        Same(baseline.HardwareId, candidate.HardwareId) &&
        Same(baseline.EnvironmentId, candidate.EnvironmentId);

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static OptimizationEvidenceResult BuildResult(
        OptimizationMeasurement baseline,
        OptimizationMeasurement candidate,
        OptimizationEvidenceVerdict verdict,
        bool shouldKeep,
        bool shouldRollback,
        bool requiresUserDecision,
        IReadOnlyList<OptimizationMetricComparison> metrics,
        IReadOnlyList<string> evidence,
        IReadOnlyList<string> warnings)
    {
        return new OptimizationEvidenceResult(
            baseline.Context.ModificationId,
            baseline.Context.ModificationKey,
            baseline.Context.ModificationName,
            verdict,
            shouldKeep,
            shouldRollback,
            requiresUserDecision,
            metrics,
            evidence,
            warnings,
            DateTimeOffset.UtcNow);
    }
}
