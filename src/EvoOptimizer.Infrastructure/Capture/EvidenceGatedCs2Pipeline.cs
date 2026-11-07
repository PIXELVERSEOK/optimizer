using EvoOptimizer.Core.Benchmark;

namespace EvoOptimizer.Infrastructure.Capture;

/// <summary>
/// Adaptador para reutilizar PresentMonCsvReader.
/// Debe convertir sus resultados al modelo común de evidencia.
/// </summary>
public interface ICs2BenchmarkEvidenceProvider
{
    Task<BenchmarkEvidence> ReadEvidenceAsync(
        string csvPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Procesador de benchmarks aceptados.
/// Aquí se conectan BenchmarkLab, OptimizationLearningEngine
/// y EvoHistoryStore mediante sus adaptadores concretos.
/// </summary>
public interface IAcceptedCs2BenchmarkProcessor
{
    Task ProcessAcceptedAsync(
        string csvPath,
        BenchmarkEvidence evidence,
        BenchmarkEvidenceResult quality,
        CancellationToken cancellationToken);
}

public sealed class BenchmarkEvidenceRejectedException : IOException
{
    public BenchmarkEvidenceResult Quality { get; }

    public BenchmarkEvidenceRejectedException(
        BenchmarkEvidenceResult quality)
        : base(
            "Benchmark rechazado: " +
            string.Join(" ", quality.Reasons))
    {
        Quality = quality;
    }
}

/// <summary>
/// Impide que capturas deficientes lleguen al aprendizaje.
/// </summary>
public sealed class EvidenceGatedCs2Pipeline : ICs2CaptureResultPipeline
{
    private readonly ICs2BenchmarkEvidenceProvider _evidenceProvider;
    private readonly IAcceptedCs2BenchmarkProcessor _processor;
    private readonly BenchmarkEvidenceGate _gate;

    public EvidenceGatedCs2Pipeline(
        ICs2BenchmarkEvidenceProvider evidenceProvider,
        IAcceptedCs2BenchmarkProcessor processor,
        BenchmarkEvidenceGate? gate = null)
    {
        _evidenceProvider = evidenceProvider ??
            throw new ArgumentNullException(nameof(evidenceProvider));

        _processor = processor ??
            throw new ArgumentNullException(nameof(processor));

        _gate = gate ?? new BenchmarkEvidenceGate();
    }

    public async Task ProcessAndStoreAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
            throw new ArgumentException(
                "La ruta del CSV es obligatoria.",
                nameof(csvPath));

        if (!File.Exists(csvPath))
            throw new FileNotFoundException(
                "No se encontró el CSV de PresentMon.",
                csvPath);

        cancellationToken.ThrowIfCancellationRequested();

        var evidence = await _evidenceProvider.ReadEvidenceAsync(
            csvPath,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var quality = _gate.Evaluate(evidence);

        if (!quality.IsAccepted)
            throw new BenchmarkEvidenceRejectedException(quality);

        await _processor.ProcessAcceptedAsync(
            csvPath,
            evidence,
            quality,
            cancellationToken);
    }
}

