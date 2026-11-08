using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EvoOptimizer.Infrastructure.Hardware;

public sealed record WindowsPowerTelemetry(
    bool? IsOnAcPower,
    string? ActiveSchemeGuid,
    uint? ProcessorEppAc,
    uint? ProcessorEppDc,
    IReadOnlyList<string> Warnings);

/// <summary>Lee el estado de alimentación y EPP del plan activo sin modificarlo.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerTelemetryService
{
    private static readonly Guid ProcessorSubgroup =
        new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid EnergyPerformancePreference =
        new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

    public WindowsPowerTelemetry ReadCurrent()
    {
        var warnings = new List<string>();
        bool? isOnAcPower = ReadAcLineStatus(warnings);
        string? schemeGuid = null;
        uint? eppAc = null;
        uint? eppDc = null;
        IntPtr activeScheme = IntPtr.Zero;

        try
        {
            uint result = PowerGetActiveScheme(IntPtr.Zero, out activeScheme);
            if (result != 0 || activeScheme == IntPtr.Zero)
            {
                warnings.Add($"No se pudo leer el GUID del plan activo: {new Win32Exception((int)result).Message}");
            }
            else
            {
                Guid scheme = Marshal.PtrToStructure<Guid>(activeScheme);
                schemeGuid = scheme.ToString("D");
                eppAc = ReadEnergyPreference(scheme, acPower: true, warnings);
                eppDc = ReadEnergyPreference(scheme, acPower: false, warnings);
            }
        }
        finally
        {
            if (activeScheme != IntPtr.Zero)
                _ = LocalFree(activeScheme);
        }

        return new WindowsPowerTelemetry(
            isOnAcPower,
            schemeGuid,
            eppAc,
            eppDc,
            warnings);
    }

    private static bool? ReadAcLineStatus(List<string> warnings)
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            warnings.Add(
                $"No se pudo consultar la fuente de alimentación: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return null;
        }

        return status.AcLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }

    private static uint? ReadEnergyPreference(
        Guid scheme,
        bool acPower,
        List<string> warnings)
    {
        Guid subgroup = ProcessorSubgroup;
        Guid setting = EnergyPerformancePreference;
        uint result;
        uint value;

        if (acPower)
        {
            result = PowerReadACValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref setting,
                out value);
        }
        else
        {
            result = PowerReadDCValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref setting,
                out value);
        }

        if (result != 0)
        {
            warnings.Add(
                $"EPP {(acPower ? "AC" : "DC")} no disponible: {new Win32Exception((int)result).Message}");
            return null;
        }

        if (value > 100)
        {
            warnings.Add($"EPP {(acPower ? "AC" : "DC")} devolvió {value}, fuera del rango documentado 0-100.");
            return null;
        }

        return value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(
        IntPtr userRootPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid settingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid settingGuid,
        out uint dcValueIndex);

    // PowerGetActiveScheme asigna el buffer con el asignador local; powrprof.dll
    // NO exporta "PowerFreeMemory". Liberarlo con LocalFree (kernel32) es el
    // contrato documentado; usar un export inexistente provocaba
    // EntryPointNotFoundException y tumbaba el arranque de la app.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
