namespace EvoOptimizer.Core.Hardware;

public sealed record GpuInfo(string Name, ulong? DedicatedMemoryBytes);

/// <summary>Identidad del equipo. Null = no se pudo determinar.</summary>
public sealed record HardwareProfile(
    string? OsDescription,
    string? CpuName,
    int LogicalProcessors,
    uint? CpuNominalMhz,
    ulong? TotalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus);
