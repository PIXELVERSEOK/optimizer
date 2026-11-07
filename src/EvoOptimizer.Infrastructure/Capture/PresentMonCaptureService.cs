using System.Diagnostics;

namespace EvoOptimizer.Infrastructure.Capture;

public enum CaptureStatus
{
    Completed,
    GameNotRunning,
    ExecutableNotFound,
    TimedOut,
    Cancelled,
    Failed
}

public sealed record PresentMonCaptureResult(
    CaptureStatus Status,
    string? CsvPath,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TimeSpan Duration,
    int? ExitCode,
    string Message,
    string StandardError);

public sealed class PresentMonCaptureService
{
    private readonly string _captureDirectory;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public PresentMonCaptureService(string? captureDirectory = null)
    {
        _captureDirectory = captureDirectory ??
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "EvoFpsOptimizer",
                "Captures");

        Directory.CreateDirectory(_captureDirectory);
    }

    public async Task<PresentMonCaptureResult> CaptureCs2Async(
        string presentMonExecutable,
        int durationSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 10 || durationSeconds > 3600)
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "La captura debe durar entre 10 y 3600 segundos.");

        if (!await _captureLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                "Ya existe una captura PresentMon activa.");

        try
        {
            return await CaptureInternalAsync(
                presentMonExecutable,
                durationSeconds,
                cancellationToken);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private async Task<PresentMonCaptureResult> CaptureInternalAsync(
        string executable,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            return Result(
                CaptureStatus.ExecutableNotFound,
                null,
                startedAt,
                null,
                "No se encontró PresentMon.exe.",
                null,
                "");
        }

        var gameProcesses = Process.GetProcessesByName("cs2");

        try
        {
            if (gameProcesses.Length == 0)
            {
                return Result(
                    CaptureStatus.GameNotRunning,
                    null,
                    startedAt,
                    null,
                    "CS2 no está ejecutándose. Iniciá el juego antes de capturar.",
                    null,
                    "");
            }
        }
        finally
        {
            foreach (var gameProcessToDispose in gameProcesses)
                gameProcessToDispose.Dispose();
        }

        var fileName =
            $"CS2_{startedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.csv";

        var csvPath = Path.Combine(_captureDirectory, fileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            WorkingDirectory = Path.GetDirectoryName(
                Path.GetFullPath(executable))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("--process_name");
        startInfo.ArgumentList.Add("cs2.exe");

        startInfo.ArgumentList.Add("--output_file");
        startInfo.ArgumentList.Add(csvPath);

        startInfo.ArgumentList.Add("--timed");
        startInfo.ArgumentList.Add(durationSeconds.ToString());

        startInfo.ArgumentList.Add("--terminate_after_timed");
        startInfo.ArgumentList.Add("--no_console_stats");
        startInfo.ArgumentList.Add("--v2_metrics");

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return Result(
                    CaptureStatus.Failed,
                    null,
                    startedAt,
                    null,
                    "Windows no pudo iniciar PresentMon.",
                    null,
                    "");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 60));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // El proceso pudo haber terminado entre comprobaciones.
                    }
                }

                var stderrCancelled = await stderrTask;
                _ = await stdoutTask;

                var status = cancellationToken.IsCancellationRequested
                    ? CaptureStatus.Cancelled
                    : CaptureStatus.TimedOut;

                return Result(
                    status,
                    File.Exists(csvPath) ? csvPath : null,
                    startedAt,
                    null,
                    status == CaptureStatus.Cancelled
                        ? "Captura cancelada."
                        : "PresentMon superó el tiempo máximo permitido.",
                    process.HasExited ? process.ExitCode : null,
                    stderrCancelled);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var finishedAt = DateTimeOffset.Now;

            if (process.ExitCode != 0)
            {
                return Result(
                    CaptureStatus.Failed,
                    File.Exists(csvPath) ? csvPath : null,
                    startedAt,
                    finishedAt,
                    $"PresentMon terminó con código {process.ExitCode}.",
                    process.ExitCode,
                    stderr);
            }

            if (!File.Exists(csvPath))
            {
                return Result(
                    CaptureStatus.Failed,
                    null,
                    startedAt,
                    finishedAt,
                    "PresentMon terminó sin generar el CSV.",
                    process.ExitCode,
                    stderr);
            }

            var fileInfo = new FileInfo(csvPath);

            if (fileInfo.Length < 100)
            {
                return Result(
                    CaptureStatus.Failed,
                    csvPath,
                    startedAt,
                    finishedAt,
                    "El CSV está vacío o no contiene datos suficientes.",
                    process.ExitCode,
                    stderr);
            }

            return Result(
                CaptureStatus.Completed,
                csvPath,
                startedAt,
                finishedAt,
                $"Captura CS2 completada. Archivo: {csvPath}",
                process.ExitCode,
                stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(
                CaptureStatus.Failed,
                File.Exists(csvPath) ? csvPath : null,
                startedAt,
                null,
                ex.Message,
                null,
                "");
        }
    }

    private static PresentMonCaptureResult Result(
        CaptureStatus status,
        string? path,
        DateTimeOffset started,
        DateTimeOffset? finished,
        string message,
        int? exitCode,
        string stderr)
    {
        var end = finished ?? DateTimeOffset.Now;

        return new PresentMonCaptureResult(
            status,
            path,
            started,
            end,
            end - started,
            exitCode,
            message,
            stderr);
    }
}
