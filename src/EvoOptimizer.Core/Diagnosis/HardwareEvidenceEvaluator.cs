using System.Globalization;
using System.Text.RegularExpressions;
using EvoOptimizer.Core.Audit;

namespace EvoOptimizer.Core.Diagnosis;

/// <summary>Confiabilidad de una conclusión. Unknown significa que no se pudo verificar.</summary>
public enum EvidenceConfidence
{
    Unknown,
    Low,
    Medium,
    High
}

/// <summary>
/// Resultado de la verificación de canales de memoria.
/// Dos módulos instalados NO confirman Dual Channel por sí solos: se exige
/// evidencia de canales distintos en DeviceLocator/BankLabel.
/// </summary>
public enum DualChannelMode
{
    Unknown,
    VerifiedSingleChannel,
    VerifiedDualChannel
}

public sealed record MemoryModuleEvidence(
    int Index,
    string? DeviceLocator,
    string? BankLabel,
    string? Manufacturer,
    string? PartNumber,
    ulong? CapacityBytes,
    uint? NominalMtps,
    uint? ConfiguredMtps);

public sealed record DualChannelAssessment(
    DualChannelMode Mode,
    EvidenceConfidence Confidence,
    int ModuleCount,
    int? SlotCount,
    int? ChannelCount,
    IReadOnlyList<MemoryModuleEvidence> Modules,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings)
{
    public string ModeText => Mode switch
    {
        DualChannelMode.VerifiedDualChannel => "DUAL CHANNEL VERIFICADO",
        DualChannelMode.VerifiedSingleChannel => "CANAL ÚNICO VERIFICADO",
        _ => "UNKNOWN (no verificable)"
    };
}

/// <summary>
/// Evidencia de CPU. <see cref="InstantaneousUtilizationPercent"/> es utilización
/// informada por Windows; <see cref="InstantaneousPowerWatts"/> sólo puede tener
/// valor con una fuente real de potencia (RAPL/PDH). La CPU acumulada de un
/// proceso no es consumo instantáneo.
/// </summary>
public sealed record ProcessorEvidence(
    string? Name,
    int? PhysicalCores,
    int? LogicalProcessors,
    uint? MaxClockMhz,
    uint? CurrentClockMhz,
    uint? InstantaneousUtilizationPercent,
    double? InstantaneousPowerWatts,
    EvidenceConfidence PowerConfidence,
    string PowerExplanation);

/// <summary>
/// Evidencia de GPU y driver tal como la informa Windows. AdapterRAM no se
/// expone como VRAM dedicada: en gráficos integrados es memoria compartida y
/// no constituye una medición fiable de VRAM.
/// </summary>
public sealed record GraphicsDeviceEvidence(
    int Index,
    string? Name,
    string? AdapterCompatibility,
    string? DriverVersion,
    string? DriverDate,
    string? VideoProcessor,
    string? PnpDeviceId,
    string? Status,
    uint? HorizontalResolution,
    uint? VerticalResolution,
    uint? RefreshRateHz)
{
    public bool HasDriverEvidence =>
        !string.IsNullOrWhiteSpace(DriverVersion) ||
        !string.IsNullOrWhiteSpace(DriverDate);
}

public enum SmartCoverage
{
    Unavailable,
    HealthStatusOnly,
    FailurePrediction,
    ReliabilityCounters
}

public sealed record StorageDeviceEvidence(
    string? Name,
    string? MediaType,
    string? BusType,
    bool? IsNvme,
    string? HealthStatus,
    string? OperationalStatus,
    ulong? SizeBytes,
    double? WearPercent,
    double? TemperatureCelsius,
    double? PowerOnHours,
    long? ReadErrorsTotal,
    long? WriteErrorsTotal,
    bool? PredictFailure,
    SmartCoverage SmartCoverage)
{
    public string SmartCoverageText => SmartCoverage switch
    {
        SmartCoverage.ReliabilityCounters => "SMART completo (contadores de confiabilidad)",
        SmartCoverage.FailurePrediction => "SMART predictivo (FailurePredictStatus)",
        SmartCoverage.HealthStatusOnly => "SOLO HealthStatus (NO equivale a SMART completo)",
        _ => "UNKNOWN"
    };
}

public sealed record TrimAssessment(
    bool? Enabled,
    IReadOnlyList<string> Entries,
    EvidenceConfidence Confidence);

public sealed record ProcessLoadSample(
    int ProcessId,
    string Name,
    double InstantaneousCpuPercent,
    double WorkingSetMb);

public sealed record ProcessLoadAssessment(
    IReadOnlyList<ProcessLoadSample> TopProcesses,
    bool HasSufficientEvidence,
    double? IntervalSeconds,
    IReadOnlyList<string> Findings);

public sealed record PowerPlanEvidence(
    string? Guid,
    string? Name,
    string? RawText,
    EvidenceConfidence Confidence);

public sealed record ServiceEvidence(
    int AutoStartCount,
    int StoppedAutoStartCount,
    IReadOnlyList<string> StoppedServiceNames,
    IReadOnlyList<WindowsServiceEvidence>? Inventory = null);

public sealed record WindowsServiceEvidence(
    string Name,
    string DisplayName,
    string State,
    string StartMode,
    int? ProcessId);

public sealed record StartupEntryEvidence(
    IReadOnlyList<string> Names);

/// <summary>
/// Instantánea de diagnóstico de hardware derivada exclusivamente de la auditoría
/// CIM de solo lectura. Todo dato ausente queda en null/UNKNOWN; nunca se infiere
/// un valor que Windows no haya informado.
/// </summary>
public sealed record HardwareDiagnosticSnapshot(
    DateTimeOffset MeasuredAt,
    string ComputerName,
    DualChannelAssessment Memory,
    ProcessorEvidence Cpu,
    IReadOnlyList<GraphicsDeviceEvidence> Graphics,
    IReadOnlyList<StorageDeviceEvidence> Storage,
    TrimAssessment Trim,
    PowerPlanEvidence PowerPlan,
    ServiceEvidence Services,
    StartupEntryEvidence Startup,
    ProcessLoadAssessment Processes);

/// <summary>
/// Interpreta la auditoría existente (WindowsSystemAuditor) y produce evidencia
/// verificable para Hardware, Memory, Storage y Process Intelligence.
/// Es puro: no ejecuta comandos ni modifica Windows.
/// </summary>
public static partial class HardwareEvidenceEvaluator
{
    public static HardwareDiagnosticSnapshot Evaluate(SystemAuditReport audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        return new HardwareDiagnosticSnapshot(
            MeasuredAt: audit.CapturedAt,
            ComputerName: audit.ComputerName,
            Memory: AssessMemory(audit),
            Cpu: AssessProcessor(audit),
            Graphics: AssessGraphics(audit),
            Storage: AssessStorage(audit),
            Trim: AssessTrim(audit),
            PowerPlan: AssessPowerPlan(audit),
            Services: AssessServices(audit),
            Startup: AssessStartup(audit),
            Processes: AssessProcesses(audit));
    }

    // ── Memoria: Dual Channel verificable ────────────────────────────────────

    public static DualChannelAssessment AssessMemory(SystemAuditReport audit)
    {
        var modules = ReadMemoryModules(audit);
        int? slotCount = ToInt(Value(audit, "Arreglo de memoria", "MemoryDevices"));

        var evidence = new List<string>();
        var warnings = new List<string>();

        if (modules.Count == 0)
        {
            return new DualChannelAssessment(
                DualChannelMode.Unknown,
                EvidenceConfidence.Low,
                0,
                slotCount,
                null,
                modules,
                new[] { "No se informaron módulos de memoria." },
                new[] { "Sin módulos CIM válidos no se puede verificar el modo de canal." });
        }

        foreach (var module in modules)
        {
            evidence.Add(
                $"Módulo {module.Index}: DeviceLocator={module.DeviceLocator ?? "UNKNOWN"}; " +
                $"BankLabel={module.BankLabel ?? "UNKNOWN"}; " +
                $"capacidad={FormatBytes(module.CapacityBytes)}; " +
                $"nominal={FormatMtps(module.NominalMtps)}; " +
                $"configurada={FormatMtps(module.ConfiguredMtps)}.");
        }

        var capacities = modules
            .Where(m => m.CapacityBytes is > 0)
            .Select(m => m.CapacityBytes!.Value)
            .Distinct()
            .ToArray();

        var configuredSpeeds = modules
            .Where(m => m.ConfiguredMtps is > 0)
            .Select(m => m.ConfiguredMtps!.Value)
            .Distinct()
            .ToArray();

        if (capacities.Length > 1)
        {
            warnings.Add(
                "Los módulos tienen capacidades distintas: el sistema puede operar " +
                "en modo flexible (flex mode). No se garantiza Dual Channel simétrico.");
        }

        if (configuredSpeeds.Length > 1)
        {
            warnings.Add(
                "Los módulos informan velocidades configuradas distintas: " +
                "la operación conjunta no es simétrica.");
        }

        if (modules.Count == 1)
        {
            evidence.Add("Sólo hay un módulo instalado: físicamente no puede haber Dual Channel.");
            return new DualChannelAssessment(
                DualChannelMode.VerifiedSingleChannel,
                EvidenceConfidence.High,
                1,
                slotCount,
                1,
                modules,
                evidence,
                warnings);
        }

        var channels = modules
            .Select(m => InferChannelToken(m.DeviceLocator))
            .ToArray();

        if (channels.Any(c => c is null))
        {
            warnings.Add(
                "La cantidad de módulos no confirma el modo de canal. " +
                "DeviceLocator/BankLabel no exponen un canal identificable, " +
                "por lo que Dual Channel queda UNKNOWN.");
            return new DualChannelAssessment(
                DualChannelMode.Unknown,
                EvidenceConfidence.Low,
                modules.Count,
                slotCount,
                null,
                modules,
                evidence,
                warnings);
        }

        int distinctChannels = channels
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var result = distinctChannels >= 2
            ? DualChannelMode.VerifiedDualChannel
            : DualChannelMode.VerifiedSingleChannel;

        evidence.Add(
            $"Canales detectados en los localizadores: {distinctChannels} " +
            $"({string.Join(", ", channels)}).");

        if (result == DualChannelMode.VerifiedSingleChannel && modules.Count >= 2)
        {
            warnings.Add(
                "Hay varios módulos pero todos comparten el mismo canal informado: " +
                "no se verificó Dual Channel.");
        }

        return new DualChannelAssessment(
            result,
            EvidenceConfidence.High,
            modules.Count,
            slotCount,
            distinctChannels,
            modules,
            evidence,
            warnings);
    }

    private static IReadOnlyList<MemoryModuleEvidence> ReadMemoryModules(
        SystemAuditReport audit)
    {
        return Indices(audit, "RAM")
            .Select(index => new MemoryModuleEvidence(
                index,
                Indexed(audit, "RAM", index, "DeviceLocator"),
                Indexed(audit, "RAM", index, "BankLabel"),
                Indexed(audit, "RAM", index, "Manufacturer"),
                Indexed(audit, "RAM", index, "PartNumber"),
                ToULong(Indexed(audit, "RAM", index, "Capacity")),
                ToUInt(Indexed(audit, "RAM", index, "Speed")),
                ToUInt(Indexed(audit, "RAM", index, "ConfiguredClockSpeed"))))
            .Where(module =>
                module.CapacityBytes is > 0 ||
                module.DeviceLocator is not null ||
                module.NominalMtps is not null ||
                module.ConfiguredMtps is not null)
            .ToArray();
    }

    /// <summary>
    /// Extrae un token de canal sólo cuando el localizador lo declara de forma
    /// explícita ("ChannelA-DIMM0", "DIMM_A1"). "DIMM 0"/"BANK 0" se consideran
    /// ambiguos y devuelven null a propósito: no se inventa un canal.
    /// </summary>
    private static string? InferChannelToken(string? deviceLocator)
    {
        if (string.IsNullOrWhiteSpace(deviceLocator))
            return null;

        string text = deviceLocator.Trim().ToUpperInvariant();

        var channel = ChannelPattern().Match(text);
        if (channel.Success)
            return "CHANNEL:" + channel.Groups[1].Value;

        var dimm = DimmLetterPattern().Match(text);
        if (dimm.Success)
            return "DIMM:" + dimm.Groups[1].Value;

        return null;
    }

    // ── CPU: utilización vs potencia ─────────────────────────────────────────

    public static ProcessorEvidence AssessProcessor(SystemAuditReport audit)
    {
        uint? utilization = ToUInt(Value(audit, "CPU", "LoadPercentage"));

        const string explanation =
            "Win32_Processor.LoadPercentage es una utilización instantánea informada " +
            "por Windows, no consumo eléctrico. Process.TotalProcessorTime es CPU " +
            "acumulada desde el inicio del proceso y tampoco equivale a consumo " +
            "instantáneo. Sin un contador de energía (RAPL/PDH) la potencia en watts " +
            "permanece UNKNOWN.";

        return new ProcessorEvidence(
            Value(audit, "CPU", "Name"),
            ToInt(Value(audit, "CPU", "NumberOfCores")),
            ToInt(Value(audit, "CPU", "NumberOfLogicalProcessors")),
            ToUInt(Value(audit, "CPU", "MaxClockSpeed")),
            ToUInt(Value(audit, "CPU", "CurrentClockSpeed")),
            utilization,
            InstantaneousPowerWatts: null,
            PowerConfidence: EvidenceConfidence.Unknown,
            PowerExplanation: explanation);
    }

    // ── GPU y driver: sólo lo que Windows informa ─────────────────────────────

    /// <summary>
    /// Lee los adaptadores gráficos de la auditoría existente. No consulta nada
    /// nuevo ni inventa versión/fecha de driver: si CIM no las informa, quedan
    /// null y el consumidor las muestra como UNKNOWN.
    /// </summary>
    public static IReadOnlyList<GraphicsDeviceEvidence> AssessGraphics(
        SystemAuditReport audit)
    {
        return Indices(audit, "GPU")
            .Select(index => new GraphicsDeviceEvidence(
                index,
                Indexed(audit, "GPU", index, "Name"),
                Indexed(audit, "GPU", index, "AdapterCompatibility"),
                Indexed(audit, "GPU", index, "DriverVersion"),
                Indexed(audit, "GPU", index, "DriverDate"),
                Indexed(audit, "GPU", index, "VideoProcessor"),
                Indexed(audit, "GPU", index, "PNPDeviceID"),
                Indexed(audit, "GPU", index, "Status"),
                ToUInt(Indexed(audit, "GPU", index, "CurrentHorizontalResolution")),
                ToUInt(Indexed(audit, "GPU", index, "CurrentVerticalResolution")),
                ToUInt(Indexed(audit, "GPU", index, "CurrentRefreshRate"))))
            .Where(device =>
                device.Name is not null ||
                device.DriverVersion is not null ||
                device.PnpDeviceId is not null)
            .ToArray();
    }

    // ── Almacenamiento: NVMe, SMART completo y HealthStatus ─────────────────

    public static IReadOnlyList<StorageDeviceEvidence> AssessStorage(
        SystemAuditReport audit)
    {
        var reliability = ReadReliabilityCounters(audit);
        var prediction = ReadPredictiveSmart(audit);
        var disks = ReadPhysicalDisks(audit);

        var results = new List<StorageDeviceEvidence>();

        foreach (var disk in disks)
        {
            reliability.TryGetValue(disk.Key, out var counters);

            bool? predictFailure =
                prediction.TryGetValue(disk.Key, out var predicted)
                    ? predicted
                    : null;

            SmartCoverage coverage =
                counters is not null && counters.HasAnyValue
                    ? SmartCoverage.ReliabilityCounters
                    : predictFailure.HasValue
                        ? SmartCoverage.FailurePrediction
                        : string.IsNullOrWhiteSpace(disk.HealthStatus)
                            ? SmartCoverage.Unavailable
                            : SmartCoverage.HealthStatusOnly;

            results.Add(new StorageDeviceEvidence(
                disk.Name,
                disk.MediaType,
                disk.BusType,
                disk.IsNvme,
                disk.HealthStatus,
                disk.OperationalStatus,
                disk.SizeBytes,
                counters?.Wear,
                counters?.Temperature,
                counters?.PowerOnHours,
                counters?.ReadErrorsTotal,
                counters?.WriteErrorsTotal,
                predictFailure,
                coverage));
        }

        if (results.Count == 0)
        {
            return Array.Empty<StorageDeviceEvidence>();
        }

        return results;
    }

    private sealed record RawDisk(
        int Key,
        string? Name,
        string? MediaType,
        string? BusType,
        bool? IsNvme,
        string? HealthStatus,
        string? OperationalStatus,
        ulong? SizeBytes);

    private sealed record RawReliability(
        double? Wear,
        double? Temperature,
        double? PowerOnHours,
        long? ReadErrorsTotal,
        long? WriteErrorsTotal)
    {
        public bool HasAnyValue =>
            Wear.HasValue || Temperature.HasValue || PowerOnHours.HasValue ||
            ReadErrorsTotal.HasValue || WriteErrorsTotal.HasValue;
    }

    private static IReadOnlyList<RawDisk> ReadPhysicalDisks(SystemAuditReport audit)
    {
        var storageIndices = Indices(audit, "Almacenamiento").ToArray();

        if (storageIndices.Length > 0)
        {
            return storageIndices
                .Select(index =>
                {
                    string? busType = Indexed(audit, "Almacenamiento", index, "BusType");

                    bool? isNvme = busType is null
                        ? null
                        : busType.Equals("NVMe", StringComparison.OrdinalIgnoreCase);

                    int key = ToInt(Indexed(audit, "Almacenamiento", index, "DeviceId"))
                        ?? index;

                    return new RawDisk(
                        key,
                        Indexed(audit, "Almacenamiento", index, "FriendlyName"),
                        Indexed(audit, "Almacenamiento", index, "MediaType"),
                        busType,
                        isNvme,
                        Indexed(audit, "Almacenamiento", index, "HealthStatus"),
                        Indexed(audit, "Almacenamiento", index, "OperationalStatus"),
                        ToULong(Indexed(audit, "Almacenamiento", index, "Size")));
                })
                .ToArray();
        }

        // Respaldo: Win32_DiskDrive cuando el módulo Storage no está disponible.
        return Indices(audit, "Disco físico")
            .Select(index =>
            {
                string? interfaceType = Indexed(audit, "Disco físico", index, "InterfaceType");
                string? model = Indexed(audit, "Disco físico", index, "Model");
                string? pnp = Indexed(audit, "Disco físico", index, "PNPDeviceID");

                bool? isNvme = DetectNvme(interfaceType, model, pnp);

                return new RawDisk(
                    index,
                    model,
                    Indexed(audit, "Disco físico", index, "MediaType"),
                    interfaceType,
                    isNvme,
                    Indexed(audit, "Disco físico", index, "Status"),
                    null,
                    ToULong(Indexed(audit, "Disco físico", index, "Size")));
            })
            .ToArray();
    }

    private static bool? DetectNvme(
        string? interfaceType,
        string? model,
        string? pnpDeviceId)
    {
        if (interfaceType is not null &&
            interfaceType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return true;

        if (model is not null &&
            model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return true;

        if (pnpDeviceId is not null &&
            pnpDeviceId.Contains("NVME", StringComparison.OrdinalIgnoreCase))
            return true;

        if (interfaceType is null && model is null && pnpDeviceId is null)
            return null;

        // Hay datos de bus pero ninguno menciona NVMe: SATA/SCSI/RAID.
        return false;
    }

    private static Dictionary<int, RawReliability> ReadReliabilityCounters(
        SystemAuditReport audit)
    {
        var counters = new Dictionary<int, RawReliability>();

        foreach (int index in Indices(audit, "Contadores de confiabilidad"))
        {
            int? deviceId = ToInt(
                Indexed(audit, "Contadores de confiabilidad", index, "DeviceId"));

            if (!deviceId.HasValue)
                continue;

            counters[deviceId.Value] = new RawReliability(
                ToDouble(Indexed(audit, "Contadores de confiabilidad", index, "Wear")),
                ToDouble(Indexed(audit, "Contadores de confiabilidad", index, "Temperature")),
                ToDouble(Indexed(audit, "Contadores de confiabilidad", index, "PowerOnHours")),
                ToLong(Indexed(audit, "Contadores de confiabilidad", index, "ReadErrorsTotal")),
                ToLong(Indexed(audit, "Contadores de confiabilidad", index, "WriteErrorsTotal")));
        }

        return counters;
    }

    private static Dictionary<int, bool> ReadPredictiveSmart(
        SystemAuditReport audit)
    {
        var prediction = new Dictionary<int, bool>();

        foreach (int index in Indices(audit, "SMART predictivo"))
        {
            bool? predictFailure = ToBool(
                Indexed(audit, "SMART predictivo", index, "PredictFailure"));

            if (!predictFailure.HasValue)
                continue;

            string? instanceName =
                Indexed(audit, "SMART predictivo", index, "InstanceName");

            // MSStorageDriver InstanceName usa \\.\PHYSICALDRIVE<n>
            int? driveIndex = ParsePhysicalDriveIndex(instanceName);

            if (driveIndex.HasValue)
                prediction[driveIndex.Value] = predictFailure.Value;
        }

        return prediction;
    }

    private static int? ParsePhysicalDriveIndex(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return null;

        var match = PhysicalDrivePattern().Match(instanceName);

        return match.Success && int.TryParse(match.Groups[1].Value, out int index)
            ? index
            : null;
    }

    // ── TRIM ─────────────────────────────────────────────────────────────────

    public static TrimAssessment AssessTrim(SystemAuditReport audit)
    {
        string? raw = Value(audit, "TRIM", "Output");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TrimAssessment(
                null,
                Array.Empty<string>(),
                EvidenceConfidence.Unknown);
        }

        var entries = new List<string>();
        bool? ntfsEnabled = null;
        bool? anyEnabled = null;

        foreach (Match match in TrimPattern().Matches(raw))
        {
            string fileSystem = match.Groups[1].Value;
            bool enabled = match.Groups[2].Value == "0";
            entries.Add($"{fileSystem}: {(enabled ? "TRIM activo" : "TRIM desactivado")}");

            anyEnabled ??= enabled;

            if (fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                ntfsEnabled = enabled;
        }

        if (entries.Count == 0)
        {
            return new TrimAssessment(
                null,
                Array.Empty<string>(),
                EvidenceConfidence.Unknown);
        }

        return new TrimAssessment(
            ntfsEnabled ?? anyEnabled,
            entries,
            EvidenceConfidence.High);
    }

    // ── Plan de energía ──────────────────────────────────────────────────────

    public static PowerPlanEvidence AssessPowerPlan(SystemAuditReport audit)
    {
        string? raw = Value(audit, "Plan de energía", "Output");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PowerPlanEvidence(
                null,
                null,
                null,
                EvidenceConfidence.Unknown);
        }

        var guidMatch = GuidPattern().Match(raw);
        string? guid = guidMatch.Success ? guidMatch.Value : null;

        string? name = null;
        var nameMatch = PowerPlanNamePattern().Match(raw);

        if (nameMatch.Success)
            name = nameMatch.Groups[1].Value.Trim();

        return new PowerPlanEvidence(
            guid,
            name,
            raw.Trim(),
            guid is null && name is null
                ? EvidenceConfidence.Low
                : EvidenceConfidence.High);
    }

    // ── Servicios y aplicaciones de inicio ───────────────────────────────────

    public static ServiceEvidence AssessServices(SystemAuditReport audit)
    {
        int total = 0;
        int stopped = 0;
        var stoppedNames = new List<string>();
        var inventory = new List<WindowsServiceEvidence>();

        foreach (int index in Indices(audit, "Servicios"))
        {
            string? name = Indexed(audit, "Servicios", index, "Name");
            string? displayName = Indexed(audit, "Servicios", index, "DisplayName");
            string? startMode = Indexed(audit, "Servicios", index, "StartMode");
            string? state = Indexed(audit, "Servicios", index, "State");
            string? processId = Indexed(audit, "Servicios", index, "ProcessId");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            inventory.Add(new WindowsServiceEvidence(
                name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                state ?? "UNKNOWN",
                startMode ?? "UNKNOWN",
                ToInt(processId)));

            if (!string.Equals(startMode, "Auto", StringComparison.OrdinalIgnoreCase))
                continue;

            total++;

            if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                stopped++;

                if (stoppedNames.Count < 8)
                {
                    stoppedNames.Add(
                        Indexed(audit, "Servicios", index, "DisplayName") ??
                        Indexed(audit, "Servicios", index, "Name") ??
                        $"Servicio [{index}]");
                }
            }
        }

        return new ServiceEvidence(total, stopped, stoppedNames, inventory);
    }

    public static StartupEntryEvidence AssessStartup(SystemAuditReport audit)
    {
        var names = new List<string>();

        foreach (int index in Indices(audit, "Inicio"))
        {
            string? name = Indexed(audit, "Inicio", index, "Name");

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return new StartupEntryEvidence(names);
    }

    // ── Procesos: CPU instantánea por delta, nunca acumulada ────────────────

    public static ProcessLoadAssessment AssessProcesses(SystemAuditReport audit)
    {
        double? elapsed = ToDouble(
            Value(audit, "Muestreo de procesos", "elapsedSeconds"));

        double? logicalProcessors = ToDouble(
            Value(audit, "Muestreo de procesos", "logicalProcessors"));

        var first = ReadProcessSamples(audit, "Procesos muestra 1");
        var second = ReadProcessSamples(audit, "Procesos muestra 2");

        var findings = new List<string>
        {
            "La CPU por proceso se calcula como delta de TotalProcessorTime " +
            "entre dos muestras separadas. La CPU acumulada desde el inicio del " +
            "proceso no se usa como consumo instantáneo."
        };

        if (elapsed is not > 0.2 ||
            logicalProcessors is not > 0 ||
            first.Count == 0 ||
            second.Count == 0)
        {
            findings.Add("Datos insuficientes para calcular la carga instantánea de procesos.");
            return new ProcessLoadAssessment(
                Array.Empty<ProcessLoadSample>(),
                false,
                elapsed,
                findings);
        }

        double seconds = elapsed.Value;
        double cores = logicalProcessors.Value;
        var samples = new List<ProcessLoadSample>();

        foreach (var (processId, before) in first)
        {
            if (!second.TryGetValue(processId, out var after))
                continue;

            if (before.CpuSeconds is null || after.CpuSeconds is null)
                continue;

            double deltaSeconds = after.CpuSeconds.Value - before.CpuSeconds.Value;

            if (deltaSeconds < 0)
                continue;

            // Porcentaje del total del sistema (convención del Administrador de tareas).
            double percent = deltaSeconds / seconds / cores * 100d;
            percent = Math.Clamp(percent, 0d, 100d);

            samples.Add(new ProcessLoadSample(
                processId,
                after.Name,
                Math.Round(percent, 1),
                Math.Round(after.WorkingSetMb, 1)));
        }

        var top = samples
            .OrderByDescending(s => s.InstantaneousCpuPercent)
            .ThenByDescending(s => s.WorkingSetMb)
            .Take(10)
            .ToArray();

        findings.Add(
            $"Intervalo de muestreo: {seconds:F2} s; procesadores lógicos: {cores:F0}.");

        return new ProcessLoadAssessment(
            top,
            top.Length > 0,
            seconds,
            findings);
    }

    private sealed record RawProcessSample(
        string Name,
        double? CpuSeconds,
        double WorkingSetMb);

    private static Dictionary<int, RawProcessSample> ReadProcessSamples(
        SystemAuditReport audit,
        string category)
    {
        var samples = new Dictionary<int, RawProcessSample>();

        foreach (int index in Indices(audit, category))
        {
            int? processId = ToInt(Indexed(audit, category, index, "Id"));

            if (!processId.HasValue || samples.ContainsKey(processId.Value))
                continue;

            samples[processId.Value] = new RawProcessSample(
                Indexed(audit, category, index, "ProcessName") ?? "UNKNOWN",
                ToDouble(Indexed(audit, category, index, "CPU")),
                ToDouble(Indexed(audit, category, index, "WorkingSet64")).GetValueOrDefault() /
                1024d / 1024d);
        }

        return samples;
    }

    // ── Lectura de hechos ────────────────────────────────────────────────────

    private static string? Value(
        SystemAuditReport audit,
        string category,
        string name)
    {
        return audit.Facts.FirstOrDefault(f =>
            f.Category == category &&
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string? Indexed(
        SystemAuditReport audit,
        string category,
        int index,
        string property)
    {
        string? indexed = Value(audit, category, $"[{index}] {property}");

        if (indexed is not null)
            return indexed;

        // PowerShell 5.1 serializa un array de un solo elemento como objeto
        // JSON (sin prefijo [n]) al cruzar el retorno de una función. Ese
        // único elemento se lee como índice 0 en lugar de perderse.
        return index == 0 ? Value(audit, category, property) : null;
    }

    private static IEnumerable<int> Indices(
        SystemAuditReport audit,
        string category)
    {
        int[] indexed = audit.Facts
            .Where(f => f.Category == category)
            .Select(f => ParseIndex(f.Name))
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .Distinct()
            .OrderBy(i => i)
            .ToArray();

        if (indexed.Length > 0)
            return indexed;

        // Sin prefijos [n] pero con hechos planos: la categoría vino como un
        // objeto único (GPU, disco o módulo de RAM único). Se trata como [0].
        bool hasPlainFacts = audit.Facts.Any(f =>
            f.Category == category && ParseIndex(f.Name) is null);

        return hasPlainFacts ? new[] { 0 } : Array.Empty<int>();
    }

    private static int? ParseIndex(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '[')
            return null;

        int end = name.IndexOf(']');

        if (end < 2)
            return null;

        return int.TryParse(
            name.Substring(1, end - 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int index)
            ? index
            : null;
    }

    private static double? ToDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double result)
            ? result
            : null;
    }

    private static long? ToLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long result)
            ? result
            : null;
    }

    private static int? ToInt(string? value)
    {
        long? parsed = ToLong(value);

        return parsed is >= int.MinValue and <= int.MaxValue
            ? (int)parsed.Value
            : null;
    }

    private static uint? ToUInt(string? value)
    {
        long? parsed = ToLong(value);

        return parsed is >= 0 and <= uint.MaxValue
            ? (uint)parsed.Value
            : null;
    }

    private static ulong? ToULong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return ulong.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out ulong result)
            ? result
            : null;
    }

    private static bool? ToBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (bool.TryParse(value, out bool parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private static string FormatBytes(ulong? bytes) =>
        bytes is null or 0
            ? "UNKNOWN"
            : $"{bytes.Value / 1024d / 1024d / 1024d:F1} GB";

    private static string FormatMtps(uint? mtps) =>
        mtps is null or 0 ? "UNKNOWN" : $"{mtps.Value} MT/s";

    [GeneratedRegex(@"CHANNEL[\s_\-]*([A-Z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"DIMM[\s_\-]*([A-Z])[0-9]*\b", RegexOptions.IgnoreCase)]
    private static partial Regex DimmLetterPattern();

    [GeneratedRegex(@"PHYSICALDRIVE\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PhysicalDrivePattern();

    [GeneratedRegex(@"(NTFS|ReFS)[^\r\n]*?DisableDeleteNotify\s*=\s*(?:0x)?([01])", RegexOptions.IgnoreCase)]
    private static partial Regex TrimPattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex PowerPlanNamePattern();
}