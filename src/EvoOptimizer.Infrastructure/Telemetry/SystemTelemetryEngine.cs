using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EvoOptimizer.Infrastructure.Telemetry;

public sealed record TelemetrySample(
    DateTimeOffset Timestamp,
    double SystemCpuPercent,
    double? Cs2CpuPercent,
    double TotalMemoryMb,
    double AvailableMemoryMb,
    double MemoryUsagePercent,
    double Cs2MemoryMb,
    int Cs2ProcessCount,
    int ActiveProcessCount,
    string Cs2ProcessIds);

public sealed record TelemetryCaptureResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int SampleCount,
    string CsvPath,
    double AverageSystemCpuPercent,
    double? AverageCs2CpuPercent,
    double AverageMemoryUsagePercent,
    double PeakCs2MemoryMb);

[SupportedOSPlatform("windows")]
public sealed class SystemTelemetryEngine
{
    private readonly TimeSpan _sampleInterval;

    public SystemTelemetryEngine(TimeSpan? sampleInterval = null)
    {
        _sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);

        if (_sampleInterval < TimeSpan.FromMilliseconds(250))
            throw new ArgumentOutOfRangeException(
                nameof(sampleInterval),
                "El intervalo mínimo es de 250 ms.");
    }

    public async Task<TelemetryCaptureResult> CaptureAsync(
        TimeSpan duration,
        string csvPath,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        var fullPath = Path.GetFullPath(csvPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var startedAt = DateTimeOffset.UtcNow;
        var samples = new List<TelemetrySample>();

        var previousSystem = ReadSystemCpuTimes();
        var previousTime = Stopwatch.GetTimestamp();
        var previousCs2CpuTimes =
            new Dictionary<(int ProcessId, long StartTimeTicks), TimeSpan>();

        await using var writer = new StreamWriter(
            fullPath,
            append: false,
            encoding: new System.Text.UTF8Encoding(true));

        await writer.WriteLineAsync(
            "Timestamp,SystemCpuPercent,Cs2CpuPercent,TotalMemoryMb," +
            "AvailableMemoryMb,MemoryUsagePercent,Cs2MemoryMb," +
            "Cs2ProcessCount,ActiveProcessCount,Cs2ProcessIds");

        while (DateTimeOffset.UtcNow - startedAt < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            var currentTime = Stopwatch.GetTimestamp();
            var currentSystem = ReadSystemCpuTimes();

            var elapsedTicks = currentTime - previousTime;
            var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;

            var systemCpu = CalculateSystemCpu(
                previousSystem,
                currentSystem);

            var memory = ReadMemoryStatus();
            var cs2 = ReadCs2Metrics(
                elapsedSeconds,
                previousCs2CpuTimes);
            var processCount = Process.GetProcesses().Length;

            var sample = new TelemetrySample(
                now,
                systemCpu,
                cs2.CpuPercent,
                memory.TotalMb,
                memory.AvailableMb,
                memory.UsagePercent,
                cs2.MemoryMb,
                cs2.ProcessCount,
                processCount,
                string.Join(";", cs2.ProcessIds));

            samples.Add(sample);

            await writer.WriteLineAsync(string.Join(",",
                sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                Format(sample.SystemCpuPercent),
                Format(sample.Cs2CpuPercent),
                Format(sample.TotalMemoryMb),
                Format(sample.AvailableMemoryMb),
                Format(sample.MemoryUsagePercent),
                Format(sample.Cs2MemoryMb),
                sample.Cs2ProcessCount,
                sample.ActiveProcessCount,
                $"\"{sample.Cs2ProcessIds}\""));

            await writer.FlushAsync(cancellationToken);

            previousSystem = currentSystem;
            previousTime = currentTime;
            previousCs2CpuTimes = cs2.CurrentCpuTimes;

            var remaining = duration - (DateTimeOffset.UtcNow - startedAt);

            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(
                    remaining < _sampleInterval ? remaining : _sampleInterval,
                    cancellationToken);
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;

        return new TelemetryCaptureResult(
            startedAt,
            finishedAt,
            samples.Count,
            fullPath,
            Average(samples.Select(x => x.SystemCpuPercent)),
            Average(samples.Select(x => x.Cs2CpuPercent)),
            Average(samples.Select(x => x.MemoryUsagePercent)),
            samples.Count == 0 ? 0 : samples.Max(x => x.Cs2MemoryMb));
    }

    private static (
        double? CpuPercent,
        double MemoryMb,
        int ProcessCount,
        IReadOnlyList<int> ProcessIds,
        Dictionary<(int ProcessId, long StartTimeTicks), TimeSpan> CurrentCpuTimes)
        ReadCs2Metrics(
            double elapsedSeconds,
            Dictionary<(int ProcessId, long StartTimeTicks), TimeSpan> previousCpuTimes)
    {
        var memoryBytes = 0L;
        var ids = new List<int>();

        var currentCpuTimes =
            new Dictionary<(int ProcessId, long StartTimeTicks), TimeSpan>();

        var cpuDeltaSeconds = 0d;
        var allProcessesHaveBaseline = true;

        foreach (var process in Process.GetProcessesByName("cs2"))
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var startTimeTicks = process.StartTime.ToUniversalTime().Ticks;
                    var totalProcessorTime = process.TotalProcessorTime;

                    var identity = (processId, startTimeTicks);

                    currentCpuTimes[identity] = totalProcessorTime;

                    memoryBytes += process.WorkingSet64;
                    ids.Add(processId);

                    if (previousCpuTimes.TryGetValue(
                        identity,
                        out var previousCpuTime))
                    {
                        var delta = totalProcessorTime - previousCpuTime;

                        if (delta >= TimeSpan.Zero)
                        {
                            cpuDeltaSeconds += delta.TotalSeconds;
                        }
                        else
                        {
                            allProcessesHaveBaseline = false;
                        }
                    }
                    else
                    {
                        // Proceso nuevo: todavía no hay intervalo comparable.
                        allProcessesHaveBaseline = false;
                    }
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    UnauthorizedAccessException)
                {
                    // No se inventa una medición si Windows no permite leer
                    // todos los contadores necesarios.
                    allProcessesHaveBaseline = false;
                }
            }
        }

        double? cpuPercent = null;

        if (ids.Count > 0 &&
            allProcessesHaveBaseline &&
            elapsedSeconds > 0 &&
            double.IsFinite(elapsedSeconds) &&
            Environment.ProcessorCount > 0)
        {
            cpuPercent = Math.Clamp(
                cpuDeltaSeconds /
                elapsedSeconds /
                Environment.ProcessorCount * 100d,
                0d,
                100d);
        }

        return (
            cpuPercent,
            memoryBytes / 1024d / 1024d,
            ids.Count,
            ids,
            currentCpuTimes);
    }

    private static double CalculateSystemCpu(
        CpuTimes previous,
        CpuTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = kernel + user;

        if (total == 0 || total < idle)
            return 0;

        return Math.Clamp(
            (total - idle) * 100d / total,
            0d,
            100d);
    }

    private static CpuTimes ReadSystemCpuTimes()
    {
        if (!GetSystemTimes(
                out var idle,
                out var kernel,
                out var user))
        {
            throw new InvalidOperationException(
                "No se pudieron leer los contadores de CPU de Windows.");
        }

        return new CpuTimes(
            idle.ToUInt64(),
            kernel.ToUInt64(),
            user.ToUInt64());
    }

    private static MemoryMetrics ReadMemoryStatus()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException(
                "No se pudo consultar la memoria física.");

        var total = status.TotalPhysical / 1024d / 1024d;
        var available = status.AvailablePhysical / 1024d / 1024d;

        return new MemoryMetrics(
            total,
            available,
            total <= 0 ? 0 :
                Math.Clamp((total - available) * 100d / total, 0, 100));
    }

    private static double Average(IEnumerable<double> values)
    {
        var data = values.Where(double.IsFinite).ToArray();
        return data.Length == 0 ? 0 : data.Average();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var data = values
            .Where(x => x.HasValue && double.IsFinite(x.Value))
            .Select(x => x!.Value)
            .ToArray();

        return data.Length == 0 ? null : data.Average();
    }

    private static string Format(double value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Format(double? value) =>
        value.HasValue
            ? Format(value.Value)
            : string.Empty;

    private sealed record CpuTimes(
        ulong Idle,
        ulong Kernel,
        ulong User);

    private sealed record MemoryMetrics(
        double TotalMb,
        double AvailableMb,
        double UsagePercent);

    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() =>
            ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(
        ref MemoryStatusEx buffer);
}
