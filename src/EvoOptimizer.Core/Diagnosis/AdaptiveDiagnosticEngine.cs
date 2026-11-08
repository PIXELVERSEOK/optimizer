
using System.Globalization;
using EvoOptimizer.Core.Audit;

namespace EvoOptimizer.Core.Diagnosis;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Critical
}

public sealed record DiagnosticFinding(
    string Id,
    DiagnosticSeverity Severity,
    string Title,
    string Explanation,
    string Evidence,
    string Confidence,
    string Recommendation
);

public sealed record DiagnosticReport(
    DateTimeOffset AnalyzedAt,
    string ComputerName,
    IReadOnlyList<DiagnosticFinding> Findings,
    bool PerformanceBenchmarkRequired
);

/// <summary>
/// Analiza evidencia de hardware y sistema.
/// No inventa cuellos de botella ni modifica Windows.
/// </summary>
public sealed class AdaptiveDiagnosticEngine
{
    public DiagnosticReport Analyze(SystemAuditReport audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var findings = new List<DiagnosticFinding>();

        AnalyzeMemory(audit, findings);
        AnalyzeStorage(audit, findings);
        AnalyzeProcessor(audit, findings);
        AnalyzeGraphics(audit, findings);
        AnalyzeDisplay(audit, findings);
        AnalyzeCs2(audit, findings);

        // Evidencia ampliada (ASUS ExpertCenter integrado): Dual Channel verificable,
        // CPU utilización/potencia, NVMe, SMART completo, TRIM, plan, servicios,
        // inicio y CPU instantánea de procesos.
        AnalyzeHardwareEvidence(
            HardwareEvidenceEvaluator.Evaluate(audit),
            findings);

        findings.Add(new DiagnosticFinding(
            "BENCHMARK_REQUIRED",
            DiagnosticSeverity.Information,
            "Falta evidencia de rendimiento real",
            "La auditoría CIM identifica el hardware, pero no mide FPS, 1% lows ni tiempos de fotograma.",
            "No se encontraron mediciones PresentMon asociadas a esta auditoría.",
            "HIGH",
            "Ejecutar una captura de rendimiento de CS2 con PresentMon antes de diagnosticar un cuello de botella."
        ));

        return new DiagnosticReport(
            DateTimeOffset.Now,
            audit.ComputerName,
            findings,
            PerformanceBenchmarkRequired: true
        );
    }

private static void AnalyzeMemory(SystemAuditReport audit, List<DiagnosticFinding> findings)
{
    double? totalBytes = FindNumber(audit, "Equipo", "TotalPhysicalMemory");

    if (!totalBytes.HasValue || totalBytes.Value <= 0)
        totalBytes = SumNumbers(audit, "RAM", "Capacity");

    if (totalBytes.HasValue && totalBytes.Value > 0)
    {
        double totalGb = totalBytes.Value / 1024d / 1024d / 1024d;

        findings.Add(new DiagnosticFinding(
            "MEMORY_CAPACITY",
            DiagnosticSeverity.Information,
            "Memoria RAM instalada",
            $"El sistema informa {totalGb:F1} GB de memoria RAM instalada.",
            $"Total detectado: {totalBytes.Value:N0} bytes.",
            "High",
            "No se requiere ninguna acción sobre la capacidad detectada."
        ));
    }
    else
    {
        findings.Add(new DiagnosticFinding(
            "MEMORY_UNKNOWN",
            DiagnosticSeverity.Warning,
            "Capacidad RAM desconocida",
            "No fue posible determinar la memoria RAM instalada.",
            "No hay un valor válido de TotalPhysicalMemory ni capacidades de módulos.",
            "Low",
            "Revisar la disponibilidad de los datos CIM de memoria."
        ));
    }

    var indices = GetArrayIndices(audit, "RAM").ToArray();
    bool comparisonAvailable = false;
    bool mismatchFound = false;

    foreach (int index in indices)
    {
        double? nominal = FindIndexedNumber(audit, "RAM", index, "Speed");
        double? configured = FindIndexedNumber(audit, "RAM", index, "ConfiguredClockSpeed");

        if (!nominal.HasValue || !configured.HasValue ||
            nominal.Value <= 0 || configured.Value <= 0)
            continue;

        comparisonAvailable = true;

        if (configured.Value < nominal.Value)
        {
            mismatchFound = true;

            findings.Add(new DiagnosticFinding(
                $"MEMORY_CLOCK_DIFFERENCE_{index}",
                DiagnosticSeverity.Warning,
                $"Velocidad RAM inferior a la nominal — módulo {index}",
                $"El módulo informa {nominal.Value:F0} MT/s nominales y {configured.Value:F0} MT/s configurados.",
                $"RAM[{index}]: Speed={nominal.Value:F0}; ConfiguredClockSpeed={configured.Value:F0}.",
                "High",
                "Verificar la configuración de memoria y las limitaciones de la placa. No cambiar parámetros automáticamente."
            ));
        }
    }

    if (!comparisonAvailable)
    {
        findings.Add(new DiagnosticFinding(
            "MEMORY_SPEED_UNKNOWN",
            DiagnosticSeverity.Information,
            "Velocidad RAM no verificable",
            "No se encontraron pares completos de velocidad nominal y configurada por módulo.",
            "Datos CIM incompletos o ausentes.",
            "Low",
            "No modificar la configuración basándose en datos incompletos."
        ));
    }
    else if (!mismatchFound)
    {
        findings.Add(new DiagnosticFinding(
            "MEMORY_CLOCK_OK",
            DiagnosticSeverity.Information,
            "Velocidad RAM verificada",
            "No se detectaron módulos con velocidad configurada inferior a la nominal informada.",
            "Comparación individual de los módulos con datos completos.",
            "High",
            "No se requiere ninguna acción."
        ));
    }
}

private static void AnalyzeStorage(SystemAuditReport audit, List<DiagnosticFinding> findings)
{
    var systemDisk = audit.Facts.FirstOrDefault(f =>
        f.Category == "Discos" &&
        f.Name.EndsWith("DeviceID", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(f.Value?.Trim(), "C:", StringComparison.OrdinalIgnoreCase));

    if (systemDisk is null)
    {
        findings.Add(new DiagnosticFinding(
            "SYSTEM_DISK_UNKNOWN",
            DiagnosticSeverity.Warning,
            "Disco del sistema no identificado",
            "No se pudo identificar la unidad C: en los datos de auditoría.",
            "No se encontró un DeviceID igual a C: en la categoría Discos.",
            "Low",
            "Revisar los datos CIM de las unidades. No modificar discos automáticamente."
        ));
        return;
    }

    int? index = GetArrayIndex(systemDisk.Name);

    // Admite tanto arrays [0] Size como objetos simples Size.
    var sizeFact = audit.Facts.FirstOrDefault(f =>
        f.Category == "Discos" &&
        f.Name.EndsWith("Size", StringComparison.OrdinalIgnoreCase) &&
        GetArrayIndex(f.Name) == index);

    var freeFact = audit.Facts.FirstOrDefault(f =>
        f.Category == "Discos" &&
        f.Name.EndsWith("FreeSpace", StringComparison.OrdinalIgnoreCase) &&
        GetArrayIndex(f.Name) == index);

    double? size = ParseStorageNumber(sizeFact?.Value);
    double? free = ParseStorageNumber(freeFact?.Value);

    if (!size.HasValue || !free.HasValue ||
        size.Value <= 0 || free.Value < 0 || free.Value > size.Value)
    {
        findings.Add(new DiagnosticFinding(
            "SYSTEM_DISK_SPACE_UNKNOWN",
            DiagnosticSeverity.Warning,
            "Espacio del disco C: no verificable",
            "Se identificó C:, pero faltan datos válidos de capacidad o espacio libre.",
            $"Registro: {systemDisk.Name}; capacidad: {sizeFact?.Value ?? "ausente"}; espacio libre: {freeFact?.Value ?? "ausente"}.",
            "Low",
            "Revisar los datos de almacenamiento. No realizar limpiezas automáticas."
        ));
        return;
    }

    double used = size.Value - free.Value;
    double freePercent = free.Value / size.Value * 100d;
    double totalGb = size.Value / 1024d / 1024d / 1024d;
    double freeGb = free.Value / 1024d / 1024d / 1024d;
    double usedPercent = used / size.Value * 100d;

    DiagnosticSeverity severity =
        freePercent < 10 ? DiagnosticSeverity.Critical :
        freePercent < 20 ? DiagnosticSeverity.Warning :
        DiagnosticSeverity.Information;

    string association = index.HasValue
        ? $"índice de array CIM [{index.Value}]"
        : "único registro CIM (sin índice de array)";

    findings.Add(new DiagnosticFinding(
        "SYSTEM_DISK_SPACE",
        severity,
        "Espacio disponible en C:",
        $"Unidad C: tiene {freeGb:F1} GB libres de {totalGb:F1} GB ({freePercent:F1}% libre).",
        $"Usado: {usedPercent:F1}%; libre: {freePercent:F1}%; asociación: {association}.",
        "High",
        freePercent < 10
            ? "Revisar archivos grandes y aplicaciones instaladas antes de liberar espacio."
            : freePercent < 20
                ? "Conviene vigilar el espacio disponible y revisar archivos innecesarios."
                : "El espacio libre detectado no requiere intervención."
    ));
}

private static double? ParseStorageNumber(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    return double.TryParse(
        value,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out var result)
        ? result
        : null;
}


    private static void AnalyzeProcessor(
        SystemAuditReport audit,
        List<DiagnosticFinding> findings)
    {
        var name = FindValue(audit, "CPU", "Name");
        var cores = FindValue(audit, "CPU", "NumberOfCores");
        var threads = FindValue(audit, "CPU", "NumberOfLogicalProcessors");

        if (name is null)
        {
            AddUnknown(findings, "CPU_UNKNOWN", "Procesador");
            return;
        }

        findings.Add(new DiagnosticFinding(
            "CPU_PROFILE",
            DiagnosticSeverity.Information,
            "Procesador identificado",
            "La identificación del procesador permite seleccionar futuras pruebas. No determina por sí sola el rendimiento en CS2.",
            $"{name}; núcleos: {cores ?? "desconocido"}; procesadores lógicos: {threads ?? "desconocido"}.",
            "SYSTEM_REPORTED",
            "Medir utilización por núcleo, frecuencias efectivas, temperatura y frametimes durante una partida."
        ));
    }

    private static void AnalyzeGraphics(
        SystemAuditReport audit,
        List<DiagnosticFinding> findings)
    {
        var gpus = audit.Facts
            .Where(f => f.Category == "GPU" && f.Name.Contains("Name", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (gpus.Count == 0)
        {
            AddUnknown(findings, "GPU_UNKNOWN", "Gráficos");
            return;
        }

        foreach (var gpu in gpus)
        {
            int? index = GetArrayIndex(gpu.Name);

            string? driverVersion = index.HasValue
                ? FindIndexedValue(audit, "GPU", index.Value, "DriverVersion")
                : FindValue(audit, "GPU", "DriverVersion");
            string? driverDate = index.HasValue
                ? FindIndexedValue(audit, "GPU", index.Value, "DriverDate")
                : FindValue(audit, "GPU", "DriverDate");

            string driverEvidence =
                $"DriverVersion: {driverVersion ?? "UNKNOWN"}; " +
                $"DriverDate: {driverDate ?? "UNKNOWN"}.";

            findings.Add(new DiagnosticFinding(
                "GPU_PROFILE",
                DiagnosticSeverity.Information,
                $"GPU identificada: {gpu.Value ?? "desconocida"}",
                "La auditoría identifica el adaptador gráfico y su driver instalado. " +
                "AdapterRAM no se considera VRAM dedicada fiable en gráficos integrados. " +
                "La antigüedad del driver no se evalúa sin una fuente de referencia verificada.",
                $"{gpu.Name}: {gpu.Value ?? "sin dato"}. {driverEvidence}",
                gpu.Confidence,
                "Medir carga de GPU, frecuencia, temperatura y tiempos de fotograma durante CS2 antes de decidir ajustes gráficos."
            ));

            if (driverVersion is null && driverDate is null)
            {
                findings.Add(new DiagnosticFinding(
                    $"GPU_DRIVER_UNKNOWN_{index?.ToString() ?? "X"}",
                    DiagnosticSeverity.Information,
                    "Driver de GPU no verificable",
                    "Windows no informó versión ni fecha del driver para este adaptador; " +
                    "no se puede diagnosticar su estado ni recomendar una actualización.",
                    driverEvidence,
                    "UNKNOWN",
                    "Consultar el administrador de dispositivos o el fabricante antes de cualquier cambio de driver."
                ));
            }
        }
    }

    private static void AnalyzeDisplay(
        SystemAuditReport audit,
        List<DiagnosticFinding> findings)
    {
        var refresh = FindNumber(audit, "GPU", "CurrentRefreshRate");

        if (refresh is null || refresh <= 0)
            return;

        findings.Add(new DiagnosticFinding(
            "DISPLAY_REFRESH",
            DiagnosticSeverity.Information,
            $"Frecuencia informada: {refresh.Value:F0} Hz",
            "Es el valor informado por Windows durante la auditoría; puede no representar todos los modos disponibles del monitor.",
            $"CurrentRefreshRate: {refresh.Value:F0} Hz.",
            "SYSTEM_REPORTED",
            "Contrastar con la configuración avanzada de pantalla y el modo activo del monitor."
        ));
    }

    /// <summary>
    /// Evidencia ampliada del diagnóstico ASUS ExpertCenter integrado.
    /// No inventa valores: todo dato ausente queda UNKNOWN.
    /// </summary>
    private static void AnalyzeHardwareEvidence(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        AnalyzeDualChannel(snapshot, findings);
        AnalyzeCpuPower(snapshot, findings);
        AnalyzeStorageEvidence(snapshot, findings);
        AnalyzeTrim(snapshot, findings);
        AnalyzePowerPlan(snapshot, findings);
        AnalyzeServicesAndStartup(snapshot, findings);
        AnalyzeInstantaneousProcesses(snapshot, findings);
    }

    private static void AnalyzeDualChannel(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        var memory = snapshot.Memory;

        string confidence = memory.Confidence switch
        {
            EvidenceConfidence.High => "High",
            EvidenceConfidence.Medium => "Medium",
            EvidenceConfidence.Low => "Low",
            _ => "UNKNOWN"
        };

        string title = memory.Mode switch
        {
            DualChannelMode.VerifiedDualChannel => "Dual Channel verificado",
            DualChannelMode.VerifiedSingleChannel => "Canal único verificado",
            _ => "Dual Channel NO verificable"
        };

        string explanation = memory.Mode switch
        {
            DualChannelMode.VerifiedDualChannel =>
                $"Se verificaron {memory.ChannelCount} canales distintos entre " +
                $"{memory.ModuleCount} módulos instalados. La cantidad de módulos " +
                "por sí sola no se usó como prueba.",
            DualChannelMode.VerifiedSingleChannel =>
                $"Los {memory.ModuleCount} módulos comparten un único canal informado. " +
                "No se declara Dual Channel sin evidencia de canales distintos.",
            _ =>
                "Windows no expone localizadores de canal suficientes. Dos módulos " +
                "instalados NO confirman Dual Channel: el modo queda UNKNOWN."
        };

        findings.Add(new DiagnosticFinding(
            "MEMORY_DUAL_CHANNEL",
            memory.Mode == DualChannelMode.VerifiedSingleChannel
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Information,
            title,
            explanation,
            string.Join(" | ", memory.Evidence),
            confidence,
            memory.Mode == DualChannelMode.VerifiedDualChannel
                ? "No se requiere ninguna acción."
                : memory.Mode == DualChannelMode.VerifiedSingleChannel
                    ? "Verificar en el manual de la placa si hay ranuras libres y si " +
                      "un segundo módulo compatible permitiría Dual Channel. EVO no " +
                      "declara mejoras de rendimiento sin medición."
                    : "No modificar la configuración de memoria basándose en datos " +
                      "incompletos. Si hace falta, consultar el manual de la placa."));

        foreach (string warning in memory.Warnings)
        {
            findings.Add(new DiagnosticFinding(
                "MEMORY_TOPOLOGY_LIMITATION",
                DiagnosticSeverity.Information,
                "Limitación de la topología de memoria",
                warning,
                $"Módulos: {memory.ModuleCount}; ranuras informadas: " +
                $"{memory.SlotCount?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN"}.",
                confidence,
                "No cambiar la configuración automáticamente."));
        }
    }

    private static void AnalyzeCpuPower(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        var cpu = snapshot.Cpu;

        findings.Add(new DiagnosticFinding(
            "CPU_POWER_EVIDENCE",
            DiagnosticSeverity.Information,
            "CPU: utilización medida y potencia UNKNOWN",
            cpu.InstantaneousUtilizationPercent.HasValue
                ? $"Windows informa {cpu.InstantaneousUtilizationPercent.Value}% de " +
                  "utilización instantánea. Ese valor no es consumo eléctrico."
                : "No se informó utilización instantánea de CPU.",
            $"{cpu.PowerExplanation} " +
            $"Frecuencia actual: {cpu.CurrentClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN"} MHz; " +
            $"máxima: {cpu.MaxClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN"} MHz.",
            cpu.InstantaneousUtilizationPercent.HasValue ? "SYSTEM_REPORTED" : "UNKNOWN",
            "No interpretar la CPU acumulada de un proceso como consumo instantáneo. " +
            "Para potencia real se requiere un contador de energía (RAPL/PDH)."));
    }

    private static void AnalyzeStorageEvidence(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        if (snapshot.Storage.Count == 0)
        {
            findings.Add(new DiagnosticFinding(
                "STORAGE_HEALTH_UNKNOWN",
                DiagnosticSeverity.Warning,
                "Salud del almacenamiento UNKNOWN",
                "No se pudo obtener información de discos físicos, SMART ni NVMe.",
                "Get-PhysicalDisk/MSStorageDriver no devolvieron datos utilizables.",
                "UNKNOWN",
                "Revisar permisos de la sesión. EVO no inventa estados de salud."));
            return;
        }

        foreach (var disk in snapshot.Storage)
        {
            string name = disk.Name ?? "Disco sin nombre";

            string health = disk.HealthStatus ?? "UNKNOWN";
            string nvme = disk.IsNvme switch
            {
                true => "NVMe verificado",
                false => "NVMe no detectado (bus " + (disk.BusType ?? "UNKNOWN") + ")",
                null => "NVMe UNKNOWN (sin datos de bus)"
            };

            string counters =
                $"Wear: {FormatNumber(disk.WearPercent)}; " +
                $"Temperatura: {FormatNumber(disk.TemperatureCelsius)} °C; " +
                $"PowerOnHours: {FormatNumber(disk.PowerOnHours)}; " +
                $"errores lectura/escritura: {FormatNumber(disk.ReadErrorsTotal)}/" +
                $"{FormatNumber(disk.WriteErrorsTotal)}.";

            bool incomplete =
                disk.SmartCoverage is SmartCoverage.HealthStatusOnly or
                    SmartCoverage.Unavailable;

            findings.Add(new DiagnosticFinding(
                $"STORAGE_HEALTH_{SanitizeId(name)}",
                disk.SmartCoverage == SmartCoverage.Unavailable
                    ? DiagnosticSeverity.Warning
                    : disk.HealthStatus is not null &&
                      !disk.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                        ? DiagnosticSeverity.Warning
                        : DiagnosticSeverity.Information,
                $"Almacenamiento: {name}",
                $"{nvme}. HealthStatus: {health}. " +
                (incomplete
                    ? "HealthStatus NO equivale a SMART completo: faltan contadores " +
                      "de confiabilidad y los valores quedan UNKNOWN."
                    : $"SMART verificado mediante {disk.SmartCoverageText}."),
                counters + $" Cobertura SMART: {disk.SmartCoverageText}.",
                disk.SmartCoverage == SmartCoverage.ReliabilityCounters
                    ? "High"
                    : disk.SmartCoverage == SmartCoverage.FailurePrediction
                        ? "Medium"
                        : disk.SmartCoverage == SmartCoverage.HealthStatusOnly
                            ? "Low"
                            : "UNKNOWN",
                incomplete
                    ? "No asumir que el disco está sano ni que está fallando. " +
                      "Para SMART completo se requieren permisos de administrador."
                    : "Vigilar la temperatura y el desgaste informados."));
        }
    }

    private static void AnalyzeTrim(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        string confidence = snapshot.Trim.Confidence == EvidenceConfidence.High
            ? "High"
            : "UNKNOWN";

        findings.Add(new DiagnosticFinding(
            "STORAGE_TRIM",
            snapshot.Trim.Enabled == false
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Information,
            snapshot.Trim.Enabled switch
            {
                true => "TRIM activo",
                false => "TRIM desactivado",
                _ => "TRIM UNKNOWN"
            },
            snapshot.Trim.Enabled switch
            {
                true => "Windows informa TRIM habilitado para el sistema de archivos.",
                false => "Windows informa TRIM deshabilitado. En SSD/NVMe esto " +
                         "puede afectar el rendimiento sostenido.",
                _ => "No se pudo leer DisableDeleteNotify; el estado queda UNKNOWN."
            },
            string.Join(" | ", snapshot.Trim.Entries),
            confidence,
            snapshot.Trim.Enabled == false
                ? "Revisar la configuración de TRIM antes de cambiarla; no se " +
                  "modifica automáticamente."
                : "No se requiere ninguna acción."));
    }

    private static void AnalyzePowerPlan(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        var plan = snapshot.PowerPlan;

        findings.Add(new DiagnosticFinding(
            "POWER_PLAN_ACTIVE",
            DiagnosticSeverity.Information,
            "Plan de energía activo",
            plan.Name is null && plan.Guid is null
                ? "No se pudo leer el plan de energía activo."
                : $"Plan activo: {plan.Name ?? "UNKNOWN"} ({plan.Guid ?? "GUID UNKNOWN"}).",
            plan.RawText ?? "powercfg no devolvió datos.",
            plan.Confidence == EvidenceConfidence.High ? "High" : "UNKNOWN",
            "Un plan Equilibrado puede retrasar el escalado de frecuencias. " +
            "El cambio, si corresponde, se realiza desde el catálogo con " +
            "autorización y medición."));
    }

    private static void AnalyzeServicesAndStartup(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        findings.Add(new DiagnosticFinding(
            "SERVICES_AUTO_STATUS",
            DiagnosticSeverity.Information,
            "Servicios de inicio automático",
            $"{snapshot.Services.AutoStartCount} servicios en inicio automático; " +
            $"{snapshot.Services.StoppedAutoStartCount} detenidos.",
            snapshot.Services.StoppedServiceNames.Count == 0
                ? "Sin servicios automáticos detenidos."
                : "Detenidos: " + string.Join(", ", snapshot.Services.StoppedServiceNames),
            "SYSTEM_REPORTED",
            "EVO no desactiva servicios automáticamente. Revisar cada caso con " +
            "documentación del fabricante antes de cambiarlo."));

        int serviceCount = snapshot.Services.Inventory?.Count ?? 0;
        int runningServiceCount = snapshot.Services.Inventory?.Count(service =>
            string.Equals(service.State, "Running", StringComparison.OrdinalIgnoreCase)) ?? 0;

        findings.Add(new DiagnosticFinding(
            "SERVICES_INVENTORY",
            DiagnosticSeverity.Information,
            "Inventario de servicios",
            serviceCount == 0
                ? "No se pudo obtener el inventario de servicios."
                : $"{serviceCount} servicios consultados; {runningServiceCount} en ejecución.",
            serviceCount == 0
                ? "La consulta CIM no devolvió servicios."
                : "El estado y el modo de inicio son evidencia de inventario, no una prueba de que un servicio sea innecesario.",
            "SYSTEM_REPORTED",
            "No se recomienda ni aplica ningún cambio de servicio sin una regla de compatibilidad específica y evidencia del uso del equipo."));

        findings.Add(new DiagnosticFinding(
            "STARTUP_APPS_SNAPSHOT",
            DiagnosticSeverity.Information,
            "Aplicaciones de inicio",
            snapshot.Startup.Names.Count == 0
                ? "No se informaron aplicaciones de inicio."
                : $"{snapshot.Startup.Names.Count} aplicaciones de inicio informadas.",
            snapshot.Startup.Names.Count == 0
                ? "Win32_StartupCommand no devolvió entradas."
                : string.Join(", ", snapshot.Startup.Names.Take(10)),
            "SYSTEM_REPORTED",
            "Las desactivaciones se realizan individualmente desde el catálogo, " +
            "con autorización, diario de cambios y rollback."));
    }

    private static void AnalyzeInstantaneousProcesses(
        HardwareDiagnosticSnapshot snapshot,
        List<DiagnosticFinding> findings)
    {
        var processes = snapshot.Processes;

        string evidence = processes.TopProcesses.Count == 0
            ? "Sin muestras emparejadas."
            : string.Join("; ", processes.TopProcesses
                .Take(5)
                .Select(p =>
                    $"{p.Name} (PID {p.ProcessId}): " +
                    $"{p.InstantaneousCpuPercent:F1}% CPU; " +
                    $"{p.WorkingSetMb:F0} MB"));

        findings.Add(new DiagnosticFinding(
            "PROCESS_INSTANTANEOUS_CPU",
            DiagnosticSeverity.Information,
            processes.HasSufficientEvidence
                ? "CPU instantánea por proceso calculada"
                : "CPU instantánea por proceso UNKNOWN",
            processes.HasSufficientEvidence
                ? "La carga se calculó como delta de TotalProcessorTime entre dos " +
                  "muestras. No se usa la CPU acumulada como consumo instantáneo."
                : "No hubo dos muestras emparejadas suficientes para calcular la carga.",
            evidence,
            processes.HasSufficientEvidence ? "Medium" : "UNKNOWN",
            "Usar estos valores como orientación; confirmar con el monitor en vivo " +
            "antes de tomar decisiones."));
    }

    private static string FormatNumber(double? value) =>
        value.HasValue
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
            : "UNKNOWN";

    private static string FormatNumber(long? value) =>
        value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : "UNKNOWN";

    private static string SanitizeId(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value.ToUpperInvariant())
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');

        return builder.ToString();
    }

    private static void AnalyzeCs2(
        SystemAuditReport audit,
        List<DiagnosticFinding> findings)
    {
        var process = audit.Facts.FirstOrDefault(f =>
            f.Category == "CS2" &&
            f.Name.Contains("ProcessName", StringComparison.OrdinalIgnoreCase));

        findings.Add(new DiagnosticFinding(
            "CS2_PROCESS_STATE",
            DiagnosticSeverity.Information,
            process is null
                ? "CS2 no estaba detectado durante la auditoría"
                : "CS2 estaba ejecutándose durante la auditoría",
            "La presencia del proceso no demuestra rendimiento ni estabilidad.",
            process?.Value ?? "No se detectó el proceso cs2 en esa captura.",
            "SYSTEM_REPORTED",
            "Realizar una captura específica mientras se ejecuta una partida o benchmark."
        ));
    }

    private static string? FindValue(
        SystemAuditReport audit,
        string category,
        string name)
    {
        return audit.Facts.FirstOrDefault(f =>
            f.Category == category &&
            f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static double? FindNumber(
        SystemAuditReport audit,
        string category,
        string name)
    {
        var values = audit.Facts
            .Where(f => f.Category == category &&
                        f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(f => ParseNumber(f.Value))
            .Where(v => v.HasValue)
            .ToList();

        return values.FirstOrDefault();
    }

    private static double? SumNumbers(
        SystemAuditReport audit,
        string category,
        string name)
    {
        var values = audit.Facts
            .Where(f => f.Category == category &&
                        f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(f => ParseNumber(f.Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        return values.Count == 0 ? null : values.Sum();
    }

private static IEnumerable<int> GetArrayIndices(
    SystemAuditReport audit,
    string category)
{
    return audit.Facts
        .Where(f => f.Category == category)
        .Select(f => GetArrayIndex(f.Name))
        .Where(i => i.HasValue)
        .Select(i => i.GetValueOrDefault())
        .Distinct()
        .OrderBy(i => i);
}

private static int? GetArrayIndex(string name)
{
    if (string.IsNullOrEmpty(name) || name[0] != '[')
        return null;

    int end = name.IndexOf(']');

    if (end < 2)
        return null;

    return int.TryParse(name.Substring(1, end - 1), out int index)
        ? index
        : null;
}

private static string? FindIndexedValue(
    SystemAuditReport audit,
    string category,
    int index,
    string property)
{
    string name = $"[{index}] {property}";

    return audit.Facts.FirstOrDefault(f =>
        f.Category == category &&
        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

private static double? FindIndexedNumber(
    SystemAuditReport audit,
    string category,
    int index,
    string property)
{
    string name = $"[{index}] {property}";

    string? value = audit.Facts.FirstOrDefault(f =>
        f.Category == category &&
        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    return ParseNumber(value);
}

    private static double? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static void AddUnknown(
        List<DiagnosticFinding> findings,
        string id,
        string component)
    {
        findings.Add(new DiagnosticFinding(
            id,
            DiagnosticSeverity.Information,
            $"{component}: evidencia insuficiente",
            "No hay datos suficientes para formular un diagnóstico fiable.",
            "Dato ausente en la auditoría.",
            "UNKNOWN",
            "Obtener una medición válida antes de recomendar cambios."
        ));
    }
}
