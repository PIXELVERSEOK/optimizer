using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Native;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Hardware;

/// <summary>
/// Perfil básico desde el registro y APIs nativas, sin elevación.
/// El registro puede incluir adaptadores inactivos; la VRAM depende del dato reportado por el driver.
/// No distingue iGPU/dGPU ni determina núcleos físicos en esta fase.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryHardwareProfileProvider : IHardwareProfileProvider
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11cd-bfc1-08002be10318}";

    public HardwareProfile Capture()
    {
        string? cpuName = null;
        uint? mhz = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
            cpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            if (key?.GetValue("~MHz") is int m && m > 0)
                mhz = (uint)m;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        ulong? totalRam = NativeMethods.TryGetMemoryStatus(out var ms) ? ms.ullTotalPhys : null;

        return new HardwareProfile(
            RuntimeInformation.OSDescription,
            string.IsNullOrWhiteSpace(cpuName) ? null : cpuName,
            Environment.ProcessorCount,
            mhz,
            totalRam,
            ReadGpus());
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return gpus;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;

                using var key = cls.OpenSubKey(sub);
                if (key is null) continue;

                var name = (key.GetValue("DriverDesc") as string)?.Trim();
                if (string.IsNullOrEmpty(name) || gpus.Any(g => g.Name == name)) continue;

                ulong? vram = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long value when value > 0 => (ulong)value,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                    _ => null
                };
                if (vram == 0) vram = null;

                gpus.Add(new GpuInfo(name, vram));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        return gpus;
    }
}
