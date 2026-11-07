namespace EvoOptimizer.Core.Optimization;

public enum OptimizationAction
{
    DisableBackgroundCapture,
    DisableHistoricalCapture,
    EnableGameMode,
    InvestigateCpuPressure,
    InvestigateMemoryPressure,
    InvestigateGpuBottleneck,
    InvestigateStoragePressure,
    NoAction
}

public enum OptimizationRisk
{
    None,
    Low,
    Medium,
    High
}

public enum OptimizationPriority
{
    Informational,
    Low,
    Normal,
    High,
    Critical
}

public enum OptimizationBenefit
{
    Unknown,
    Low,
    Moderate,
    PotentiallyHigh
}

public sealed record OptimizationSignal(
    string Code,
    bool IsPresent,
    double Confidence,
    string Evidence);

public sealed record AdaptiveOptimizationRecommendation(
    Guid Id,
    OptimizationAction Action,
    string Title,
    string Explanation,
    OptimizationPriority Priority,
    OptimizationRisk Risk,
    OptimizationBenefit ExpectedBenefit,
    double ConfidencePercent,
    IReadOnlyList<string> Evidence,
    bool IsReversible,
    bool RequiresUserApproval,
    bool RequiresBenchmark,
    bool CanApply);

public sealed record AdaptiveOptimizationReport(
    DateTimeOffset GeneratedAt,
    int SignalsEvaluated,
    int RecommendationsCount,
    int ApplicableRecommendations,
    IReadOnlyList<AdaptiveOptimizationRecommendation> Recommendations,
    IReadOnlyList<string> Warnings);

public sealed class AdaptiveOptimizationEngine
{
    private const double MinimumActionConfidence = 0.75;

    public AdaptiveOptimizationReport Evaluate(
        IEnumerable<OptimizationSignal> input,
        bool benchmarkAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        var signals = input
            .Where(IsValidSignal)
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => x.Confidence)
                .First())
            .ToArray();

        var recommendations =
            new List<AdaptiveOptimizationRecommendation>();

        var warnings = new List<string>();

        foreach (var signal in signals.Where(x => x.IsPresent))
        {
            var recommendation = EvaluateSignal(
                signal,
                benchmarkAvailable);

            if (recommendation is not null)
                recommendations.Add(recommendation);
        }

        if (recommendations.Count == 0)
        {
            warnings.Add(
                "No hay ajustes justificados por las señales recibidas.");

            warnings.Add(
                "No se recomienda modificar Windows sin evidencia suficiente.");
        }

        if (!benchmarkAvailable &&
            recommendations.Any(x => x.RequiresBenchmark))
        {
            warnings.Add(
                "No hay benchmark de referencia. Los cambios no podrán considerarse mejoras demostradas.");
        }

        var ordered = recommendations
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.ConfidencePercent)
            .ToArray();

        return new AdaptiveOptimizationReport(
            DateTimeOffset.UtcNow,
            signals.Length,
            ordered.Length,
            ordered.Count(x => x.CanApply),
            ordered,
            warnings);
    }

    private static bool IsValidSignal(OptimizationSignal signal)
    {
        return !string.IsNullOrWhiteSpace(signal.Code) &&
               double.IsFinite(signal.Confidence) &&
               signal.Confidence is >= 0 and <= 1;
    }

    private static AdaptiveOptimizationRecommendation? EvaluateSignal(
        OptimizationSignal signal,
        bool benchmarkAvailable)
    {
        var confidence = signal.Confidence * 100;

        switch (signal.Code.Trim().ToUpperInvariant())
        {
            case "GAMEDVR_CAPTURE_ENABLED":
                return Create(
                    OptimizationAction.DisableBackgroundCapture,
                    "Desactivar captura en segundo plano",
                    "La captura de Windows está habilitada. Puede consumir recursos durante el juego. Conviene medir su impacto antes de conservar el cambio.",
                    signal,
                    OptimizationPriority.Normal,
                    OptimizationRisk.Low,
                    OptimizationBenefit.Low,
                    benchmarkAvailable);

            case "HISTORICAL_CAPTURE_ENABLED":
                return Create(
                    OptimizationAction.DisableHistoricalCapture,
                    "Desactivar captura histórica",
                    "La captura histórica está habilitada. Se puede evaluar su desactivación como ajuste reversible.",
                    signal,
                    OptimizationPriority.Normal,
                    OptimizationRisk.Low,
                    OptimizationBenefit.Low,
                    benchmarkAvailable);

            case "GAME_MODE_DISABLED":
                return Create(
                    OptimizationAction.EnableGameMode,
                    "Habilitar Modo Juego",
                    "El Modo Juego aparece deshabilitado. Se recomienda probarlo y comparar los frametimes antes y después.",
                    signal,
                    OptimizationPriority.Normal,
                    OptimizationRisk.Low,
                    OptimizationBenefit.Moderate,
                    benchmarkAvailable);

            case "HIGH_CPU_PRESSURE":
                return Investigate(
                    OptimizationAction.InvestigateCpuPressure,
                    "Investigar presión de CPU",
                    "Hay evidencia de carga elevada del procesador. Identificar el proceso responsable antes de cambiar prioridades o afinidades.",
                    signal,
                    OptimizationPriority.High,
                    OptimizationRisk.None);

            case "HIGH_MEMORY_PRESSURE":
                return Investigate(
                    OptimizationAction.InvestigateMemoryPressure,
                    "Investigar presión de memoria",
                    "La memoria disponible es reducida. Identificar consumo real y procesos responsables antes de cerrar aplicaciones.",
                    signal,
                    OptimizationPriority.High,
                    OptimizationRisk.None);

            case "GPU_BOTTLENECK":
                return Investigate(
                    OptimizationAction.InvestigateGpuBottleneck,
                    "Investigar límite de GPU",
                    "La evidencia apunta a una posible limitación gráfica. Comparar resolución, carga y frametimes antes de recomendar cambios.",
                    signal,
                    OptimizationPriority.High,
                    OptimizationRisk.None);

            case "STORAGE_PRESSURE":
                return Investigate(
                    OptimizationAction.InvestigateStoragePressure,
                    "Investigar actividad del almacenamiento",
                    "Se detectó posible presión de almacenamiento. Revisar latencia y actividad de disco antes de modificar servicios.",
                    signal,
                    OptimizationPriority.Normal,
                    OptimizationRisk.None);

            default:
                return null;
        }
    }

    private static AdaptiveOptimizationRecommendation Create(
        OptimizationAction action,
        string title,
        string explanation,
        OptimizationSignal signal,
        OptimizationPriority priority,
        OptimizationRisk risk,
        OptimizationBenefit benefit,
        bool benchmarkAvailable)
    {
        var confidence = signal.Confidence * 100;

        var canApply =
            signal.Confidence >= MinimumActionConfidence &&
            risk <= OptimizationRisk.Low;

        return new AdaptiveOptimizationRecommendation(
            Guid.NewGuid(),
            action,
            title,
            explanation,
            priority,
            risk,
            benefit,
            confidence,
            new[] { signal.Evidence },
            IsReversible: true,
            RequiresUserApproval: true,
            RequiresBenchmark: true,
            CanApply: canApply);
    }

    private static AdaptiveOptimizationRecommendation Investigate(
        OptimizationAction action,
        string title,
        string explanation,
        OptimizationSignal signal,
        OptimizationPriority priority,
        OptimizationRisk risk)
    {
        return new AdaptiveOptimizationRecommendation(
            Guid.NewGuid(),
            action,
            title,
            explanation,
            priority,
            risk,
            OptimizationBenefit.Unknown,
            signal.Confidence * 100,
            new[] { signal.Evidence },
            IsReversible: false,
            RequiresUserApproval: false,
            RequiresBenchmark: true,
            CanApply: false);
    }
}
