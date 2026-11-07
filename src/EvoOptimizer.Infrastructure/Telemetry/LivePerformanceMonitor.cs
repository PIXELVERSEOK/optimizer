using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EvoOptimizer.Infrastructure.Telemetry;

public sealed record LiveMonitorOptions(
    TimeSpan SamplingInterval,
    double HighCpuThresholdPercent,
    double HighMemoryThresholdPercent,
    double HighCs2CpuThresholdPercent,
    int HistoryCapacity)
{
    public static LiveMonitorOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        90,
        90,
        85,
        600);
}

public sealed record ProcessSummary(
    int ProcessId,
    string Name,
    double CpuPercent,
    double MemoryMb);

public sealed record LivePerformanceSnapshot(
    DateTimeOffset Timestamp,
    double SystemCpuPercent,
    double? Cs2CpuPercent,
    double TotalMemoryMb,
    double AvailableMemoryMb,
    double MemoryUsagePercent,
    double CommitUsedMb,
    double CommitLimitMb,
    double Cs2MemoryMb,
    int Cs2ProcessCount,
    int ActiveProcessCount,
    IReadOnlyList<int> Cs2ProcessIds,
    IReadOnlyList<ProcessSummary> TopProcesses,
    double? GpuUsagePercent,
    double? GpuSharedMemoryMb,
    IReadOnlyList<string> Alerts);

[SupportedOSPlatform("windows")]
public sealed class LivePerformanceMonitor : IDisposable
{
    private readonly LiveMonitorOptions _options;
    private readonly ConcurrentQueue<LivePerformanceSnapshot> _history = new();
    private readonly Dictionary<int, TimeSpan> _previousCs2Cpu = new();
    private readonly Dictionary<int, TimeSpan> _previousAllProcessCpu = new();
    private readonly WindowsGpuReader _gpuReader = new();

    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private DateTimeOffset? _previousCapture;
    private int _running;
    private bool _disposed;

    public event Action<LivePerformanceSnapshot>? SnapshotCaptured;

    public LivePerformanceMonitor(LiveMonitorOptions? options = null)
    {
        _options = options ?? LiveMonitorOptions.Default;

        if (_options.SamplingInterval < TimeSpan.FromMilliseconds(250))
            throw new ArgumentOutOfRangeException(
                nameof(options), "El intervalo mínimo es 250 ms.");

        if (_options.HistoryCapacity < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), "El historial debe tener capacidad positiva.");
    }

    public IReadOnlyList<LivePerformanceSnapshot> GetHistory()
        => _history.ToArray();

    public LivePerformanceSnapshot? GetLatest()
        => _history.TryPeek(out _)
            ? _history.LastOrDefault()
            : null;

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException(
                "El monitor ya está en ejecución.");

        try
        {
            using var timer = new PeriodicTimer(_options.SamplingInterval);

            // Primera muestra: establece la referencia de CPU.
            CaptureAndPublish();

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureAndPublish();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void CaptureAndPublish()
    {
        var snapshot = Capture();

        _history.Enqueue(snapshot);

        while (_history.Count > _options.HistoryCapacity)
            _history.TryDequeue(out _);

        try
        {
            SnapshotCaptured?.Invoke(snapshot);
        }
        catch
        {
            // Un consumidor externo no debe detener la monitorización.
        }
    }

    private LivePerformanceSnapshot Capture()
    {
        var now = DateTimeOffset.UtcNow;

        var cpu = ReadSystemCpu();
        var memory = ReadMemory();
        var processes = ReadProcesses(now);
        var (gpuUsage, gpuSharedMem) = _gpuReader.Read();

        var alerts = new List<string>();

        if (cpu >= _options.HighCpuThresholdPercent)
            alerts.Add("HIGH_SYSTEM_CPU");

        if (memory.UsagePercent >= _options.HighMemoryThresholdPercent)
            alerts.Add("HIGH_MEMORY_PRESSURE");

        if (memory.CommitLimitMb > 0 && (memory.CommitUsedMb / memory.CommitLimitMb >= 0.90))
            alerts.Add("HIGH_COMMIT_PRESSURE");

        if (processes.Cs2CpuPercent >= _options.HighCs2CpuThresholdPercent)
            alerts.Add("HIGH_CS2_CPU");

        if (processes.Cs2Count == 0)
            alerts.Add("CS2_NOT_RUNNING");

        if (gpuUsage.HasValue && gpuUsage.Value >= 95)
            alerts.Add("HIGH_GPU_USAGE");

        return new LivePerformanceSnapshot(
            now,
            cpu,
            processes.Cs2CpuPercent,
            memory.TotalMb,
            memory.AvailableMb,
            memory.UsagePercent,
            memory.CommitUsedMb,
            memory.CommitLimitMb,
            processes.Cs2MemoryMb,
            processes.Cs2Count,
            processes.ActiveCount,
            processes.Cs2ProcessIds,
            processes.TopProcesses,
            gpuUsage,
            gpuSharedMem,
            alerts);
    }

    private double ReadSystemCpu()
    {
        if (!GetSystemTimes(
            out var idleTime,
            out var kernelTime,
            out var userTime))
        {
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);

        var result = 0d;

        if (_previousIdle.HasValue &&
            _previousKernel.HasValue &&
            _previousUser.HasValue)
        {
            var idleDelta = Delta(idle, _previousIdle.Value);
            var kernelDelta = Delta(kernel, _previousKernel.Value);
            var userDelta = Delta(user, _previousUser.Value);

            var total = kernelDelta + userDelta;

            if (total > 0 && total >= idleDelta)
                result = (double)(total - idleDelta) / total * 100;
        }

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        return Math.Clamp(result, 0, 100);
    }

    private (double TotalMb, double AvailableMb, double UsagePercent, double CommitUsedMb, double CommitLimitMb)
        ReadMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status) ||
            status.TotalPhysical == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        var total = status.TotalPhysical / 1024d / 1024d;
        var available = status.AvailablePhysical / 1024d / 1024d;
        var usage = (total - available) / total * 100;

        var commitLimit = status.TotalPageFile / 1024d / 1024d;
        var commitAvail = status.AvailablePageFile / 1024d / 1024d;
        var commitUsed = Math.Max(0, commitLimit - commitAvail);

        return (
            Math.Round(total, 1),
            Math.Round(available, 1),
            Math.Round(Math.Clamp(usage, 0, 100), 1),
            Math.Round(commitUsed, 1),
            Math.Round(commitLimit, 1));
    }

    private ProcessMetrics ReadProcesses(DateTimeOffset now)
    {
        using var processList = new ProcessCollection(Process.GetProcesses());

        var activeCount = 0;
        var cs2Count = 0;
        var cs2Memory = 0L;
        var cs2Cpu = 0d;
        var cs2Ids = new List<int>();
        var currentCs2Cpu = new Dictionary<int, TimeSpan>();
        var currentAllCpu = new Dictionary<int, TimeSpan>();
        var topProcessCandidates = new List<ProcessSummary>();

        double elapsedSeconds = 0;
        if (_previousCapture.HasValue)
        {
            elapsedSeconds = (now - _previousCapture.Value).TotalSeconds;
        }

        int processorCount = Environment.ProcessorCount;

        foreach (var process in processList.Items)
        {
            try
            {
                activeCount++;
                int pid = process.Id;
                string pName = process.ProcessName;
                bool isCs2 = string.Equals(pName, "cs2", StringComparison.OrdinalIgnoreCase);

                if (isCs2)
                {
                    cs2Count++;
                    cs2Ids.Add(pid);
                    cs2Memory += process.WorkingSet64;
                }

                var cpuTime = process.TotalProcessorTime;
                currentAllCpu[pid] = cpuTime;

                if (isCs2)
                {
                    currentCs2Cpu[pid] = cpuTime;
                }

                if (elapsedSeconds > 0 && processorCount > 0 && _previousAllProcessCpu.TryGetValue(pid, out var prevCpu))
                {
                    var delta = cpuTime - prevCpu;
                    if (delta > TimeSpan.Zero)
                    {
                        double pct = delta.TotalSeconds / (elapsedSeconds * processorCount) * 100d;
                        if (pct >= 0.5 && !string.Equals(pName, "Idle", StringComparison.OrdinalIgnoreCase))
                        {
                            topProcessCandidates.Add(new ProcessSummary(
                                pid,
                                pName,
                                Math.Round(Math.Clamp(pct, 0, 100), 1),
                                Math.Round(process.WorkingSet64 / 1024d / 1024d, 1)));
                        }

                        if (isCs2)
                        {
                            cs2Cpu += pct;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // El proceso pudo finalizar durante la captura.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Acceso restringido: se omite ese proceso.
            }
        }

        _previousCs2Cpu.Clear();
        foreach (var item in currentCs2Cpu)
            _previousCs2Cpu[item.Key] = item.Value;

        _previousAllProcessCpu.Clear();
        foreach (var item in currentAllCpu)
            _previousAllProcessCpu[item.Key] = item.Value;

        _previousCapture = now;

        var topProcesses = topProcessCandidates
            .OrderByDescending(x => x.CpuPercent)
            .Take(4)
            .ToArray();

        return new ProcessMetrics(
            Math.Round(Math.Clamp(cs2Cpu, 0, 100), 1),
            Math.Round(cs2Memory / 1024d / 1024d, 1),
            cs2Count,
            activeCount,
            cs2Ids,
            topProcesses);
    }

    private static ulong Delta(ulong current, ulong previous)
        => current >= previous ? current - previous : 0;

    private static ulong ToUInt64(FileTime value)
        => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SnapshotCaptured = null;
        _history.Clear();
        _previousCs2Cpu.Clear();
        _previousAllProcessCpu.Clear();
        _gpuReader.Dispose();
    }

    private sealed record ProcessMetrics(
        double Cs2CpuPercent,
        double Cs2MemoryMb,
        int Cs2Count,
        int ActiveCount,
        IReadOnlyList<int> Cs2ProcessIds,
        IReadOnlyList<ProcessSummary> TopProcesses);

    private sealed class ProcessCollection : IDisposable
    {
        public Process[] Items { get; }

        public ProcessCollection(Process[] items)
            => Items = items;

        public void Dispose()
        {
            foreach (var process in Items)
                process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
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
