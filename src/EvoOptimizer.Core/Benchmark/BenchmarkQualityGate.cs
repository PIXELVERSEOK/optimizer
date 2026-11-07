namespace EvoOptimizer.Core.Benchmark;

public enum BenchmarkQualityDecision
{
    Reject,
    KeepForObservation,
    EligibleForLearning,
    EligibleForBaselinePromotion
}

public sealed record BenchmarkQualityRequest(
    bool EvidenceAccepted,
    BenchmarkContextResolution Context,
    BenchmarkConfidenceReport Confidence,
    BenchmarkRepeatabilityReport Repeatability,
    bool UserApprovedBaselinePromotion);

public sealed record BenchmarkQualityReport(
    BenchmarkQualityDecision Decision,
    bool CanCompare,
    bool CanLearn,
    bool CanPromoteBaseline,
    double ConfidenceScore,
    BenchmarkRepeatabilityLevel Repeatability,
    IReadOnlyList<string> Reasons,
    DateTimeOffset EvaluatedAt);

public sealed class BenchmarkQualityGate
{
    public BenchmarkQualityReport Evaluate(
        BenchmarkQualityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reasons = new List<string>();

        if (!request.EvidenceAccepted)
        {
            reasons.Add("La evidencia no superó el control inicial.");

            return Create(
                BenchmarkQualityDecision.Reject,
                false, false, false, request, reasons);
        }

        if (!request.Context.CanComparePerformance)
        {
            reasons.Add(request.Context.Explanation);

            return Create(
                BenchmarkQualityDecision.KeepForObservation,
                false, false, false, request, reasons);
        }

        if (request.Confidence.Score < 55 ||
            request.Confidence.ValidFrameCount < 1000 ||
            request.Confidence.DurationSeconds < 30)
        {
            reasons.Add("La confianza o la cantidad de evidencia es insuficiente.");

            return Create(
                BenchmarkQualityDecision.KeepForObservation,
                true, false, false, request, reasons);
        }

        if (request.Repeatability.Level ==
            BenchmarkRepeatabilityLevel.InsufficientRuns)
        {
            reasons.Add("Se necesitan más ejecuciones para confirmar repetibilidad.");

            return Create(
                BenchmarkQualityDecision.KeepForObservation,
                true, false, false, request, reasons);
        }

        if (request.Repeatability.Level ==
            BenchmarkRepeatabilityLevel.Inconsistent)
        {
            reasons.Add("Las ejecuciones no son suficientemente consistentes.");

            return Create(
                BenchmarkQualityDecision.KeepForObservation,
                true, false, false, request, reasons);
        }

        if (!request.Context.CanUpdateLearning)
        {
            reasons.Add("El contexto no autoriza actualizar el aprendizaje.");

            return Create(
                BenchmarkQualityDecision.KeepForObservation,
                true, false, false, request, reasons);
        }

        bool canPromote =
            request.Confidence.Score >= 85 &&
            request.Repeatability.Level ==
                BenchmarkRepeatabilityLevel.HighlyRepeatable;

        if (canPromote && request.UserApprovedBaselinePromotion)
        {
            reasons.Add("Evidencia sólida, contexto comparable y repetibilidad alta.");
            reasons.Add("Promoción aprobada explícitamente.");

            return Create(
                BenchmarkQualityDecision.EligibleForBaselinePromotion,
                true, true, true, request, reasons);
        }

        if (canPromote)
            reasons.Add("Puede promoverse, pero falta aprobación del usuario.");

        return Create(
            BenchmarkQualityDecision.EligibleForLearning,
            true, true, false, request, reasons);
    }

    private static BenchmarkQualityReport Create(
        BenchmarkQualityDecision decision,
        bool compare,
        bool learn,
        bool promote,
        BenchmarkQualityRequest request,
        IReadOnlyList<string> reasons)
    {
        return new BenchmarkQualityReport(
            decision,
            compare,
            learn,
            promote,
            request.Confidence.Score,
            request.Repeatability.Level,
            reasons,
            DateTimeOffset.UtcNow);
    }
}
