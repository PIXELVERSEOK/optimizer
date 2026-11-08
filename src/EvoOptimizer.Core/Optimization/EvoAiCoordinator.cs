using EvoOptimizer.Core.Diagnosis;

namespace EvoOptimizer.Core.Optimization;

public sealed record EvoAiInput(
    string? PrimaryBottleneck,
    double? CpuPercent,
    double? GpuPercent,
    double? MemoryUsedPercent,
    double? CommitUsedPercent,
    bool Cs2Running,
    bool HasFpsEvidence,
    int VerifiedOptimizations,
    int InconclusiveOptimizations,
    int AppliedOptimizations,
    HardwareDiagnosticSnapshot? Hardware,
    OptimizationLiveContext Live);

public sealed record EvoAiDecision(
    string Title,
    string Explanation,
    string Evidence,
    string Confidence,
    string SuggestedAction);

public sealed record EvoAiReport(
    DateTimeOffset GeneratedAt,
    string Summary,
    IReadOnlyList<EvoAiDecision> Decisions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Coordina los motores existentes y explica prioridades. No aplica cambios,
/// no confunde correlación con causalidad y se abstiene si falta evidencia.
/// </summary>
public static class EvoAiCoordinator
{
    public static EvoAiReport Analyze(EvoAiInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decisions = new List<EvoAiDecision>();
        var conflicts = new List<string>();
        var warnings = new List<string>();

        string summary = SummarizeBottleneck(input);

        decisions.Add(new EvoAiDecision(
            "Cuello de botella predominante",
            summary,
            BuildBottleneckEvidence(input),
            input.PrimaryBottleneck is null ? "UNKNOWN" : "Medium",
            input.PrimaryBottleneck is null
                ? "Recopilar al menos 5 muestras de telemetría antes de decidir."
                : "Priorizar optimizaciones de la categoría dominante."));

        AddMemoryDecision(input, decisions, conflicts, warnings);
        AddGraphicsDecision(input, decisions, warnings);
        AddStorageDecision(input, decisions, warnings);
        AddGamingDecision(input, decisions, warnings);
        AddOutcomeDecision(input, decisions, conflicts);

        if (input.AppliedOptimizations > 1 && !input.HasFpsEvidence)
        {
            warnings.Add(
                $"{input.AppliedOptimizations} optimizaciones aplicadas sin evidencia " +
                "FPS: no se puede atribuir ninguna mejora. Medir una variable por vez.");
        }

        if (input.Live.CpuTemperatureCelsius is null &&
            input.Live.GpuTemperatureCelsius is null)
        {
            warnings.Add(
                "Temperaturas UNKNOWN: EVO no declara throttling ni protección térmica " +
                "sin una fuente real de sensores.");
        }

        if (input.VerifiedOptimizations == 0 && input.InconclusiveOptimizations > 0)
        {
            warnings.Add(
                "Hay optimizaciones inconclusas. No se conservan como mejoras sin " +
                "evidencia consistente.");
        }

        return new EvoAiReport(
            DateTimeOffset.UtcNow,
            summary,
            decisions,
            conflicts,
            warnings);
    }

    private static string SummarizeBottleneck(EvoAiInput input)
    {
        return input.PrimaryBottleneck?.ToUpperInvariant() switch
        {
            "GPU" => "La GPU aparece como límite predominante. Buscar consistencia de " +
                     "frametime antes que FPS promedio; ajustes de CPU no moverán el techo.",
            "CPU" => "La CPU aparece como límite predominante. Revisar procesos " +
                     "competidores, afinidad híbrida y prioridad segura, con medición.",
            "MEMORIA" or "MEMORY" =>
                     "La memoria aparece como límite predominante. Revisar presión, " +
                     "commit y procesos; no se purga standby sin justificación.",
            "ALMACENAMIENTO" or "STORAGE" =>
                     "El almacenamiento aparece como límite predominante. Revisar salud, " +
                     "TRIM, espacio libre y latencia antes de cambiar ajustes.",
            "TÉRMICO" or "THERMAL" =>
                     "Hay indicios térmicos. Verificar sensores y rendimiento sostenido " +
                     "antes de limitar boost.",
            "ENERGÍA" or "POWER" =>
                     "Hay un límite de energía. Revisar plan y EPP con medición sostenida.",
            _ => "Sin cuello de botella demostrable todavía. EVO se abstiene de recomendar " +
                 "cambios de rendimiento sin muestras suficientes."
        };
    }

    private static string BuildBottleneckEvidence(EvoAiInput input)
    {
        return $"CPU: {Format(input.CpuPercent)}%; GPU: {Format(input.GpuPercent)}%; " +
               $"RAM: {Format(input.MemoryUsedPercent)}%; commit: " +
               $"{Format(input.CommitUsedPercent)}%; " +
               $"CS2: {(input.Cs2Running ? "en ejecución" : "no detectado")}.";
    }

    private static void AddMemoryDecision(
        EvoAiInput input,
        List<EvoAiDecision> decisions,
        List<string> conflicts,
        List<string> warnings)
    {
        var memory = input.Hardware?.Memory;

        if (memory is null)
        {
            warnings.Add("Diagnóstico de memoria no disponible: Dual Channel y " +
                         "velocidades quedan UNKNOWN.");
            return;
        }

        string suggestion = memory.Mode switch
        {
            DualChannelMode.VerifiedDualChannel =>
                "Dual Channel verificado. No hay acción de memoria por este motivo.",
            DualChannelMode.VerifiedSingleChannel =>
                "Canal único verificado. Si el equipo admite otro módulo compatible, " +
                "consultar el manual; EVO no promete mejoras sin medición.",
            _ =>
                "Dual Channel no verificable. No asumir Dual Channel por tener dos módulos."
        };

        decisions.Add(new EvoAiDecision(
            "Memoria y canales",
            suggestion,
            string.Join(" ", memory.Evidence),
            memory.Confidence.ToString(),
            "Mantener la configuración; no modificar sin evidencia."));

        if (input.Live.HighMemoryPressure &&
            input.Live.GpuPercent is >= 85)
        {
            conflicts.Add(
                "Presión de memoria y GPU saturada: bajar prioridad de procesos puede " +
                "no mejorar FPS; medir antes de conservar el cambio.");
        }

        if (memory.Warnings.Count > 0)
        {
            foreach (string warning in memory.Warnings.Take(3))
                warnings.Add(warning);
        }
    }

    private static void AddGraphicsDecision(
        EvoAiInput input,
        List<EvoAiDecision> decisions,
        List<string> warnings)
    {
        var hardware = input.Hardware;

        if (hardware is null || hardware.Graphics.Count == 0)
        {
            warnings.Add("GPU UNKNOWN: no se pudo identificar el adaptador gráfico " +
                         "ni su driver; no se recomiendan ajustes gráficos sin esa evidencia.");
            return;
        }

        var device = hardware.Graphics[0];

        string driverText = device.HasDriverEvidence
            ? $"Driver instalado: {device.DriverVersion ?? "UNKNOWN"} " +
              $"({device.DriverDate ?? "fecha UNKNOWN"})."
            : "Windows no informó versión ni fecha del driver: queda UNKNOWN.";

        decisions.Add(new EvoAiDecision(
            "GPU y driver",
            $"{device.Name ?? "Adaptador UNKNOWN"} ({device.AdapterCompatibility ?? "fabricante UNKNOWN"}). " +
            driverText + " EVO no declara el driver como antiguo ni recomienda una " +
            "actualización sin una fuente de referencia verificada.",
            $"Origen: Win32_VideoController (CIM, solo lectura). " +
            $"Estado: {device.Status ?? "UNKNOWN"}.",
            device.HasDriverEvidence ? "Medium" : "UNKNOWN",
            "Medir carga y frametimes antes de cambiar ajustes gráficos; no tocar el driver sin evidencia."));

        foreach (var other in hardware.Graphics.Skip(1))
        {
            if (!other.HasDriverEvidence)
            {
                warnings.Add($"Adaptador adicional '{other.Name ?? "UNKNOWN"}' sin " +
                             "evidencia de driver; su estado queda UNKNOWN.");
            }
        }
    }

    private static void AddStorageDecision(
        EvoAiInput input,
        List<EvoAiDecision> decisions,
        List<string> warnings)
    {
        var hardware = input.Hardware;

        if (hardware is null || hardware.Storage.Count == 0)
        {
            warnings.Add("Almacenamiento UNKNOWN: NVMe, SMART y TRIM no se pudieron verificar.");
            return;
        }

        var disk = hardware.Storage[0];

        decisions.Add(new EvoAiDecision(
            "Salud del almacenamiento",
            disk.SmartCoverage == SmartCoverage.HealthStatusOnly
                ? "HealthStatus no equivale a SMART completo. Los contadores de " +
                  "desgaste y temperatura quedan UNKNOWN."
                : $"Cobertura: {disk.SmartCoverageText}.",
            $"HealthStatus: {disk.HealthStatus ?? "UNKNOWN"}; " +
            $"TRIM: {hardware.Trim.Enabled switch { true => "activo", false => "desactivado", _ => "UNKNOWN" }}.",
            disk.SmartCoverage == SmartCoverage.ReliabilityCounters ? "High" : "Low",
            "No desfragmentar SSD ni ejecutar limpiezas automáticas. Vigilar salud."));

        if (hardware.Trim.Enabled == false)
        {
            warnings.Add("TRIM desactivado: revisar antes de modificar el sistema.");
        }
    }

    private static void AddGamingDecision(
        EvoAiInput input,
        List<EvoAiDecision> decisions,
        List<string> warnings)
    {
        if (!input.Cs2Running)
            return;

        decisions.Add(new EvoAiDecision(
            "Sesión de CS2",
            input.HasFpsEvidence
                ? "Hay evidencia FPS asociada; comparar antes/después antes de conservar cambios."
                : "CS2 está en ejecución pero no hay evidencia FPS. Cualquier mejora " +
                  "sería una suposición.",
            "PresentMon / Regression Guard",
            input.HasFpsEvidence ? "High" : "UNKNOWN",
            input.HasFpsEvidence
                ? "Revisar 1% low, 0.1% low y P99."
                : "Capturar línea base y candidato antes de declarar mejoras."));

        if (!input.HasFpsEvidence)
            warnings.Add("Sin FPS medidos, el resultado de cualquier optimización será INCONCLUSO.");
    }

    private static void AddOutcomeDecision(
        EvoAiInput input,
        List<EvoAiDecision> decisions,
        List<string> conflicts)
    {
        if (input.VerifiedOptimizations > 0)
        {
            decisions.Add(new EvoAiDecision(
                "Optimizaciones verificadas",
                $"{input.VerifiedOptimizations} optimizaciones tienen evidencia favorable.",
                "Historial SQLite / OptimizationEvidenceEvaluator",
                "High",
                "Mantener y revisar regresiones con el paso del tiempo."));
        }

        if (input.InconclusiveOptimizations > 0)
        {
            decisions.Add(new EvoAiDecision(
                "Optimizaciones inconclusas",
                $"{input.InconclusiveOptimizations} optimizaciones no demuestran mejora.",
                "Historial de mediciones",
                "Low",
                "Repetir medición o revertir; no conservar por teoría."));
        }

        if (input.AppliedOptimizations > 0 && input.PrimaryBottleneck == "GPU")
        {
            conflicts.Add(
                "Optimizaciones de CPU aplicadas con GPU como límite: la ganancia " +
                "esperada es consistencia, no FPS promedio.");
        }
    }

    private static string Format(double? value) =>
        value.HasValue ? value.Value.ToString("F1") : "UNKNOWN";
}