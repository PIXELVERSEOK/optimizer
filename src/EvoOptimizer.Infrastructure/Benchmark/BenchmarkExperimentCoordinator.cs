using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Benchmark;

public sealed class BenchmarkExperimentCoordinator
{
    private readonly BenchmarkExperimentRunner _runner;
    private readonly SqliteBenchmarkRepository _repository;

    public BenchmarkExperimentCoordinator(
        BenchmarkExperimentRunner runner,
        SqliteBenchmarkRepository repository)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<BenchmarkExperiment> CreateAsync(
        string name,
        string gameId,
        string hardwareId,
        string environmentId,
        string scenario,
        string resolution,
        string captureSource,
        int requiredRunsPerSide = 3,
        CancellationToken cancellationToken = default)
    {
        var experiment = _runner.Create(
            name,
            gameId,
            hardwareId,
            environmentId,
            scenario,
            resolution,
            captureSource,
            requiredRunsPerSide);

        await _repository.SaveExperimentAsync(
            experiment, cancellationToken);

        return experiment;
    }

    public async Task<BenchmarkExperiment> StartAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default)
    {
        var experiment = await RequireExperimentAsync(
            experimentId, cancellationToken);

        var started = _runner.Start(experiment);

        await _repository.SaveExperimentAsync(
            started, cancellationToken);

        return started;
    }

    public async Task<BenchmarkExperiment> RecordRunAsync(
        Guid experimentId,
        BenchmarkExperimentSide side,
        BenchmarkRunResult result,
        CancellationToken cancellationToken = default)
    {
        var experiment = await RequireExperimentAsync(
            experimentId, cancellationToken);

        var updated = _runner.RecordRun(experiment, side, result);

        var run = updated.Runs.Last();

        await _repository.SaveExperimentRunAsync(
            updated, run, cancellationToken);

        return updated;
    }

    public async Task<BenchmarkExperiment> CompleteAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default)
    {
        var experiment = await RequireExperimentAsync(
            experimentId, cancellationToken);

        var completed = _runner.Complete(experiment);

        await _repository.SaveExperimentAsync(
            completed, cancellationToken);

        return completed;
    }

    public async Task<BenchmarkExperiment> CancelAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default)
    {
        var experiment = await RequireExperimentAsync(
            experimentId, cancellationToken);

        var cancelled = _runner.Cancel(experiment);

        await _repository.SaveExperimentAsync(
            cancelled, cancellationToken);

        return cancelled;
    }

    public Task<BenchmarkExperiment?> GetAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default) =>
        _repository.GetExperimentAsync(experimentId, cancellationToken);

    private async Task<BenchmarkExperiment> RequireExperimentAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await _repository.GetExperimentAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"No existe el experimento {id}.");
    }
}
