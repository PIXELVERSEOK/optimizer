using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Native;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Hardware;

[SupportedOSPlatform("windows")]
public sealed class IntelHybridTopologyProvider
{
    private const string CpuRegistryPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public CpuTopologyInfo DiscoverTopology()
    {
        string processorName = ReadProcessorName() ?? "Intel Core (Hybrid Architecture)";
        int logicalProcessors = Environment.ProcessorCount;

        try
        {
            int length = 0;
            NativeMethods.GetLogicalProcessorInformationEx(
                NativeMethods.RelationProcessorCore,
                IntPtr.Zero,
                ref length);

            if (length <= 0)
                return CpuTopologyInfo.Default(logicalProcessors, processorName);

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.GetLogicalProcessorInformationEx(
                    NativeMethods.RelationProcessorCore,
                    buffer,
                    ref length))
                {
                    return CpuTopologyInfo.Default(logicalProcessors, processorName);
                }

                var cores = new List<CoreDescription>();
                IntPtr current = buffer;
                int offset = 0;
                int coreIndex = 0;

                while (offset < length)
                {
                    var info = (NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX)
                        Marshal.PtrToStructure(current, typeof(NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX))!;

                    ulong mask = info.Processor.GroupMask.Mask.ToUInt64();
                    bool hasSmt = (info.Processor.Flags & NativeMethods.LtpPcSmt) != 0;
                    byte efficiency = info.Processor.EfficiencyClass;

                    // El conteo de procesadores lógicos por núcleo se cuenta por bits activos
                    int logicalCount = BitOperationsCount(mask);

                    cores.Add(new CoreDescription(
                        CoreIndex: coreIndex++,
                        Type: efficiency > 0 ? CoreType.Performance : CoreType.Efficient,
                        AffinityMask: mask,
                        LogicalProcessorCount: logicalCount,
                        HasHyperThreading: hasSmt));

                    offset += info.Size;
                    current = new IntPtr(current.ToInt64() + info.Size);
                }

                bool hasMultipleClasses = cores.Select(c => c.Type).Distinct().Count() > 1;
                int pCores = cores.Count(c => c.Type == CoreType.Performance);
                int eCores = cores.Count(c => c.Type == CoreType.Efficient);

                ulong pMask = 0;
                ulong eMask = 0;
                ulong allMask = 0;

                foreach (var c in cores)
                {
                    allMask |= c.AffinityMask;
                    if (c.Type == CoreType.Performance)
                        pMask |= c.AffinityMask;
                    else if (c.Type == CoreType.Efficient)
                        eMask |= c.AffinityMask;
                }

                // Si no hay clases heterogéneas marcadas, es una CPU estándar
                if (!hasMultipleClasses)
                {
                    return new CpuTopologyInfo(
                        ProcessorName: processorName,
                        TotalPhysicalCores: cores.Count,
                        LogicalProcessors: logicalProcessors,
                        PerformanceCores: cores.Count,
                        EfficientCores: 0,
                        PerformanceCoresMask: allMask,
                        EfficientCoresMask: 0,
                        AllCoresMask: allMask,
                        IsHybrid: false,
                        Cores: cores);
                }

                return new CpuTopologyInfo(
                    ProcessorName: processorName,
                    TotalPhysicalCores: cores.Count,
                    LogicalProcessors: logicalProcessors,
                    PerformanceCores: pCores,
                    EfficientCores: eCores,
                    PerformanceCoresMask: pMask,
                    EfficientCoresMask: eMask,
                    AllCoresMask: allMask,
                    IsHybrid: true,
                    Cores: cores);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return CpuTopologyInfo.Default(logicalProcessors, processorName);
        }
    }

    private static string? ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuRegistryPath);
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static int BitOperationsCount(ulong value)
    {
        int count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }
}
