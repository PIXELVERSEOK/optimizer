namespace EvoOptimizer.Infrastructure.Optimization;

public enum MemoryPressureLevel
{
    Unknown,
    Normal,
    Moderate,
    High,
    Critical
}

public sealed record MemoryPressureEvidence(
    long? TotalPhysicalBytes,
    long? AvailablePhysicalBytes,
    long? CommitChargeBytes,
    long? CommitLimitBytes,
    double? SystemHardFaultsPerSecond);

public sealed record MemoryPressureResult(
    MemoryPressureLevel Level,
    double? PhysicalMemoryUsedPercent,
    double? CommitUsedPercent,
    double? HardFaultsPerSecond,
    bool HasSufficientEvidence,
    IReadOnlyList<string> Findings);

/// <summary>
/// Evalúa presión de memoria exclusivamente a partir de métricas medidas.
/// No modifica procesos, memoria virtual ni configuración de Windows.
/// </summary>
public sealed class MemoryPressureEvaluator
{
    public MemoryPressureResult Evaluate(MemoryPressureEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var findings = new List<string>();
        double? physicalUsed = null;
        double? commitUsed = null;

        if (evidence.TotalPhysicalBytes is > 0 &&
            evidence.AvailablePhysicalBytes is >= 0)
        {
            long total = evidence.TotalPhysicalBytes.Value;
            long available = evidence.AvailablePhysicalBytes.Value;

            if (available <= total)
            {
                physicalUsed = (1d - (double)available / total) * 100d;
                findings.Add(
                    $"Uso de memoria física: {physicalUsed.Value:F1}%.");
            }
            else
            {
                findings.Add("Lectura física inconsistente: disponible supera el total.");
            }
        }

        if (evidence.CommitChargeBytes is >= 0 &&
            evidence.CommitLimitBytes is > 0)
        {
            long charge = evidence.CommitChargeBytes.Value;
            long limit = evidence.CommitLimitBytes.Value;

            if (charge <= limit)
            {
                commitUsed = (double)charge / limit * 100d;
                findings.Add($"Commit utilizado: {commitUsed.Value:F1}%.");
            }
            else
            {
                findings.Add("Lectura de commit inconsistente: consumo supera el límite.");
            }
        }

        double? hardFaults = evidence.SystemHardFaultsPerSecond;

        if (hardFaults is < 0)
        {
            hardFaults = null;
            findings.Add("Se descartó una lectura negativa de hard faults.");
        }
        else if (hardFaults.HasValue)
        {
            findings.Add($"Hard faults del sistema: {hardFaults.Value:F1}/s.");
        }

        if (!physicalUsed.HasValue && !commitUsed.HasValue)
        {
            findings.Add("Datos insuficientes para evaluar presión de memoria.");

            return new(
                MemoryPressureLevel.Unknown,
                null,
                null,
                hardFaults,
                false,
                findings);
        }

        MemoryPressureLevel physicalLevel = ClassifyPhysical(physicalUsed);
        MemoryPressureLevel commitLevel = ClassifyCommit(commitUsed);

        MemoryPressureLevel level = Max(physicalLevel, commitLevel);

        // Los hard faults solo corroboran presión física moderada o superior.
        // Nunca generan por sí solos una recomendación de optimización.
        if (hardFaults is >= 50 &&
            physicalLevel >= MemoryPressureLevel.Moderate)
        {
            level = PromoteOneLevel(level);
            findings.Add("Los hard faults corroboran la presión física observada.");
        }

        findings.Add($"Clasificación resultante: {level}.");

        return new(
            level,
            physicalUsed,
            commitUsed,
            hardFaults,
            true,
            findings);
    }

    private static MemoryPressureLevel ClassifyPhysical(double? used)
    {
        if (!used.HasValue)
            return MemoryPressureLevel.Unknown;

        if (used.Value >= 95)
            return MemoryPressureLevel.Critical;

        if (used.Value >= 90)
            return MemoryPressureLevel.High;

        if (used.Value >= 80)
            return MemoryPressureLevel.Moderate;

        return MemoryPressureLevel.Normal;
    }

    private static MemoryPressureLevel ClassifyCommit(double? used)
    {
        if (!used.HasValue)
            return MemoryPressureLevel.Unknown;

        if (used.Value >= 98)
            return MemoryPressureLevel.Critical;

        if (used.Value >= 90)
            return MemoryPressureLevel.High;

        if (used.Value >= 80)
            return MemoryPressureLevel.Moderate;

        return MemoryPressureLevel.Normal;
    }

    private static MemoryPressureLevel Max(
        MemoryPressureLevel first,
        MemoryPressureLevel second)
    {
        return (MemoryPressureLevel)Math.Max((int)first, (int)second);
    }

    private static MemoryPressureLevel PromoteOneLevel(
        MemoryPressureLevel level)
    {
        return level switch
        {
            MemoryPressureLevel.Moderate => MemoryPressureLevel.High,
            MemoryPressureLevel.High => MemoryPressureLevel.Critical,
            _ => level
        };
    }
}