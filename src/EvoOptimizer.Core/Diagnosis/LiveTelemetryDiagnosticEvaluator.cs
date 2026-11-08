using System.Globalization;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Telemetry;

namespace EvoOptimizer.Core.Diagnosis;

/// <summary>
/// Adds explainable observations from the dashboard's real system-wide CPU and
/// memory samples. It does not infer a game bottleneck or recommend system changes.
/// </summary>
public static class LiveTelemetryDiagnosticEvaluator
{
    private const int MinimumSamples = 5;
    private const double CpuHighThresholdPercent = 85;
    private const double MemoryHighThresholdPercent = 90;
    private const double SustainedSampleRatio = 0.8;

    public static IReadOnlyList<DiagnosticFinding> Evaluate(
        HardwareProfile hardware,
        IReadOnlyList<TelemetrySnapshot> samples)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(samples);

        var findings = new List<DiagnosticFinding>
        {
            CreateHardwareFinding(hardware)
        };

        var cpuSamples = samples
            .Where(sample => sample.CpuUtilizationPercent is >= 0 and <= 100)
            .ToArray();
        var memorySamples = samples
            .Where(sample => sample.MemoryLoadPercent is >= 0 and <= 100)
            .ToArray();

        findings.Add(CreateCoverageFinding(samples, cpuSamples.Length, memorySamples.Length));

        if (cpuSamples.Length >= MinimumSamples)
            findings.Add(CreateLoadFinding(
                "LIVE_CPU_LOAD",
                "Utilización global de CPU observada",
                cpuSamples.Select(sample => sample.CpuUtilizationPercent!.Value).ToArray(),
                CpuHighThresholdPercent,
                "CPU"));

        if (memorySamples.Length >= MinimumSamples)
            findings.Add(CreateLoadFinding(
                "LIVE_MEMORY_LOAD",
                "Carga de memoria física observada",
                memorySamples.Select(sample => sample.MemoryLoadPercent!.Value).ToArray(),
                MemoryHighThresholdPercent,
                "memoria"));

        return findings;
    }

    private static DiagnosticFinding CreateHardwareFinding(HardwareProfile hardware)
    {
        string cpu = string.IsNullOrWhiteSpace(hardware.CpuName)
            ? "CPU no informada"
            : hardware.CpuName;
        string memory = hardware.TotalMemoryBytes is > 0
            ? FormatGb(hardware.TotalMemoryBytes.Value)
            : "RAM no informada";
        string gpus = hardware.Gpus.Count == 0
            ? "GPU no informada por la fuente"
            : string.Join(", ", hardware.Gpus.Select(gpu => gpu.Name));
        string os = string.IsNullOrWhiteSpace(hardware.OsDescription)
            ? "Windows no informado"
            : hardware.OsDescription;

        bool hasEvidence = hardware.CpuName is not null ||
                           hardware.TotalMemoryBytes is > 0 ||
                           hardware.Gpus.Count > 0;

        return new DiagnosticFinding(
            "LIVE_HARDWARE_PROFILE",
            DiagnosticSeverity.Information,
            "Perfil de hardware detectado",
            $"{cpu}; {hardware.LogicalProcessors} procesadores lógicos; {memory}; GPU: {gpus}.",
            $"Sistema: {os}. Fuente: perfil obtenido al iniciar desde el registro y APIs de Windows.",
            hasEvidence ? "Medium" : "Low",
            "La identificación no demuestra un cuello de botella. Las métricas sin fuente compatible quedan como no informadas.");
    }

    private static DiagnosticFinding CreateCoverageFinding(
        IReadOnlyList<TelemetrySnapshot> samples,
        int cpuCount,
        int memoryCount)
    {
        var timestamps = samples
            .Where(sample => sample.CpuUtilizationPercent is not null ||
                             sample.MemoryLoadPercent is not null)
            .Select(sample => sample.Timestamp)
            .ToArray();

        string interval = timestamps.Length >= 2
            ? $" Ventana: {(timestamps.Max() - timestamps.Min()).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s."
            : string.Empty;

        bool sufficient = cpuCount >= MinimumSamples || memoryCount >= MinimumSamples;
        string explanation = sufficient
            ? $"Muestras válidas: CPU {cpuCount}, memoria {memoryCount}.{interval}"
            : $"Evidencia insuficiente para caracterizar carga: CPU {cpuCount}/{MinimumSamples}, memoria {memoryCount}/{MinimumSamples} muestras mínimas.{interval}";

        return new DiagnosticFinding(
            "LIVE_TELEMETRY_COVERAGE",
            DiagnosticSeverity.Information,
            sufficient ? "Cobertura de telemetría en vivo" : "Telemetría en vivo insuficiente",
            explanation,
            "CPU: delta de GetSystemTimes (uso global); memoria física: GlobalMemoryStatusEx. La disponibilidad de cada métrica se cuenta por separado.",
            sufficient ? "High" : "Low",
            "Recopilar más muestras antes de interpretar una carga sostenida. Estos datos no incluyen uso por proceso, GPU, temperaturas, FPS ni frametime.");
    }

    private static DiagnosticFinding CreateLoadFinding(
        string id,
        string title,
        IReadOnlyList<double> values,
        double highThreshold,
        string metric)
    {
        double average = values.Average();
        double maximum = values.Max();
        int elevatedSamples = values.Count(value => value >= highThreshold);
        bool sustained = elevatedSamples >= Math.Ceiling(values.Count * SustainedSampleRatio);
        string thresholdText = highThreshold.ToString("F0", CultureInfo.InvariantCulture);

        string explanation = sustained
            ? $"Se observó carga global de {metric} sostenida por encima de {thresholdText}% en {elevatedSamples}/{values.Count} muestras."
            : $"La carga global de {metric} promedió {average:F0}% y alcanzó {maximum:F0}% en {values.Count} muestras; no cumple el criterio de carga alta sostenida (≥{thresholdText}% en al menos el 80% de las muestras).";

        if (sustained)
            explanation = $"Promedio {average:F0}%, máximo {maximum:F0}%; " + explanation;

        return new DiagnosticFinding(
            id,
            sustained ? DiagnosticSeverity.Warning : DiagnosticSeverity.Information,
            title,
            explanation,
            $"Medición real del sistema; umbral observacional: ≥{thresholdText}% en ≥80% de {values.Count} muestras válidas.",
            "Medium",
            sustained
                ? "Es una señal de carga, no un diagnóstico de cuello de botella. Correlacionar con la tarea activa y métricas por proceso antes de considerar cambios."
                : "No se observó carga alta sostenida en esta ventana. No implica que el sistema esté libre de otros límites.");
    }

    private static string FormatGb(ulong bytes)
        => $"{bytes / 1024d / 1024d / 1024d:F1} GB";
}
