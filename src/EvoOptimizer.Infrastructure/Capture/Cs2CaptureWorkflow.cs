namespace EvoOptimizer.Infrastructure.Capture;

/// <summary>
/// Punto de integración con los analizadores y el historial existentes.
/// La implementación concreta debe reutilizar esos módulos.
/// </summary>
public interface ICs2CaptureResultPipeline
{
    Task ProcessAndStoreAsync(
        string csvPath,
        CancellationToken cancellationToken);
}

public enum Cs2WorkflowStatus
{
    Completed,
    CaptureRejected,
    AnalysisFailed,
    Cancelled
}

public sealed record Cs2CaptureWorkflowResult(
    Cs2WorkflowStatus Status,
    PresentMonCaptureResult Capture,
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>
/// Orquesta captura, análisis y persistencia sin duplicar analizadores.
/// </summary>
public sealed class Cs2CaptureWorkflow
{
    private readonly PresentMonCaptureService _captureService;
    private readonly ICs2CaptureResultPipeline _pipeline;
    private readonly SemaphoreSlim _workflowLock = new(1, 1);

    public Cs2CaptureWorkflow(
        PresentMonCaptureService captureService,
        ICs2CaptureResultPipeline pipeline)
    {
        _captureService = captureService ??
            throw new ArgumentNullException(nameof(captureService));

        _pipeline = pipeline ??
            throw new ArgumentNullException(nameof(pipeline));
    }

    public async Task<Cs2CaptureWorkflowResult> RunAsync(
        string presentMonExecutable,
        int durationSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (!await _workflowLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                "Ya existe un flujo de benchmark CS2 en ejecución.");

        var startedAt = DateTimeOffset.Now;

        try
        {
            var capture = await _captureService.CaptureCs2Async(
                presentMonExecutable,
                durationSeconds,
                cancellationToken);

            if (capture.Status == CaptureStatus.Cancelled)
            {
                return CreateResult(
                    Cs2WorkflowStatus.Cancelled,
                    capture,
                    "La captura fue cancelada.");
            }

            if (capture.Status != CaptureStatus.Completed ||
                string.IsNullOrWhiteSpace(capture.CsvPath))
            {
                return CreateResult(
                    Cs2WorkflowStatus.CaptureRejected,
                    capture,
                    $"No se puede analizar la captura: {capture.Message}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _pipeline.ProcessAndStoreAsync(
                    capture.CsvPath,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return CreateResult(
                    Cs2WorkflowStatus.AnalysisFailed,
                    capture,
                    $"La captura se guardó, pero falló su procesamiento: {ex.Message}");
            }

            return CreateResult(
                Cs2WorkflowStatus.Completed,
                capture,
                "Captura, análisis y persistencia completados.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _workflowLock.Release();
        }
    }

    private Cs2CaptureWorkflowResult CreateResult(
        Cs2WorkflowStatus status,
        PresentMonCaptureResult capture,
        string message)
    {
        return new Cs2CaptureWorkflowResult(
            status,
            capture,
            message,
            StartedAt: capture.StartedAt,
            FinishedAt: DateTimeOffset.Now);
    }
}

