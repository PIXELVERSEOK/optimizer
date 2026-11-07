namespace EvoOptimizer.Core.Telemetry;

/// <summary>
/// Muestra puntual. Todo valor es nullable: null significa "no medido / no disponible",
/// nunca "cero". Ningún consumidor debe asumir un valor por defecto.
/// </summary>
public sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    double? CpuUtilizationPercent,
    double? MemoryLoadPercent,
    ulong? MemoryTotalBytes,
    ulong? MemoryAvailableBytes,
    ulong? CommitUsedBytes,
    ulong? CommitLimitBytes);
