using EvoOptimizer.Infrastructure.Analysis;

namespace EvoOptimizer.Infrastructure.Capture;

/// <summary>
/// Integra el análisis avanzado con el pipeline de evidencia existente.
/// No duplica captura, validación, persistencia ni aprendizaje.
/// </summary>
public sealed class AdvancedAnalysisCs2CapturePipeline
    : ICs2CaptureResultPipeline
{
    private readonly PresentMonCsvReader _csvReader;
    private readonly AdvancedFpsAnalyzer _analyzer;
    private readonly ICs2CaptureResultPipeline _evidencePipeline;

    public AdvancedFpsReport? LastReport { get; private set; }

    public string? LastCsvPath { get; private set; }

    public DateTimeOffset? LastSuccessfulAnalysis { get; private set; }

    public AdvancedAnalysisCs2CapturePipeline(
        ICs2CaptureResultPipeline evidencePipeline,
        PresentMonCsvReader? csvReader = null,
        AdvancedFpsAnalyzer? analyzer = null)
    {
        _evidencePipeline = evidencePipeline ??
            throw new ArgumentNullException(nameof(evidencePipeline));

        _csvReader = csvReader ?? new PresentMonCsvReader();
        _analyzer = analyzer ?? new AdvancedFpsAnalyzer();
    }

    public async Task ProcessAndStoreAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
            throw new ArgumentException(
                "La ruta del CSV está vacía.",
                nameof(csvPath));

        cancellationToken.ThrowIfCancellationRequested();

        LastReport = null;
        LastCsvPath = null;
        LastSuccessfulAnalysis = null;

        if (!File.Exists(csvPath))
            throw new FileNotFoundException(
                "No se encontró el CSV de PresentMon.",
                csvPath);

        // Reutiliza el lector y el analizador existentes.
        var parsed = _csvReader.Read(csvPath);
        var report = _analyzer.Analyze(parsed);

        if (report.Stability == FpsStabilityLevel.InsufficientData ||
            report.FrameCount < 30)
        {
            throw new InvalidDataException(
                "El análisis avanzado no dispone de suficientes frames válidos.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // El pipeline existente conserva el control de calidad,
        // aceptación, aprendizaje y persistencia.
        await _evidencePipeline.ProcessAndStoreAsync(
            csvPath,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Solo publicamos el resultado si el pipeline de evidencia
        // aceptó y procesó correctamente la captura.
        LastReport = report;
        LastCsvPath = Path.GetFullPath(csvPath);
        LastSuccessfulAnalysis = DateTimeOffset.UtcNow;
    }
}