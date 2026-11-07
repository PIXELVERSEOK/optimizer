using System.Runtime.Versioning;
using EvoOptimizer.Core.Telemetry;
using EvoOptimizer.Infrastructure.Native;

namespace EvoOptimizer.Infrastructure.Telemetry;

/// <summary>
/// CPU: delta de GetSystemTimes entre muestras (utilización global, 0..100).
/// La primera muestra devuelve null porque no existe delta todavía.
/// Memoria: GlobalMemoryStatusEx (física y commit).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTelemetryProvider : ITelemetryProvider
{
    private bool _hasPrevious;
    private ulong _prevIdle, _prevKernel, _prevUser;

    public TelemetrySnapshot Sample()
    {
        double? memLoad = null;
        ulong? total = null, avail = null, commitUsed = null, commitLimit = null;

        if (NativeMethods.TryGetMemoryStatus(out var m))
        {
            memLoad = m.dwMemoryLoad;
            total = m.ullTotalPhys;
            avail = m.ullAvailPhys;
            commitLimit = m.ullTotalPageFile;
            commitUsed = m.ullTotalPageFile >= m.ullAvailPageFile
                ? m.ullTotalPageFile - m.ullAvailPageFile
                : null;
        }

        return new TelemetrySnapshot(
            DateTimeOffset.Now, ReadCpuPercent(), memLoad, total, avail, commitUsed, commitLimit);
    }

    private double? ReadCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        ulong i = idle.Value, k = kernel.Value, u = user.Value;
        double? result = null;

        if (_hasPrevious && i >= _prevIdle && k >= _prevKernel && u >= _prevUser)
        {
            ulong dIdle = i - _prevIdle;
            ulong dTotal = (k - _prevKernel) + (u - _prevUser);
            if (dTotal > 0 && dTotal >= dIdle)
                result = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
        }

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;
        _hasPrevious = true;
        return result;
    }
}
