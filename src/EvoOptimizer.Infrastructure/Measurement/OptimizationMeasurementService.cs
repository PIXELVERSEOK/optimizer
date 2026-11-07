using System.Diagnostics;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Analysis;
using EvoOptimizer.Infrastructure.Capture;
using EvoOptimizer.Infrastructure.History;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.Infrastructure.Measurement;

/// <summary>
/// Opciones de una ventana de medición. La duración mínima efectiva para FPS
/// es la que exige la regla de evidencia (30 s y 1000 frames); el analizador
/// descarta automáticamente las ventanas que no la cumplen.
/// </summary>
public sealed record OptimizationMeasurementCaptureOptions(
    int DurationSeconds = 30,
    bool CaptureFps = true,
    string? PresentMonExecutable = null);

public sealed record OptimizationMeasurementCaptureResult(
    OptimizationMeasurement Measurement,
    bool SystemCaptured,
    bool FpsCaptured,
    IReadOnlyList<string> Messages);

/// <summary>
/// Captura una ventana de medición real y comparable:
/// - Métricas de sistema con LivePerformanceMonitor (CPU, GPU, RAM, Commit, procesos).
/// - FPS con PresentMon cuando está disponible y CS2 se está ejecutando.
/// Los sensores que no existen en el código quedan en null; nunca se inventan.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OptimizationMeasurementService
{
    private readonly PresentMonCaptureService _captureService;
    private readonly PresentMonCsvReader _csvReader;
    private readonly AdvancedFpsAnalyzer _fpsAnalyzer;
    private readonly EvoHistoryStore? _history;
    private readonly Func<LivePerformanceMonitor> _monitorFactory;
    private readonly SemaphoreSlim _measurementLock = new(1, 1);

    public OptimizationMeasurementService(
        PresentMonCaptureService? captureService = null,
        PresentMonCsvReader? csvReader = null,
        AdvancedFpsAnalyzer? fpsAnalyzer = null,
        EvoHistoryStore? history = null,
        Func<LivePerformanceMonitor>? monitorFactory = null)
    {
        _captureService = captureService ?? new PresentMonCaptureService();
        _csvReader = csvReader ?? new PresentMonCsvReader();
        _fpsAnalyzer = fpsAnalyzer ?? new AdvancedFpsAnalyzer();
        _history = history;
        _monitorFactory = monitorFactory ?? (() => new LivePerformanceMonitor());
    }

    public async Task<OptimizationMeasurementCaptureResult> CaptureAsync(
        OptimizationMeasurementContext context,
        OptimizationMeasurementCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        options ??= new OptimizationMeasurementCaptureOptions();

        if (!await _measurementLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException(
                "Ya existe una medición en curso. Esperá a que termine.");
        }

        try
        {
            return await CaptureInternalAsync(
                context,
                options,
                cancellationToken);
        }
        finally
        {
            _measurementLock.Release();
        }
    }

    private async Task<OptimizationMeasurementCaptureResult> CaptureInternalAsync(
        OptimizationMeasurementContext context,
        OptimizationMeasurementCaptureOptions options,
        CancellationToken cancellationToken)
    {
        int duration = Math.Clamp(options.DurationSeconds, 5, 600);
        int fpsCaptureSeconds = Math.Max(duration, 10);

        var notes = new List<string>();
        var messages = new List<string>();
        var samples = new List<LivePerformanceSnapshot>();
        var sampleLock = new object();

        using var monitor = _monitorFactory();

        void OnSnapshot(LivePerformanceSnapshot snapshot)
        {
            lock (sampleLock)
                samples.Add(snapshot);
        }

        monitor.SnapshotCaptured += OnSnapshot;

        using var monitorCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var monitorTask = Task.Run(async () =>
        {
            try
            {
                await monitor.StartAsync(monitorCts.Token);
            }
            catch (OperationCanceledException)
            {
                // La ventana terminó: es el final esperado del muestreo.
            }
        }, CancellationToken.None);

        string? executable = options.PresentMonExecutable;

        if (string.IsNullOrWhiteSpace(executable))
            executable = PresentMonLocator.Find();

        Task<PresentMonCaptureResult>? fpsTask = null;

        if (options.CaptureFps && !string.IsNullOrWhiteSpace(executable))
        {
            fpsTask = _captureService.CaptureCs2Async(
                executable!,
                fpsCaptureSeconds,
                cancellationToken);
        }
        else if (options.CaptureFps)
        {
            notes.Add(
                "PresentMon no está instalado o no se encontró el ejecutable: " +
                "no se midieron FPS. La evidencia de rendimiento quedará incompleta.");
            messages.Add("FPS no medidos: PresentMon no disponible.");
        }

        PresentMonCaptureResult? fpsCapture = null;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(duration), cancellationToken);

            if (fpsTask is not null)
                fpsCapture = await fpsTask;
        }
        finally
        {
            monitorCts.Cancel();

            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Esperado.
            }

            monitor.SnapshotCaptured -= OnSnapshot;

            if (fpsTask is not null)
            {
                try
                {
                    await fpsTask;
                }
                catch
                {
                    // Si la ventana se canceló, la captura también se cancela.
                }
            }
        }

        LivePerformanceSnapshot[] snapshotArray;

        lock (sampleLock)
            snapshotArray = samples.ToArray();

        bool systemCaptured = snapshotArray.Length > 0;

        // La primera muestra no tiene referencia previa de CPU y devuelve 0.
        var cpuSamples = snapshotArray.Length > 1
            ? snapshotArray.Skip(1).ToArray()
            : snapshotArray;

        double? cpuPercent = cpuSamples.Length > 0
            ? Math.Round(cpuSamples.Average(x => x.SystemCpuPercent), 2)
            : null;

        var gpuValues = snapshotArray
            .Where(x => x.GpuUsagePercent.HasValue)
            .Select(x => x.GpuUsagePercent!.Value)
            .ToArray();

        double? gpuPercent = gpuValues.Length > 0
            ? Math.Round(gpuValues.Average(), 2)
            : null;

        double? memoryPercent = snapshotArray.Length > 0
            ? Math.Round(
                snapshotArray.Average(x => x.MemoryUsagePercent), 2)
            : null;

        double? commitMb = snapshotArray.Length > 0
            ? Math.Round(snapshotArray.Average(x => x.CommitUsedMb), 1)
            : null;

        int? activeProcesses = snapshotArray.Length > 0
            ? (int)Math.Round(
                snapshotArray.Average(x => (double)x.ActiveProcessCount))
            : null;

        double systemDuration = snapshotArray.Length > 1
            ? (snapshotArray[^1].Timestamp - snapshotArray[0].Timestamp)
                .TotalSeconds
            : 0;

        if (systemCaptured)
        {
            notes.Add(
                $"Sistema: {snapshotArray.Length} muestras en " +
                $"{systemDuration:F1}s (LivePerformanceMonitor).");
        }
        else
        {
            notes.Add(
                "No se obtuvieron muestras de sistema; la evidencia es insuficiente.");
            messages.Add("Sistema no medido: sin muestras.");
        }

        double? averageFps = null;
        double? onePercentLow = null;
        double? pointOnePercentLow = null;
        double? p95 = null;
        double? p99 = null;
        double? stutterRate = null;
        int frameCount = 0;
        double fpsDuration = 0;
        bool fpsCaptured = false;

        if (fpsCapture is not null)
        {
            if (fpsCapture.Status == CaptureStatus.Completed &&
                !string.IsNullOrWhiteSpace(fpsCapture.CsvPath))
            {
                try
                {
                    var read = _csvReader.Read(fpsCapture.CsvPath);
                    var report = _fpsAnalyzer.Analyze(read);

                    if (report.FrameCount >= 30)
                    {
                        averageFps = Math.Round(report.AverageFps, 2);
                        onePercentLow = Math.Round(report.OnePercentLowFps, 2);
                        pointOnePercentLow =
                            Math.Round(report.PointOnePercentLowFps, 2);
                        p95 = Math.Round(report.P95FrametimeMs, 3);
                        p99 = Math.Round(report.P99FrametimeMs, 3);
                        stutterRate =
                            Math.Round(report.StutterRatePercent, 2);
                        frameCount = report.FrameCount;
                        fpsDuration = report.DurationSeconds;
                        fpsCaptured = true;

                        notes.Add(
                            $"FPS: {report.FrameCount} frames en " +
                            $"{report.DurationSeconds:F1}s (PresentMon).");

                        foreach (var finding in report.Findings)
                            notes.Add($"FPS: {finding}");
                    }
                    else
                    {
                        notes.Add(
                            $"FPS: sólo {report.FrameCount} frames válidos; " +
                            "se requieren al menos 30 para analizar.");
                        messages.Add(
                            "FPS no medidos: frames insuficientes.");
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"No se pudo analizar el CSV de PresentMon: {ex.Message}");
                    messages.Add("FPS no medidos: error analizando PresentMon.");
                }
            }
            else
            {
                notes.Add($"FPS no medidos: {fpsCapture.Message}");
                messages.Add($"FPS no medidos: {fpsCapture.Status}.");
            }
        }

        notes.Add(
            "Sensores ausentes en esta versión: temperatura CPU/GPU, potencia, " +
            "frecuencia, latencia y disco. Quedan en null y no se usan para " +
            "declarar mejoras.");

        var metrics = new OptimizationMetricValues(
            AverageFps: averageFps,
            OnePercentLowFps: onePercentLow,
            PointOnePercentLowFps: pointOnePercentLow,
            P95FrametimeMs: p95,
            P99FrametimeMs: p99,
            StutterRatePercent: stutterRate,
            CpuUsagePercent: cpuPercent,
            GpuUsagePercent: gpuPercent,
            MemoryUsagePercent: memoryPercent,
            CommitUsedMb: commitMb,
            ActiveProcessCount: activeProcesses,
            FrameCount: frameCount,
            SampleCount: snapshotArray.Length,
            DurationSeconds: Math.Max(systemDuration, fpsDuration));

        var measurement = new OptimizationMeasurement(
            Guid.NewGuid(),
            context,
            DateTimeOffset.UtcNow,
            metrics,
            notes);

        return new OptimizationMeasurementCaptureResult(
            measurement,
            systemCaptured,
            fpsCaptured,
            messages);
    }

    public async Task SaveBaselineAsync(
        OptimizationMeasurement baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (_history is null)
            return;

        try
        {
            await _history.SaveAsync(
                "OptimizationBaseline",
                baseline,
                baseline.CapturedAt,
                cancellationToken);
        }
        catch
        {
            // La medición sigue siendo válida en memoria; SQLite es respaldo.
        }
    }

    public async Task<IReadOnlyDictionary<string, OptimizationMeasurement>>
        LoadBaselinesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, OptimizationMeasurement>(
            StringComparer.OrdinalIgnoreCase);

        if (_history is null)
            return result;

        try
        {
            var records = await _history.GetLatestAsync(
                "OptimizationBaseline",
                500,
                cancellationToken);

            foreach (var record in records)
            {
                OptimizationMeasurement? measurement;

                try
                {
                    measurement = System.Text.Json.JsonSerializer
                        .Deserialize<OptimizationMeasurement>(record.Payload);
                }
                catch
                {
                    continue;
                }

                if (measurement is null)
                    continue;

                string key = measurement.Context.ModificationKey;

                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!result.TryGetValue(key, out var existing) ||
                    measurement.CapturedAt > existing.CapturedAt)
                {
                    result[key] = measurement;
                }
            }
        }
        catch
        {
            // Si SQLite falla, el diccionario en memoria sigue siendo útil.
        }

        return result;
    }
}

/// <summary>
/// Localiza PresentMon.exe sin pedirle al usuario una ruta manual.
/// Si no está instalado, la medición de FPS queda ausente y la evidencia
/// se clasifica como insuficiente/inconclusa.
/// </summary>
public static class PresentMonLocator
{
    public static string? Find()
    {
        var candidates = new List<string>();

        string? configured =
            Environment.GetEnvironmentVariable("EVO_PRESENTMON_PATH");

        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);

        string baseDirectory = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(baseDirectory, "PresentMon.exe"));
        candidates.Add(Path.Combine(baseDirectory, "tools", "PresentMon.exe"));
        candidates.Add(Path.Combine(baseDirectory, "PresentMon", "PresentMon.exe"));

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        candidates.Add(Path.Combine(
            localAppData, "EvoFpsOptimizer", "Tools", "PresentMon.exe"));

        string? path = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory, "PresentMon.exe"));
                }
                catch
                {
                    // Una entrada de PATH inválida no debe romper la búsqueda.
                }
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignorar rutas inválidas y seguir buscando.
            }
        }

        return null;
    }
}
