using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Benchmark;

public enum OptimizationFeedbackAction
{
    CollectMoreEvidence,
    KeepCurrentRecommendation,
    MarkAsEffective,
    FlagForReview,
    RecommendRollbackReview
}

public sealed record OptimizationFeedbackReport(
    string OptimizationId,
    OptimizationEffectiveness Overall,
    OptimizationFeedbackAction Action,
    int ObservationCount,
    double BeneficialRatePercent,
    string Explanation,
    IReadOnlyList<string> Evidence,
    DateTimeOffset GeneratedAt);

public sealed class OptimizationFeedbackEngine
{
    private readonly OptimizationEffectivenessTracker _tracker;

    public OptimizationFeedbackEngine(
        OptimizationEffectivenessTracker tracker)
    {
        _tracker = tracker ??
            throw new ArgumentNullException(nameof(tracker));
    }

    public OptimizationFeedbackReport Evaluate(
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

        var summary = _tracker.Summarize(optimizationId, items);
        var evidence = new List<string>();

        evidence.Add($"Observaciones: {summary.TotalObservations}");
        evidence.Add($"Beneficiosas: {summary.BeneficialCount}");
        evidence.Add($"Neutras: {summary.NeutralCount}");
        evidence.Add($"Perjudiciales: {summary.HarmfulCount}");

        var action = summary.Overall switch
        {
            OptimizationEffectiveness.Beneficial =>
                OptimizationFeedbackAction.MarkAsEffective,

            OptimizationEffectiveness.Harmful =>
                OptimizationFeedbackAction.RecommendRollbackReview,

            OptimizationEffectiveness.Neutral =>
                OptimizationFeedbackAction.KeepCurrentRecommendation,

            OptimizationEffectiveness.Unknown =>
                OptimizationFeedbackAction.CollectMoreEvidence,

            _ =>
                OptimizationFeedbackAction.FlagForReview
        };

        var explanation = action switch
        {
            OptimizationFeedbackAction.MarkAsEffective =>
                "La evidencia histórica favorece esta optimización. " +
                "Puede mantenerse como recomendación, sujeta a validación contextual.",

            OptimizationFeedbackAction.RecommendRollbackReview =>
                "Se detectó un historial perjudicial. " +
                "Se recomienda revisar la reversión; no se ejecuta automáticamente.",

            OptimizationFeedbackAction.KeepCurrentRecommendation =>
                "Las observaciones indican un efecto predominantemente neutro.",

            OptimizationFeedbackAction.CollectMoreEvidence =>
                "Todavía no hay suficientes observaciones para una conclusión.",

            _ =>
                "Los resultados son mixtos o inconclusos. Se necesita más evidencia."
        };

        return new OptimizationFeedbackReport(
            optimizationId,
            summary.Overall,
            action,
            summary.TotalObservations,
            summary.BeneficialRatePercent,
            explanation,
            evidence,
            DateTimeOffset.UtcNow);
    }
}
