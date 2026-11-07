namespace EvoOptimizer.Core.Benchmark;

public sealed record BenchmarkFrameObservation(
    double TimestampSeconds,
    double FrametimeMs);

public sealed record BenchmarkEvidence(
    string GameId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    DateTimeOffset CapturedAt,
    IReadOnlyList<BenchmarkFrameObservation> Frames);

public enum BenchmarkEvidenceStatus
{
    Accepted,
    Rejected,
    InsufficientData,
    InvalidMetadata
}

public sealed record BenchmarkEvidenceResult(
    BenchmarkEvidenceStatus Status,
    int TotalFrames,
    int ValidFrames,
    int InvalidFrames,
    double DurationSeconds,
    double InvalidFramePercentage,
    IReadOnlyList<string> Reasons)
{
    public bool IsAccepted =>
        Status == BenchmarkEvidenceStatus.Accepted;
}

public sealed record BenchmarkCompatibilityResult(
    bool IsComparable,
    IReadOnlyList<string> Differences);

public sealed class BenchmarkEvidenceGate
{
    public const int MinimumFrames = 1000;
    public const double MinimumDurationSeconds = 30;
    public const double MaximumInvalidFramePercentage = 1.0;

    public BenchmarkEvidenceResult Evaluate(
        BenchmarkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(evidence.GameId) ||
            string.IsNullOrWhiteSpace(evidence.Scenario) ||
            string.IsNullOrWhiteSpace(evidence.Resolution) ||
            string.IsNullOrWhiteSpace(evidence.CaptureSource))
        {
            return Result(
                BenchmarkEvidenceStatus.InvalidMetadata,
                0, 0, 0, 0, 0,
                "Faltan identificadores de juego, escenario, resolución o fuente.");
        }

        if (evidence.Frames is null || evidence.Frames.Count == 0)
        {
            return Result(
                BenchmarkEvidenceStatus.InsufficientData,
                0, 0, 0, 0, 0,
                "La captura no contiene frames.");
        }

        int total = evidence.Frames.Count;
        int valid = 0;
        int invalid = 0;

        double firstTimestamp = double.NaN;
        double lastTimestamp = double.NaN;
        double previousTimestamp = double.NegativeInfinity;

        foreach (var frame in evidence.Frames)
        {
            bool validTimestamp =
                double.IsFinite(frame.TimestampSeconds) &&
                frame.TimestampSeconds >= 0 &&
                frame.TimestampSeconds >= previousTimestamp;

            bool validFrametime =
                double.IsFinite(frame.FrametimeMs) &&
                frame.FrametimeMs > 0;

            if (!validTimestamp || !validFrametime)
            {
                invalid++;
                continue;
            }

            if (double.IsNaN(firstTimestamp))
                firstTimestamp = frame.TimestampSeconds;

            previousTimestamp = frame.TimestampSeconds;
            lastTimestamp = frame.TimestampSeconds;
            valid++;
        }

        double duration = valid >= 2
            ? Math.Max(0, lastTimestamp - firstTimestamp)
            : 0;

        double invalidPercentage = total == 0
            ? 100
            : invalid * 100.0 / total;

        if (valid < MinimumFrames)
            reasons.Add(
                $"Frames válidos insuficientes: {valid}/{MinimumFrames}.");

        if (duration < MinimumDurationSeconds)
            reasons.Add(
                $"Duración insuficiente: {duration:F2}s; mínimo {MinimumDurationSeconds:F0}s.");

        if (invalidPercentage > MaximumInvalidFramePercentage)
            reasons.Add(
                $"Datos inválidos: {invalidPercentage:F2}%.");

        var status = reasons.Count == 0
            ? BenchmarkEvidenceStatus.Accepted
            : valid < MinimumFrames || duration < MinimumDurationSeconds
                ? BenchmarkEvidenceStatus.InsufficientData
                : BenchmarkEvidenceStatus.Rejected;

        return new BenchmarkEvidenceResult(
            status,
            total,
            valid,
            invalid,
            duration,
            invalidPercentage,
            reasons);
    }

    public BenchmarkCompatibilityResult Compare(
        BenchmarkEvidence baseline,
        BenchmarkEvidence candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var differences = new List<string>();

        if (!Same(baseline.GameId, candidate.GameId))
            differences.Add("Los juegos no coinciden.");

        if (!Same(baseline.Scenario, candidate.Scenario))
            differences.Add("Los escenarios de prueba no coinciden.");

        if (!Same(baseline.Resolution, candidate.Resolution))
            differences.Add("Las resoluciones no coinciden.");

        if (!Same(baseline.CaptureSource, candidate.CaptureSource))
            differences.Add("Las fuentes de captura no coinciden.");

        var baselineQuality = Evaluate(baseline);
        var candidateQuality = Evaluate(candidate);

        if (!baselineQuality.IsAccepted)
            differences.Add("El benchmark de referencia no es válido.");

        if (!candidateQuality.IsAccepted)
            differences.Add("El benchmark candidato no es válido.");

        return new BenchmarkCompatibilityResult(
            differences.Count == 0,
            differences);
    }

    private static bool Same(string left, string right) =>
        string.Equals(
            left.Trim(),
            right.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static BenchmarkEvidenceResult Result(
        BenchmarkEvidenceStatus status,
        int total,
        int valid,
        int invalid,
        double duration,
        double invalidPercentage,
        string reason)
    {
        return new BenchmarkEvidenceResult(
            status,
            total,
            valid,
            invalid,
            duration,
            invalidPercentage,
            new[] { reason });
    }
}
