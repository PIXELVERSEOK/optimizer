using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Benchmark;

public sealed class BenchmarkExperimentPersistenceService
{
    private readonly SqliteBenchmarkRepository _repository;

    public BenchmarkExperimentPersistenceService(
        SqliteBenchmarkRepository repository)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    public Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.InitializeAsync(cancellationToken);
    }

    public Task SaveAsync(
        BenchmarkExperiment experiment,
        CancellationToken cancellationToken = default)
    {
        return _repository.SaveExperimentAsync(
            experiment, cancellationToken);
    }

    public Task<IReadOnlyList<BenchmarkExperiment>> LoadAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetExperimentsAsync(
            limit, cancellationToken);
    }

    public Task SaveEffectivenessAsync(
        OptimizationEffectivenessObservation observation,
        CancellationToken cancellationToken = default)
    {
        return _repository.SaveEffectivenessAsync(
            observation, cancellationToken);
    }

    public Task<IReadOnlyList<OptimizationEffectivenessObservation>>
        LoadEffectivenessAsync(
            string optimizationId,
            int limit = 500,
            CancellationToken cancellationToken = default)
    {
        return _repository.GetEffectivenessAsync(
            optimizationId, limit, cancellationToken);
    }
}
