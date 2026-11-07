namespace EvoOptimizer.Core.Benchmark;

public enum BenchmarkExperimentStatus
{
    Planned,
    Running,
    Completed,
    Incomplete,
    Cancelled
}

public enum BenchmarkExperimentSide
{
    Control,
    Candidate
}

public sealed record BenchmarkExperimentDefinition(
    Guid Id,
    string Name,
    string GameId,
    string HardwareId,
    string EnvironmentId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    int RequiredRunsPerSide,
    DateTimeOffset CreatedAt);

public sealed record BenchmarkExperimentRun(
    Guid Id,
    Guid ExperimentId,
    BenchmarkExperimentSide Side,
    BenchmarkRunResult Result,
    DateTimeOffset RecordedAt);

public sealed record BenchmarkExperiment(
    BenchmarkExperimentDefinition Definition,
    BenchmarkExperimentStatus Status,
    IReadOnlyList<BenchmarkExperimentRun> Runs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed class BenchmarkExperimentRunner
{
    public BenchmarkExperiment Create(
        string name,
        string gameId,
        string hardwareId,
        string environmentId,
        string scenario,
        string resolution,
        string captureSource,
        int requiredRunsPerSide = 3)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(gameId) ||
            string.IsNullOrWhiteSpace(hardwareId) ||
            string.IsNullOrWhiteSpace(environmentId) ||
            string.IsNullOrWhiteSpace(scenario) ||
            string.IsNullOrWhiteSpace(resolution) ||
            string.IsNullOrWhiteSpace(captureSource))
            throw new ArgumentException("Faltan datos del experimento.");

        if (requiredRunsPerSide < 3 || requiredRunsPerSide > 20)
            throw new ArgumentOutOfRangeException(
                nameof(requiredRunsPerSide),
                "Se requieren entre 3 y 20 ejecuciones por grupo.");

        var definition = new BenchmarkExperimentDefinition(
            Guid.NewGuid(),
            name.Trim(),
            gameId.Trim(),
            hardwareId.Trim(),
            environmentId.Trim(),
            scenario.Trim(),
            resolution.Trim(),
            captureSource.Trim(),
            requiredRunsPerSide,
            DateTimeOffset.UtcNow);

        return new BenchmarkExperiment(
            definition,
            BenchmarkExperimentStatus.Planned,
            Array.Empty<BenchmarkExperimentRun>(),
            null,
            null);
    }

    public BenchmarkExperiment Start(BenchmarkExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        if (experiment.Status != BenchmarkExperimentStatus.Planned)
            throw new InvalidOperationException(
                "Solo se pueden iniciar experimentos planificados.");

        return experiment with
        {
            Status = BenchmarkExperimentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    public BenchmarkExperiment RecordRun(
        BenchmarkExperiment experiment,
        BenchmarkExperimentSide side,
        BenchmarkRunResult result)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(result);

        if (experiment.Status != BenchmarkExperimentStatus.Running)
            throw new InvalidOperationException(
                "El experimento no está en ejecución.");

        var definition = experiment.Definition;

        if (!Same(result.GameId, definition.GameId) ||
            !Same(result.HardwareId, definition.HardwareId) ||
            !Same(result.EnvironmentId, definition.EnvironmentId) ||
            !Same(result.Scenario, definition.Scenario) ||
            !Same(result.Resolution, definition.Resolution) ||
            !Same(result.CaptureSource, definition.CaptureSource))
            throw new InvalidOperationException(
                "La captura no pertenece al contexto del experimento.");

        if (result.Confidence.Score < 55 ||
            result.Confidence.ValidFrameCount < 1000 ||
            result.Confidence.DurationSeconds < 30)
            throw new InvalidOperationException(
                "La captura no tiene evidencia suficiente.");

        var count = experiment.Runs.Count(x => x.Side == side);

        if (count >= definition.RequiredRunsPerSide)
            throw new InvalidOperationException(
                "El grupo ya alcanzó el número de ejecuciones requerido.");

        var run = new BenchmarkExperimentRun(
            Guid.NewGuid(),
            definition.Id,
            side,
            result,
            DateTimeOffset.UtcNow);

        return experiment with
        {
            Runs = experiment.Runs.Append(run).ToArray()
        };
    }

    public BenchmarkExperiment Complete(
        BenchmarkExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        if (experiment.Status != BenchmarkExperimentStatus.Running)
            throw new InvalidOperationException(
                "El experimento no está en ejecución.");

        var control = experiment.Runs.Count(
            x => x.Side == BenchmarkExperimentSide.Control);

        var candidate = experiment.Runs.Count(
            x => x.Side == BenchmarkExperimentSide.Candidate);

        if (control < experiment.Definition.RequiredRunsPerSide ||
            candidate < experiment.Definition.RequiredRunsPerSide)
        {
            return experiment with
            {
                Status = BenchmarkExperimentStatus.Incomplete
            };
        }

        return experiment with
        {
            Status = BenchmarkExperimentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public BenchmarkExperiment Cancel(BenchmarkExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        if (experiment.Status == BenchmarkExperimentStatus.Completed)
            throw new InvalidOperationException(
                "Un experimento completado no se puede cancelar.");

        return experiment with
        {
            Status = BenchmarkExperimentStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
