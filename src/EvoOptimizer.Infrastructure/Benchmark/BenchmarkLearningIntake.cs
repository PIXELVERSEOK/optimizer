using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Benchmark;

public sealed record BenchmarkLearningCandidate(
    Guid Id,
    BenchmarkRunResult Run,
    BenchmarkQualityReport Quality,
    DateTimeOffset CreatedAt);

public sealed record BenchmarkLearningIntakeResult(
    bool AcceptedForLearning,
    BenchmarkQualityReport Quality,
    Guid? CandidateId,
    string Message);

public sealed class BenchmarkLearningIntake
{
    private readonly BenchmarkQualityGate _qualityGate;
    private readonly SqliteBenchmarkRepository _repository;

    public BenchmarkLearningIntake(
        BenchmarkQualityGate qualityGate,
        SqliteBenchmarkRepository repository)
    {
        _qualityGate = qualityGate ??
            throw new ArgumentNullException(nameof(qualityGate));

        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    public async Task<BenchmarkLearningIntakeResult> SubmitAsync(
        BenchmarkRunResult run,
        BenchmarkQualityRequest qualityRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(qualityRequest);

        var quality = _qualityGate.Evaluate(qualityRequest);

        if (!quality.CanLearn)
        {
            return new BenchmarkLearningIntakeResult(
                false,
                quality,
                null,
                "Resultado conservado fuera de la cola de aprendizaje: " +
                string.Join(" ", quality.Reasons));
        }

        if (qualityRequest.Context.Decision !=
            BenchmarkContextDecision.Comparable)
        {
            return new BenchmarkLearningIntakeResult(
                false,
                quality,
                null,
                "El contexto no es comparable. No se admite al aprendizaje.");
        }

        if (run.Confidence.Score < 55 ||
            run.Confidence.ValidFrameCount < 1000 ||
            run.Confidence.DurationSeconds < 30)
        {
            return new BenchmarkLearningIntakeResult(
                false,
                quality,
                null,
                "La ejecución no satisface los mínimos de confianza.");
        }

        var candidate = new BenchmarkLearningCandidate(
            Guid.NewGuid(),
            run,
            quality,
            DateTimeOffset.UtcNow);

        await _repository.SaveLearningCandidateAsync(
            candidate, cancellationToken);

        return new BenchmarkLearningIntakeResult(
            true,
            quality,
            candidate.Id,
            "Resultado aceptado y guardado en la cola de aprendizaje.");
    }
}
