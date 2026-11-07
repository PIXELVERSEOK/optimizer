using EvoOptimizer.Core.Hardware;

namespace EvoOptimizer.Core.Benchmark;

public enum BenchmarkContextDecision
{
    Comparable,
    NewHardwareBaselineRequired,
    NewEnvironmentBaselineRequired,
    ScenarioMismatch,
    EvidenceRejected,
    ContextIncomplete
}

public sealed record BenchmarkContext(
    string GameId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    DateTimeOffset CapturedAt);

public sealed record BenchmarkContextResolution(
    BenchmarkContextDecision Decision,
    bool CanComparePerformance,
    bool CanUpdateLearning,
    string Explanation,
    IReadOnlyList<string> Differences,
    DateTimeOffset ResolvedAt);

public sealed class BenchmarkContextResolver
{
    public BenchmarkContextResolution Resolve(
        BenchmarkContext baseline,
        BenchmarkContext candidate,
        HardwareChangeReport hardwareReport,
        bool baselineEvidenceAccepted,
        bool candidateEvidenceAccepted)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(hardwareReport);

        var differences = new List<string>();

        if (!baselineEvidenceAccepted || !candidateEvidenceAccepted)
        {
            return Result(
                BenchmarkContextDecision.EvidenceRejected,
                false,
                false,
                "Uno o ambos benchmarks no superaron el control de evidencia.",
                differences);
        }

        CompareField(differences, "Juego", baseline.GameId, candidate.GameId);
        CompareField(differences, "Escenario", baseline.Scenario, candidate.Scenario);
        CompareField(differences, "Resolución", baseline.Resolution, candidate.Resolution);
        CompareField(differences, "Fuente de captura",
            baseline.CaptureSource, candidate.CaptureSource);

        if (differences.Count > 0)
        {
            return Result(
                BenchmarkContextDecision.ScenarioMismatch,
                false,
                false,
                "Los benchmarks no representan el mismo contexto de prueba.",
                differences);
        }

        if (hardwareReport.Status == HardwareComparisonStatus.IncompleteEvidence)
        {
            return Result(
                BenchmarkContextDecision.ContextIncomplete,
                false,
                false,
                "Faltan datos fiables para confirmar que los equipos y entornos sean comparables.",
                hardwareReport.UnverifiedFields);
        }

        if (!hardwareReport.BenchmarksRemainHardwareComparable)
        {
            return Result(
                BenchmarkContextDecision.NewHardwareBaselineRequired,
                false,
                false,
                "Cambió la identidad del hardware. Se necesita una nueva línea base.",
                hardwareReport.Changes
                    .Where(x => x.AffectsHardwareIdentity)
                    .Select(x => x.Field)
                    .ToArray());
        }

        if (!hardwareReport.BenchmarksRemainEnvironmentComparable)
        {
            return Result(
                BenchmarkContextDecision.NewEnvironmentBaselineRequired,
                false,
                false,
                "El hardware coincide, pero cambió el entorno. Conservá ambos resultados en grupos separados.",
                hardwareReport.Changes
                    .Where(x => !x.AffectsHardwareIdentity)
                    .Select(x => x.Field)
                    .ToArray());
        }

        return Result(
            BenchmarkContextDecision.Comparable,
            true,
            true,
            "Los benchmarks superaron los controles de evidencia, hardware, entorno y escenario.",
            differences);
    }

    private static void CompareField(
        List<string> differences,
        string label,
        string? baseline,
        string? candidate)
    {
        if (string.IsNullOrWhiteSpace(baseline) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            differences.Add($"{label}: dato ausente.");
            return;
        }

        if (!string.Equals(
            Normalize(baseline),
            Normalize(candidate),
            StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(
                $"{label}: '{baseline}' frente a '{candidate}'.");
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Trim().ToUpperInvariant().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static BenchmarkContextResolution Result(
        BenchmarkContextDecision decision,
        bool canCompare,
        bool canLearn,
        string explanation,
        IReadOnlyList<string> differences)
    {
        return new BenchmarkContextResolution(
            decision,
            canCompare,
            canLearn,
            explanation,
            differences,
            DateTimeOffset.UtcNow);
    }
}
