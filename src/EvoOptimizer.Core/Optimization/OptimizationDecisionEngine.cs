using EvoOptimizer.Core.Learning;

namespace EvoOptimizer.Core.Optimization;

public enum DecisionPriority
{
    Critical,
    High,
    Normal,
    Low
}

public enum DecisionRisk
{
    Low,
    Medium,
    High
}

public enum DecisionDisposition
{
    Recommended,
    Investigate,
    BlockedByEvidence,
    SuppressedByHistory,
    InsufficientEvidence
}

public sealed record OptimizationDecisionContext(
    string GameId,
    string HardwareFingerprint,
    IReadOnlyList<OptimizationSignal> Signals,
    IReadOnlyList<OptimizationKnowledge> HistoricalKnowledge,
    bool BenchmarkAvailable);

public sealed record OptimizationDecision(
    string Id,
    string Title,
    string Description,
    string Evidence,
    DecisionPriority Priority,
    DecisionRisk Risk,
    DecisionDisposition Disposition,
    double Confidence,
    bool IsReversible,
    bool RequiresBenchmark,
    bool RequiresUserApproval,
    bool CanApply,
    string Reason);

public sealed record OptimizationDecisionReport(
    DateTimeOffset GeneratedAt,
    string GameId,
    string HardwareFingerprint,
    int SignalsEvaluated,
    int DecisionsGenerated,
    int RecommendedCount,
    int InvestigationCount,
    int BlockedCount,
    IReadOnlyList<OptimizationDecision> Decisions,
    IReadOnlyList<string> GlobalWarnings);

public sealed class OptimizationDecisionEngine
{
    private const double MinimumConfidence = 0.75;
    private const double ApplyConfidence = 0.90;

    private sealed record Rule(
        string SignalCode,
        string Id,
        string Title,
        string Description,
        DecisionPriority Priority,
        DecisionRisk Risk,
        bool Reversible,
        bool RequiresBenchmark,
        bool CanApply);

    private static readonly Rule[] Rules =
    [
        new(
            "GAMEDVR_CAPTURE_ENABLED",
            "DISABLE_BACKGROUND_CAPTURE",
            "Desactivar captura en segundo plano",
            "Reducir actividad de captura durante el juego.",
            DecisionPriority.Normal,
            DecisionRisk.Low,
            true,
            true,
            true),

        new(
            "HISTORICAL_CAPTURE_ENABLED",
            "DISABLE_HISTORICAL_CAPTURE",
            "Desactivar captura histórica",
            "Evitar grabación histórica innecesaria.",
            DecisionPriority.Normal,
            DecisionRisk.Low,
            true,
            true,
            true),

        new(
            "GAME_MODE_DISABLED",
            "ENABLE_GAME_MODE",
            "Activar Modo Juego",
            "Evaluar el uso del Modo Juego de Windows.",
            DecisionPriority.Normal,
            DecisionRisk.Low,
            true,
            true,
            true),

        new(
            "HIGH_CPU_PRESSURE",
            "INVESTIGATE_CPU_PRESSURE",
            "Investigar presión de CPU",
            "Identificar procesos y cargas que compiten con el juego.",
            DecisionPriority.High,
            DecisionRisk.Low,
            false,
            false,
            false),

        new(
            "HIGH_MEMORY_PRESSURE",
            "INVESTIGATE_MEMORY_PRESSURE",
            "Investigar presión de memoria",
            "Identificar consumo elevado y disponibilidad insuficiente de RAM.",
            DecisionPriority.High,
            DecisionRisk.Low,
            false,
            false,
            false),

        new(
            "GPU_BOTTLENECK",
            "INVESTIGATE_GPU_BOTTLENECK",
            "Investigar límite de GPU",
            "Analizar carga gráfica y frametimes antes de modificar ajustes.",
            DecisionPriority.High,
            DecisionRisk.Low,
            false,
            false,
            false),

        new(
            "STORAGE_PRESSURE",
            "INVESTIGATE_STORAGE_PRESSURE",
            "Investigar almacenamiento",
            "Revisar actividad, latencia y espacio disponible.",
            DecisionPriority.Normal,
            DecisionRisk.Low,
            false,
            false,
            false),

        new(
            "BACKGROUND_ACTIVITY_SPIKE",
            "INVESTIGATE_BACKGROUND_ACTIVITY",
            "Investigar actividad en segundo plano",
            "Identificar procesos que aumentaron su actividad durante la sesión.",
            DecisionPriority.High,
            DecisionRisk.Low,
            false,
            false,
            false)
    ];

    public OptimizationDecisionReport Evaluate(
        OptimizationDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(context.Signals);
        ArgumentNullException.ThrowIfNull(context.HistoricalKnowledge);

        var decisions = new List<OptimizationDecision>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(context.GameId))
            warnings.Add("No se especificó el juego.");

        if (string.IsNullOrWhiteSpace(context.HardwareFingerprint))
            warnings.Add("No se especificó la identificación del hardware.");

        var activeSignals = context.Signals
            .Where(x => x.IsPresent)
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(s => s.Confidence).First())
            .ToArray();

        foreach (var signal in activeSignals)
        {
            var rule = Rules.FirstOrDefault(x =>
                string.Equals(
                    x.SignalCode,
                    signal.Code,
                    StringComparison.OrdinalIgnoreCase));

            if (rule is null)
                continue;

            decisions.Add(CreateDecision(
                rule,
                signal,
                context));
        }

        var ordered = decisions
            .OrderBy(x => x.Disposition switch
            {
                DecisionDisposition.Recommended => 0,
                DecisionDisposition.Investigate => 1,
                DecisionDisposition.InsufficientEvidence => 2,
                DecisionDisposition.BlockedByEvidence => 3,
                DecisionDisposition.SuppressedByHistory => 4,
                _ => 5
            })
            .ThenBy(x => x.Priority)
            .ThenByDescending(x => x.Confidence)
            .ToArray();

        if (!context.BenchmarkAvailable)
        {
            warnings.Add(
                "No hay benchmark válido. Las acciones que requieren medición quedan bloqueadas.");
        }

        if (activeSignals.Length == 0)
        {
            warnings.Add(
                "No se detectaron señales activas compatibles con las reglas actuales.");
        }

        return new OptimizationDecisionReport(
            DateTimeOffset.UtcNow,
            context.GameId,
            context.HardwareFingerprint,
            context.Signals.Count,
            ordered.Length,
            ordered.Count(x =>
                x.Disposition == DecisionDisposition.Recommended),
            ordered.Count(x =>
                x.Disposition == DecisionDisposition.Investigate),
            ordered.Count(x =>
                x.Disposition is DecisionDisposition.BlockedByEvidence
                    or DecisionDisposition.SuppressedByHistory),
            ordered,
            warnings);
    }

    private static OptimizationDecision CreateDecision(
        Rule rule,
        OptimizationSignal signal,
        OptimizationDecisionContext context)
    {
        var knowledge = context.HistoricalKnowledge.FirstOrDefault(x =>
            string.Equals(
                x.GameId,
                context.GameId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                x.HardwareFingerprint,
                context.HardwareFingerprint,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                x.OptimizationId,
                rule.Id,
                StringComparison.OrdinalIgnoreCase));

        var confidence = Math.Clamp(signal.Confidence, 0, 1);

        var disposition = DecisionDisposition.Recommended;
        var reason = "La señal está respaldada por evidencia suficiente.";

        if (confidence < MinimumConfidence)
        {
            disposition = DecisionDisposition.InsufficientEvidence;
            reason = "La confianza de la señal no alcanza el mínimo requerido.";
        }
        else if (knowledge?.RecommendedOutcome ==
                 LearningOutcome.StrongRegression)
        {
            disposition = DecisionDisposition.SuppressedByHistory;
            reason = "El historial de este hardware muestra regresiones repetidas.";
        }
        else if (knowledge?.RecommendedOutcome ==
                 LearningOutcome.Mixed)
        {
            disposition = DecisionDisposition.BlockedByEvidence;
            reason = "El historial muestra resultados inconsistentes.";
        }
        else if (rule.RequiresBenchmark && !context.BenchmarkAvailable)
        {
            disposition = DecisionDisposition.BlockedByEvidence;
            reason = "Se necesita un benchmark comparable antes de probar el cambio.";
        }
        else if (!rule.CanApply)
        {
            disposition = DecisionDisposition.Investigate;
            reason = "Es necesario investigar la causa antes de proponer una modificación.";
        }
        else if (knowledge is not null &&
                 knowledge.Experiments >= 3 &&
                 knowledge.RecommendedOutcome ==
                 LearningOutcome.StrongImprovement)
        {
            confidence = Math.Min(0.99, confidence + 0.05);
            reason = "La señal y los experimentos históricos respaldan esta prueba.";
        }

        var canApply =
            disposition == DecisionDisposition.Recommended &&
            rule.CanApply &&
            rule.Reversible &&
            confidence >= ApplyConfidence &&
            context.BenchmarkAvailable;

        return new OptimizationDecision(
            rule.Id,
            rule.Title,
            rule.Description,
            signal.Evidence,
            rule.Priority,
            rule.Risk,
            disposition,
            Math.Round(confidence, 2),
            rule.Reversible,
            rule.RequiresBenchmark,
            RequiresUserApproval: rule.CanApply,
            CanApply: canApply,
            reason);
    }
}
