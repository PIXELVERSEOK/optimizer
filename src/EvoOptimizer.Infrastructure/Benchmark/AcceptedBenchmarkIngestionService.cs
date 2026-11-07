using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Benchmark;

public enum BenchmarkIngestionStatus
{
    Stored,
    Rejected,
    InvalidEvidence
}

public sealed record BenchmarkIngestionResult(
    BenchmarkIngestionStatus Status,
    string? EvidenceId,
    string Message);

/// <summary>
/// Punto controlado de entrada al historial de benchmarks.
/// Exige aceptación explícita del control de calidad antes de persistir.
/// </summary>
public sealed class AcceptedBenchmarkIngestionService
{
    private readonly SqliteAcceptedBenchmarkEvidenceRepository _repository;

    public AcceptedBenchmarkIngestionService(
        SqliteAcceptedBenchmarkEvidenceRepository repository)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    public async Task<BenchmarkIngestionResult> IngestAsync(
        BenchmarkEvidence evidence,
        bool qualityGateAccepted,
        string qualityReportJson,
        string? rejectionReason = null,
        CancellationToken cancellationToken = default)
    {
        if (!qualityGateAccepted)
        {
            return new BenchmarkIngestionResult(
                BenchmarkIngestionStatus.Rejected,
                null,
                string.IsNullOrWhiteSpace(rejectionReason)
                    ? "La evidencia no superó el control de calidad."
                    : rejectionReason);
        }

        if (evidence is null ||
            evidence.Frames is null ||
            evidence.Frames.Count == 0 ||
            string.IsNullOrWhiteSpace(evidence.GameId) ||
            string.IsNullOrWhiteSpace(evidence.Scenario) ||
            string.IsNullOrWhiteSpace(evidence.Resolution) ||
            string.IsNullOrWhiteSpace(evidence.CaptureSource))
        {
            return new BenchmarkIngestionResult(
                BenchmarkIngestionStatus.InvalidEvidence,
                null,
                "La evidencia aceptada contiene metadatos o frames incompletos.");
        }

        if (string.IsNullOrWhiteSpace(qualityReportJson))
        {
            return new BenchmarkIngestionResult(
                BenchmarkIngestionStatus.InvalidEvidence,
                null,
                "Falta el informe de calidad que justifica la aceptación.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string id = await _repository.SaveAcceptedAsync(
            evidence,
            qualityReportJson,
            cancellationToken);

        return new BenchmarkIngestionResult(
            BenchmarkIngestionStatus.Stored,
            id,
            "Evidencia aceptada y almacenada correctamente.");
    }
}