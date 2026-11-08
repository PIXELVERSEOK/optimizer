using EvoOptimizer.Core.Diagnosis;

namespace EvoOptimizer.Core.Optimization;

public enum OptimizationCategory
{
    Windows,
    Memory,
    Cpu,
    Gpu,
    Process,
    Thermal,
    Storage,
    Gaming,
    Ai
}

/// <summary>Estados diferenciados exigidos por el catálogo EVO.</summary>
public enum OptimizationCatalogState
{
    NotImplemented,
    Available,
    Applied,
    RollbackAvailable,
    Untested,
    InsufficientEvidence,
    Inconclusive,
    ImprovementVerified,
    RegressionDetected,
    NotApplicable
}

public enum CatalogRiskLevel
{
    None,
    Low,
    Medium,
    High
}

public enum OptimizationExecutionKind
{
    WindowsSetting,
    ProcessPriority,
    ProcessAffinity,
    GameProfile,
    Monitoring,
    Advisory,
    Unavailable
}

/// <summary>Contexto medido que el catálogo usa para decidir compatibilidad y estado.</summary>
public sealed record OptimizationLiveContext(
    double? CpuPercent = null,
    double? GpuPercent = null,
    double? MemoryUsedPercent = null,
    double? CommitUsedPercent = null,
    bool Cs2Running = false,
    string? PrimaryBottleneck = null,
    bool IntelUhdPresent = false,
    bool DiscreteGpuPresent = false,
    bool HybridTopologyVerified = false,
    int PerformanceCores = 0,
    int EfficientCores = 0,
    int LogicalProcessors = 0,
    double? CpuTemperatureCelsius = null,
    double? GpuTemperatureCelsius = null,
    double? FreeSystemDrivePercent = null,
    bool HighMemoryPressure = false);

/// <summary>
/// Entrada del catálogo. Toda optimización declara identificador, categoría,
/// compatibilidad, estado detectado, beneficio, riesgo, confianza, autorización,
/// medición y la acción real que la ejecuta (si existe).
/// </summary>
public sealed record OptimizationCatalogEntry(
    string Id,
    string Name,
    string Description,
    OptimizationCategory Category,
    string Compatibility,
    string CurrentState,
    string ExpectedBenefit,
    string Risk,
    CatalogRiskLevel RiskLevel,
    string Confidence,
    bool RequiresAuthorization,
    bool RequiresMeasurement,
    OptimizationCatalogState State,
    bool CanApply,
    bool CanRollback,
    bool CanMeasure,
    string StatusDetail,
    string EvidenceSource,
    DateTimeOffset MeasuredAt,
    OptimizationExecutionKind ExecutionKind,
    string? ActionKey = null,
    Guid? ChangeId = null)
{
    public string StateLabel => State switch
    {
        OptimizationCatalogState.NotImplemented => "NO IMPLEMENTADO",
        OptimizationCatalogState.Available => "DISPONIBLE",
        OptimizationCatalogState.Applied => "APLICADA",
        OptimizationCatalogState.RollbackAvailable => "ROLLBACK DISPONIBLE",
        OptimizationCatalogState.Untested => "SIN MEDIR",
        OptimizationCatalogState.InsufficientEvidence => "EVIDENCIA INSUFICIENTE",
        OptimizationCatalogState.Inconclusive => "INCONCLUSO",
        OptimizationCatalogState.ImprovementVerified => "MEJORA VERIFICADA",
        OptimizationCatalogState.RegressionDetected => "REGRESIÓN DETECTADA",
        OptimizationCatalogState.NotApplicable => "NO APLICA",
        _ => "DESCONOCIDO"
    };

    public string CategoryLabel => Category switch
    {
        OptimizationCategory.Windows => "WINDOWS",
        OptimizationCategory.Memory => "MEMORY INTELLIGENCE",
        OptimizationCategory.Cpu => "CPU INTELLIGENCE",
        OptimizationCategory.Gpu => "GPU INTELLIGENCE",
        OptimizationCategory.Process => "PROCESS GOVERNANCE",
        OptimizationCategory.Thermal => "THERMAL INTELLIGENCE",
        OptimizationCategory.Storage => "STORAGE INTELLIGENCE",
        OptimizationCategory.Gaming => "GAMING",
        OptimizationCategory.Ai => "EVO AI",
        _ => Category.ToString().ToUpperInvariant()
    };

    public string RiskLabel => RiskLevel switch
    {
        CatalogRiskLevel.None => "Riesgo nulo",
        CatalogRiskLevel.Low => "Riesgo bajo",
        CatalogRiskLevel.Medium => "Riesgo medio",
        CatalogRiskLevel.High => "Riesgo alto",
        _ => "Riesgo desconocido"
    };

    public string MeasurementLabel => MeasuredAt == default
        ? "Sin fecha de medición"
        : $"Medido: {MeasuredAt:yyyy-MM-dd HH:mm}";

    public bool IsApplied => State is
        OptimizationCatalogState.Applied or
        OptimizationCatalogState.Untested or
        OptimizationCatalogState.ImprovementVerified or
        OptimizationCatalogState.Inconclusive;

    /// <summary>Color de estado para la interfaz (no altera la lógica).</summary>
    public string StateColor => State switch
    {
        OptimizationCatalogState.Available => "#2F81F7",
        OptimizationCatalogState.Applied => "#3FB950",
        OptimizationCatalogState.RollbackAvailable => "#3FB950",
        OptimizationCatalogState.ImprovementVerified => "#3FB950",
        OptimizationCatalogState.Inconclusive => "#D29922",
        OptimizationCatalogState.InsufficientEvidence => "#D29922",
        OptimizationCatalogState.RegressionDetected => "#F85149",
        OptimizationCatalogState.Untested => "#8B949E",
        OptimizationCatalogState.NotImplemented => "#6E7681",
        OptimizationCatalogState.NotApplicable => "#6E7681",
        _ => "#8B949E"
    };

    /// <summary>
    /// Sólo los ajustes de Windows usan la línea base antes/después del
    /// OptimizationMeasurementService. Las acciones de proceso se miden con
    /// benchmark A/B y las de monitoreo no requieren línea base.
    /// </summary>
    public bool SupportsBaseline => ActionKey is not null &&
        ActionKey.StartsWith("windows:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Construye el catálogo completo a partir de los motores existentes. Es puro:
/// no lee hardware ni aplica cambios. Los datos ausentes producen estados
/// InsufficientEvidence o NotApplicable, nunca valores inventados.
/// </summary>
public static class OptimizationCatalogBuilder
{
    public static IReadOnlyList<OptimizationCatalogEntry> Build(
        DateTimeOffset capturedAt,
        WindowsOptimizationSnapshot windows,
        IReadOnlyList<WindowsOptimizationItem> windowsPlan,
        IReadOnlyList<WindowsOptimizationChange> changes,
        HardwareDiagnosticSnapshot? hardware,
        OptimizationLiveContext live,
        bool cs2PriorityApplied = false,
        bool cs2AffinityApplied = false,
        bool autoGameProfileEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(windowsPlan);
        ArgumentNullException.ThrowIfNull(changes);

        var entries = new List<OptimizationCatalogEntry>();

        foreach (var item in windowsPlan)
            entries.Add(BuildWindowsEntry(capturedAt, item, changes));

        entries.AddRange(BuildCalibrationEntries());
        entries.AddRange(BuildMemoryEntries(hardware, live));
        entries.AddRange(BuildCpuEntries(hardware, live, cs2PriorityApplied, cs2AffinityApplied));
        entries.AddRange(BuildGpuEntries(hardware, live));
        entries.AddRange(BuildProcessEntries(live, autoGameProfileEnabled));
        entries.AddRange(BuildThermalEntries(live));
        entries.AddRange(BuildStorageEntries(hardware, live));
        entries.AddRange(BuildGamingEntries(live, autoGameProfileEnabled));
        entries.AddRange(BuildAiEntries(live, hardware));

        return entries;
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    private static OptimizationCatalogEntry BuildWindowsEntry(
        DateTimeOffset capturedAt,
        WindowsOptimizationItem item,
        IReadOnlyList<WindowsOptimizationChange> changes)
    {
        var change = changes
            .Where(x => x.Target == item.Target &&
                        x.Target != WindowsOptimizationTarget.StartupApp)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();

        var (state, canApply, canRollback, canMeasure, detail) =
            MapWindowsState(item, change);

        return new OptimizationCatalogEntry(
            Id: $"WIN-{item.Target.ToString().ToUpperInvariant()}",
            Name: item.Title,
            Description: item.ProposedState,
            Category: OptimizationCategory.Windows,
            Compatibility: "Windows 10/11 · HKCU o powercfg · sin elevación",
            CurrentState: item.CurrentState,
            ExpectedBenefit: item.ExpectedBenefit,
            Risk: item.Risk,
            RiskLevel: item.RiskLevel == WindowsOptimizationRisk.Low
                ? CatalogRiskLevel.Low
                : CatalogRiskLevel.Medium,
            Confidence: change is not null ? "High" : "Medium",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: state,
            CanApply: canApply,
            CanRollback: canRollback,
            CanMeasure: canMeasure,
            StatusDetail: detail,
            EvidenceSource: "WindowsOptimizationService · registro HKCU / powercfg",
            MeasuredAt: change?.CompletedAt ?? change?.CreatedAt ?? capturedAt,
            ExecutionKind: OptimizationExecutionKind.WindowsSetting,
            ActionKey: $"windows:{item.Target}",
            ChangeId: change?.Id);
    }

    private static (
        OptimizationCatalogState State,
        bool CanApply,
        bool CanRollback,
        bool CanMeasure,
        string Detail) MapWindowsState(
        WindowsOptimizationItem item,
        WindowsOptimizationChange? change)
    {
        if (change is null)
        {
            return item.CanApply
                ? (OptimizationCatalogState.Available, true, false, true,
                   "Capturá una línea base, aplicá y luego medí el resultado.")
                : (OptimizationCatalogState.NotApplicable, false, false, false,
                   item.BlockedReason ?? "El sistema ya se encuentra en el estado propuesto.");
        }

        return change.Status switch
        {
            WindowsOptimizationStatus.Applied =>
                (OptimizationCatalogState.Untested, false, true, true,
                 "Aplicado. Falta la medición posterior."),
            WindowsOptimizationStatus.Verified =>
                (OptimizationCatalogState.Applied, false, true, true,
                 "Aplicado y verificado en el sistema. Falta evidencia de rendimiento."),
            WindowsOptimizationStatus.PerformanceVerified =>
                (OptimizationCatalogState.ImprovementVerified, false, true, true,
                 "Mejora verificada con evidencia antes/después."),
            WindowsOptimizationStatus.PerformanceInconclusive =>
                (OptimizationCatalogState.Inconclusive, false, true, true,
                 "Resultado inconcluso: no se declara mejora."),
            WindowsOptimizationStatus.RollbackFailed =>
                (OptimizationCatalogState.RollbackAvailable, false, true, true,
                 $"La restauración no pudo verificarse. Reintenta el rollback y revisa el estado del sistema. {change.Error}"),
            WindowsOptimizationStatus.Failed =>
                (item.CanApply
                    ? OptimizationCatalogState.Available
                    : OptimizationCatalogState.NotApplicable,
                 item.CanApply, false, item.CanApply,
                 "El intento falló y se restauró el estado original."),
            WindowsOptimizationStatus.RolledBack =>
                (item.CanApply
                    ? OptimizationCatalogState.Available
                    : OptimizationCatalogState.NotApplicable,
                 item.CanApply, false, item.CanApply,
                 "Revertido. El estado original quedó restaurado."),
            _ =>
                (OptimizationCatalogState.Available, item.CanApply, false, true,
                 "Cambio registrado en el diario.")
        };
    }

    // ── Calibración / política ───────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildCalibrationEntries()
    {
        yield return Advisory(
            "WIN-DELIVERY-OPTIMIZATION",
            "Delivery Optimization",
            "Windows puede descargar y compartir actualizaciones entre equipos.",
            OptimizationCategory.Windows,
            "Requiere HKLM y elevación",
            "No se leyó el estado (evita tocar HKLM sin autorización).",
            "Limitar la descarga entre pares reduce actividad de red y disco.",
            "Medio: clave de sistema; requiere elevación y medición.",
            CatalogRiskLevel.Medium,
            "UNKNOWN",
            OptimizationCatalogState.NotImplemented,
            "No implementado: EVO no escribe HKLM sin elevación y autorización explícita.");

        yield return Advisory(
            "WIN-WINDOWED-GAMES",
            "Optimización de juegos en ventana",
            "Windows 11 puede aplicar optimizaciones de presentación a juegos en ventana.",
            OptimizationCategory.Windows,
            "Windows 11",
            "Clave no verificada en este equipo.",
            "Puede mejorar la presentación en ventana en equipos compatibles.",
            "Medio: la clave exacta no está validada; no se inventa.",
            CatalogRiskLevel.Medium,
            "UNKNOWN",
            OptimizationCatalogState.NotImplemented,
            "No implementado: no se modifica una clave sin verificarla en el equipo.");

        yield return Advisory(
            "WIN-OPTIONAL-SERVICES",
            "Servicios opcionales",
            "Desactivar servicios no esenciales puede liberar recursos, pero también romper funciones.",
            OptimizationCategory.Windows,
            "Requiere análisis por fabricante",
            "Sin cambios: EVO no desactiva servicios indiscriminadamente.",
            "Reducción potencial de actividad en segundo plano.",
            "Alto: efectos secundarios difíciles de revertir sin documentación.",
            CatalogRiskLevel.High,
            "UNKNOWN",
            OptimizationCatalogState.NotApplicable,
            "Política de seguridad: no se desactivan servicios sin evidencia del fabricante.");
    }

    // ── Memory Intelligence ──────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildMemoryEntries(
        HardwareDiagnosticSnapshot? hardware,
        OptimizationLiveContext live)
    {
        var memory = hardware?.Memory;

        yield return new OptimizationCatalogEntry(
            "MEM-BALANCED-MONITORING",
            "Monitorización equilibrada de memoria",
            "Seguimiento continuo de RAM disponible, commit y presión de memoria.",
            OptimizationCategory.Memory,
            "Windows 11 · GlobalMemoryStatusEx",
            live.MemoryUsedPercent.HasValue
                ? $"RAM usada: {live.MemoryUsedPercent:F1}%; commit: " +
                  $"{live.CommitUsedPercent?.ToString("F1") ?? "UNKNOWN"}%"
                : "Sin métricas en vivo todavía",
            "Detecta presión real antes de proponer cambios; no promete FPS.",
            "Nulo: sólo lectura.",
            CatalogRiskLevel.None,
            live.MemoryUsedPercent.HasValue ? "High" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: OptimizationCatalogState.Available,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "Monitorización activa mientras EVO Live Monitor está en ejecución.",
            EvidenceSource: "LivePerformanceMonitor · GlobalMemoryStatusEx",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Monitoring);

        yield return new OptimizationCatalogEntry(
            "MEM-ADAPTIVE-CONTAINMENT",
            "Contención adaptativa de memoria",
            "Baja la prioridad de procesos no críticos autorizados cuando hay presión sostenida.",
            OptimizationCategory.Memory,
            "Windows 11 · procesos de usuario",
            live.HighMemoryPressure
                ? "Presión de memoria alta detectada"
                : "Sin presión de memoria demostrada",
            "Reduce competencia por CPU y memoria sin terminar procesos.",
            "Medio: sólo baja prioridad de procesos autorizados; reversible.",
            CatalogRiskLevel.Medium,
            live.HighMemoryPressure ? "Medium" : "Low",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: OptimizationCatalogState.NotImplemented,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: live.HighMemoryPressure
                ? "Disponible como gobierno de procesos (prioridad BelowNormal). " +
                  "No se purga standby ni se termina ningún proceso."
                : "Se activa sólo con evidencia de presión. Sin evidencia no se ejecuta.",
            EvidenceSource: "MemoryPressureEvaluator",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory,
            ActionKey: "process:governance");

        yield return new OptimizationCatalogEntry(
            "MEM-SUPPRESS-STANDBY-PURGE",
            "Supresión de purga de standby",
            "Política: EVO no ejecuta purgas indiscriminadas de la lista standby.",
            OptimizationCategory.Memory,
            "Windows 11",
            "Política activa: sin purgas automáticas",
            "Evita destruir caché útil y provocar relecturas de disco.",
            "Alto: una purga indiscriminada puede empeorar el rendimiento.",
            CatalogRiskLevel.High,
            "High",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: OptimizationCatalogState.NotImplemented,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "No implementado por política. Requeriría justificación, " +
                          "autorización explícita y medición antes/después.",
            EvidenceSource: "Política EVO",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Unavailable);

        if (memory is not null)
        {
            yield return new OptimizationCatalogEntry(
                "MEM-DUAL-CHANNEL",
                "Verificación de Dual Channel",
                "Comprueba canales reales a partir de DeviceLocator/BankLabel.",
                OptimizationCategory.Memory,
                "CIM Win32_PhysicalMemory + matriz de memoria",
                memory.ModeText,
                "Confirma o descarta Dual Channel sin suponerlo por cantidad de módulos.",
                "Nulo: sólo lectura.",
                CatalogRiskLevel.None,
                memory.Confidence.ToString(),
                RequiresAuthorization: false,
                RequiresMeasurement: false,
                State: memory.Mode == DualChannelMode.Unknown
                    ? OptimizationCatalogState.InsufficientEvidence
                    : OptimizationCatalogState.NotApplicable,
                CanApply: false,
                CanRollback: false,
                CanMeasure: false,
                StatusDetail: string.Join(" ", memory.Evidence),
                EvidenceSource: "WindowsSystemAuditor · DeviceLocator/BankLabel",
                MeasuredAt: hardware!.MeasuredAt,
                ExecutionKind: OptimizationExecutionKind.Advisory);
        }
    }

    // ── CPU Intelligence ─────────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildCpuEntries(
        HardwareDiagnosticSnapshot? hardware,
        OptimizationLiveContext live,
        bool priorityApplied,
        bool affinityApplied)
    {
        yield return new OptimizationCatalogEntry(
            "CPU-HYBRID-SCHEDULING",
            "Planificación híbrida P/E",
            "Detecta P-Cores y E-Cores y valida la topología antes de aplicar afinidad.",
            OptimizationCategory.Cpu,
            live.HybridTopologyVerified
                ? $"Topología híbrida: {live.PerformanceCores} P-Cores, " +
                  $"{live.EfficientCores} E-Cores, {live.LogicalProcessors} hilos"
                : "Topología no híbrida o no verificada",
            live.HybridTopologyVerified
                ? "Estrategias de afinidad derivadas de la topología real."
                : "Sin supuestos de P/E.",
            "Estrategias de afinidad derivadas de la topología real, sin máscaras fijas.",
            "Bajo: no modifica el planificador si la topología no es confiable.",
            CatalogRiskLevel.Low,
            live.HybridTopologyVerified ? "High" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: live.HybridTopologyVerified
                ? OptimizationCatalogState.Available
                : OptimizationCatalogState.InsufficientEvidence,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: live.HybridTopologyVerified
                ? "Usá las entradas de afinidad para CS2 cuando el juego esté activo."
                : "No se aplican máscaras universales ni supuestos de topología.",
            EvidenceSource: "IntelHybridTopologyProvider · GetLogicalProcessorInformationEx",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);

        yield return Cs2Entry(
            "CPU-PRIORITY-CS2",
            "Prioridad AboveNormal para CS2",
            "Sube CS2 a AboveNormal (nunca High/Realtime), con token de reversión.",
            "cs2:priority",
            priorityApplied,
            live,
            hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            "Cs2ProcessPriorityOptimizer");

        yield return Cs2Entry(
            "CPU-AFFINITY-PCORES",
            "Afinidad a P-Cores para CS2",
            "Restringe CS2 a los P-Cores detectados dinámicamente.",
            "cs2:affinity-p",
            affinityApplied,
            live,
            hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            "Cs2AffinityOptimizer · máscara derivada de la topología");

        yield return Cs2Entry(
            "CPU-AFFINITY-BALANCED",
            "Afinidad balanceada P + E para CS2",
            "P-Cores más hasta 2 E-Cores auxiliares, derivado de la topología.",
            "cs2:affinity-balanced",
            affinityApplied,
            live,
            hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            "Cs2AffinityOptimizer · máscara derivada de la topología");

        yield return Advisory(
            "CPU-EPP",
            "EPP y configuración energética",
            "Evalúa el equilibrio energía/rendimiento del procesador.",
            OptimizationCategory.Cpu,
            "Requiere powercfg y elevación",
            hardware?.Cpu.InstantaneousUtilizationPercent is null
                ? "Sin lectura de utilización"
                : $"Utilización instantánea: {hardware.Cpu.InstantaneousUtilizationPercent}%",
            "Ajustar EPP puede sostener frecuencias, con mayor consumo.",
            "Medio: requiere elevación y medición sostenida.",
            CatalogRiskLevel.Medium,
            "UNKNOWN",
            OptimizationCatalogState.NotImplemented,
            "No implementado: no se modifica EPP sin una fuente de frecuencia/energía fiable.");

        yield return new OptimizationCatalogEntry(
            "CPU-THROTTLING",
            "Detección de throttling",
            "Compara frecuencia actual contra máxima y temperaturas disponibles.",
            OptimizationCategory.Cpu,
            "Requiere sensores de temperatura",
            hardware?.Cpu.CurrentClockMhz is null
                ? "Frecuencia actual UNKNOWN"
                : $"Actual {hardware.Cpu.CurrentClockMhz} MHz / máx " +
                  $"{hardware.Cpu.MaxClockMhz?.ToString() ?? "UNKNOWN"} MHz",
            "Detecta pérdida de rendimiento sostenido.",
            "Nulo: sólo lectura.",
            CatalogRiskLevel.None,
            live.CpuTemperatureCelsius.HasValue ? "Medium" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: live.CpuTemperatureCelsius.HasValue
                ? OptimizationCatalogState.Available
                : OptimizationCatalogState.InsufficientEvidence,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: live.CpuTemperatureCelsius.HasValue
                ? "Comparar frecuencia y temperatura durante carga sostenida."
                : "Sin sensor de temperatura fiable: no se declara throttling.",
            EvidenceSource: "CIM Win32_Processor + sensores disponibles",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);
    }

    private static OptimizationCatalogEntry Cs2Entry(
        string id,
        string name,
        string description,
        string actionKey,
        bool applied,
        OptimizationLiveContext live,
        DateTimeOffset measuredAt,
        string source)
    {
        bool applicable = live.Cs2Running;

        return new OptimizationCatalogEntry(
            id,
            name,
            description,
            OptimizationCategory.Cpu,
            live.Cs2Running
                ? "CS2 detectado"
                : "Requiere cs2.exe en ejecución",
            applied ? "Aplicada" : "Stock",
            "Reduce cambios de contexto y saltos de hilo; sin promesa de FPS.",
            "Bajo: reversible con token; nunca High/Realtime.",
            CatalogRiskLevel.Low,
            applied ? "High" : "Medium",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: applied
                ? OptimizationCatalogState.Applied
                : applicable
                    ? OptimizationCatalogState.Available
                    : OptimizationCatalogState.NotApplicable,
            CanApply: !applied && applicable,
            CanRollback: applied,
            CanMeasure: true,
            StatusDetail: applied
                ? "Activa. Usá REVERTIR para volver al estado stock."
                : applicable
                    ? "Listo para aplicar."
                    : "Iniciá CS2 para habilitar esta estrategia.",
            EvidenceSource: source,
            MeasuredAt: measuredAt,
            ExecutionKind: actionKey.StartsWith("cs2:affinity", StringComparison.Ordinal)
                ? OptimizationExecutionKind.ProcessAffinity
                : OptimizationExecutionKind.ProcessPriority,
            ActionKey: actionKey);
    }

    // ── GPU Intelligence ─────────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildGpuEntries(
        HardwareDiagnosticSnapshot? hardware,
        OptimizationLiveContext live)
    {
        yield return new OptimizationCatalogEntry(
            "GPU-UMA-BANDWIDTH-GUARD",
            "Protección de ancho de banda UMA (Intel UHD)",
            "Vigila memoria compartida y saturación de la iGPU para evitar competencia con la RAM.",
            OptimizationCategory.Gpu,
            live.IntelUhdPresent
                ? "Intel UHD detectada"
                : "Requiere Intel UHD",
            live.GpuPercent.HasValue
                ? $"GPU 3D: {live.GpuPercent:F1}% · bottleneck: " +
                  $"{live.PrimaryBottleneck ?? "SIN EVALUAR"}"
                : "Sin telemetría de GPU todavía",
            "Detecta saturación y evita recomendar ajustes cuando la iGPU es el límite.",
            "Bajo: sólo lectura y recomendaciones.",
            CatalogRiskLevel.Low,
            live.GpuPercent.HasValue ? "High" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: live.IntelUhdPresent
                ? OptimizationCatalogState.Available
                : OptimizationCatalogState.NotApplicable,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: live.IntelUhdPresent
                ? "Cuando la GPU está saturada, EVO prioriza consistencia de frametime."
                : "No hay Intel UHD compatible detectada.",
            EvidenceSource: "WindowsGpuReader (PDH) + HardwareBottleneckAnalyzer",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Monitoring);

        yield return Advisory(
            "GPU-DISCRETE-QUEUE-ISOLATION",
            "Aislamiento de cola en GPU discreta",
            "Separa la cola de presentación del juego en una GPU discreta compatible.",
            OptimizationCategory.Gpu,
            live.DiscreteGpuPresent ? "GPU discreta detectada" : "Requiere GPU discreta",
            live.DiscreteGpuPresent
                ? "Requiere validación por modelo y driver."
                : "No aplica: sólo hay iGPU o no se detectó dGPU.",
            "Puede reducir interferencias de presentación en dGPU compatibles.",
            "Medio: depende de driver y juego; no implementado.",
            CatalogRiskLevel.Medium,
            "UNKNOWN",
            OptimizationCatalogState.NotImplemented,
            live.DiscreteGpuPresent
                ? "No implementado: requiere lista blanca de modelos/drivers y medición A/B."
                : "No aplica sin GPU discreta compatible.");
    }

    // ── Process Governance ───────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildProcessEntries(
        OptimizationLiveContext live,
        bool autoGameProfile)
    {
        yield return new OptimizationCatalogEntry(
            "PROC-BACKGROUND-THROTTLE",
            "Gobierno de procesos en segundo plano",
            "Clasifica procesos y permite bajar prioridad sólo a los autorizados.",
            OptimizationCategory.Process,
            "Windows 11 · procesos de usuario",
            live.CpuPercent.HasValue
                ? $"CPU del sistema: {live.CpuPercent:F1}%"
                : "Sin métricas en vivo",
            "Reduce competencia de CPU sin terminar procesos críticos.",
            "Medio: cambios de prioridad reversibles; nunca High/Realtime.",
            CatalogRiskLevel.Medium,
            "Medium",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: OptimizationCatalogState.Available,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "Elegí procesos no críticos en la lista de gobierno de procesos.",
            EvidenceSource: "ProcessGovernanceService",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.ProcessPriority,
            ActionKey: "process:governance");

        yield return new OptimizationCatalogEntry(
            "PROC-GAMING-PROFILE",
            "Perfil Gaming automático",
            "Aplica prioridad/afinidad autorizadas al detectar CS2 y las revierte al cerrarlo.",
            OptimizationCategory.Process,
            live.Cs2Running ? "CS2 en ejecución" : "Esperando CS2",
            autoGameProfile ? "Activo" : "Desactivado",
            "Reduce intervención manual y restaura el estado stock al cerrar el juego.",
            "Bajo: sólo usa optimizadores existentes con rollback.",
            CatalogRiskLevel.Low,
            autoGameProfile ? "High" : "Medium",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: autoGameProfile
                ? OptimizationCatalogState.Applied
                : OptimizationCatalogState.Available,
            CanApply: !autoGameProfile,
            CanRollback: autoGameProfile,
            CanMeasure: false,
            StatusDetail: autoGameProfile
                ? "Activo: aplica al iniciar CS2 y revierte al cerrar."
                : "Requiere autorización explícita para automatizar.",
            EvidenceSource: "LivePerformanceMonitor + optimizadores CS2",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.GameProfile,
            ActionKey: "game:auto-profile");
    }

    // ── Thermal Intelligence ─────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildThermalEntries(
        OptimizationLiveContext live)
    {
        double? maxTemp = Max(live.CpuTemperatureCelsius, live.GpuTemperatureCelsius);

        yield return new OptimizationCatalogEntry(
            "THERM-SAFETY-LOCK-CPU-BOOST",
            "Protección térmica y límite de boost",
            "Vigila temperaturas y propone reducir carga si hay evidencia de throttling.",
            OptimizationCategory.Thermal,
            "Requiere sensores de temperatura",
            maxTemp.HasValue
                ? $"Temperatura máxima: {maxTemp:F1} °C"
                : "Temperatura UNKNOWN",
            "Evita degradación sostenida por temperatura.",
            "Medio: un límite de boost reduce rendimiento máximo; reversible.",
            CatalogRiskLevel.Medium,
            maxTemp.HasValue ? "Medium" : "Unknown",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: maxTemp.HasValue
                ? maxTemp >= 95
                    ? OptimizationCatalogState.Available
                    : OptimizationCatalogState.NotApplicable
                : OptimizationCatalogState.InsufficientEvidence,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: maxTemp.HasValue
                ? maxTemp >= 95
                    ? "Temperatura crítica: reducir carga y revisar refrigeración."
                    : "Temperatura por debajo del umbral de protección."
                : "Sin sensor fiable no se modifica el boost ni se muestra 0 °C.",
            EvidenceSource: "Sensores disponibles (si existen)",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Unavailable);
    }

    // ── Storage Intelligence ─────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildStorageEntries(
        HardwareDiagnosticSnapshot? hardware,
        OptimizationLiveContext live)
    {
        var storage = hardware?.Storage ?? Array.Empty<StorageDeviceEvidence>();

        foreach (var disk in storage)
        {
            string name = disk.Name ?? "Disco sin nombre";

            yield return new OptimizationCatalogEntry(
                $"STORAGE-HEALTH-{Sanitize(name)}",
                $"Salud de almacenamiento: {name}",
                "NVMe, HealthStatus, SMART, temperatura y desgaste.",
                OptimizationCategory.Storage,
                disk.IsNvme switch
                {
                    true => "NVMe verificado",
                    false => $"Bus {disk.BusType ?? "UNKNOWN"}",
                    null => "Bus UNKNOWN"
                },
                $"{disk.HealthStatus ?? "UNKNOWN"} · {disk.SmartCoverageText}",
                "Detecta desgaste y temperaturas antes de que afecten la latencia.",
                "Nulo: sólo lectura.",
                CatalogRiskLevel.None,
                disk.SmartCoverage == SmartCoverage.ReliabilityCounters ? "High" :
                    disk.SmartCoverage == SmartCoverage.FailurePrediction ? "Medium" :
                    disk.SmartCoverage == SmartCoverage.HealthStatusOnly ? "Low" : "Unknown",
                RequiresAuthorization: false,
                RequiresMeasurement: false,
                State: disk.SmartCoverage == SmartCoverage.Unavailable
                    ? OptimizationCatalogState.InsufficientEvidence
                    : OptimizationCatalogState.NotApplicable,
                CanApply: false,
                CanRollback: false,
                CanMeasure: false,
                StatusDetail:
                    $"Wear: {disk.WearPercent?.ToString("F1") ?? "UNKNOWN"}%; " +
                    $"Temp: {disk.TemperatureCelsius?.ToString("F1") ?? "UNKNOWN"} °C; " +
                    $"PowerOnHours: {disk.PowerOnHours?.ToString("F0") ?? "UNKNOWN"}. " +
                    (disk.SmartCoverage == SmartCoverage.HealthStatusOnly
                        ? "HealthStatus NO equivale a SMART completo."
                        : string.Empty),
                EvidenceSource: "Get-PhysicalDisk + Get-StorageReliabilityCounter",
                MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
                ExecutionKind: OptimizationExecutionKind.Advisory);
        }

        var trim = hardware?.Trim;

        yield return new OptimizationCatalogEntry(
            "STORAGE-TRIM",
            "TRIM del almacenamiento",
            "Verifica que TRIM esté habilitado en los sistemas de archivos.",
            OptimizationCategory.Storage,
            "Windows 11 · fsutil",
            trim?.Enabled switch
            {
                true => "TRIM activo",
                false => "TRIM desactivado",
                _ => "TRIM UNKNOWN"
            },
            "Mantiene el rendimiento sostenido del SSD/NVMe.",
            "Nulo: sólo lectura.",
            CatalogRiskLevel.None,
            trim?.Confidence == EvidenceConfidence.High ? "High" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: trim?.Enabled == true
                ? OptimizationCatalogState.NotApplicable
                : trim?.Enabled == false
                    ? OptimizationCatalogState.Available
                    : OptimizationCatalogState.InsufficientEvidence,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: trim?.Entries.Count > 0
                ? string.Join(" | ", trim.Entries)
                : "No se pudo leer DisableDeleteNotify.",
            EvidenceSource: "fsutil behavior query DisableDeleteNotify",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);

        double? free = live.FreeSystemDrivePercent;

        yield return new OptimizationCatalogEntry(
            "STORAGE-FREE-SPACE",
            "Espacio libre del sistema",
            "Vigila el espacio libre de la unidad del sistema.",
            OptimizationCategory.Storage,
            "Windows 11",
            free.HasValue ? $"{free:F1}% libre" : "UNKNOWN",
            "Evita latencia por falta de espacio y problemas de actualización.",
            "Nulo: sólo lectura; EVO no borra archivos personales.",
            CatalogRiskLevel.None,
            free.HasValue ? "High" : "Unknown",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: free is < 15
                ? OptimizationCatalogState.Available
                : free.HasValue
                    ? OptimizationCatalogState.NotApplicable
                    : OptimizationCatalogState.InsufficientEvidence,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: free is < 15
                ? "Espacio bajo: revisar archivos y aplicaciones. EVO no elimina archivos."
                : "Espacio suficiente según la lectura actual.",
            EvidenceSource: "DriveInfo / Win32_LogicalDisk",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);
    }

    // ── Gaming ───────────────────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildGamingEntries(
        OptimizationLiveContext live,
        bool autoGameProfile)
    {
        yield return new OptimizationCatalogEntry(
            "GAME-CS2-AUTO",
            "Perfil CS2 automático",
            "Detecta inicio/cierre de CS2 y aplica o revierte el perfil autorizado.",
            OptimizationCategory.Gaming,
            live.Cs2Running ? "CS2 detectado" : "Esperando cs2.exe",
            autoGameProfile ? "Activo" : "Desactivado",
            "Automatiza la aplicación y restauración del perfil de juego.",
            "Bajo: usa prioridad/afinidad seguras y rollback.",
            CatalogRiskLevel.Low,
            "High",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: autoGameProfile
                ? OptimizationCatalogState.Applied
                : OptimizationCatalogState.Available,
            CanApply: !autoGameProfile,
            CanRollback: autoGameProfile,
            CanMeasure: false,
            StatusDetail: autoGameProfile
                ? "Activo. Al cerrar CS2 se restaura el estado stock."
                : "Activalo para automatizar el perfil al iniciar el juego.",
            EvidenceSource: "LivePerformanceMonitor + optimizadores CS2",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.GameProfile,
            ActionKey: "game:auto-profile");

        yield return new OptimizationCatalogEntry(
            "GAME-PRESENTMON",
            "Captura PresentMon",
            "Mide FPS, 1% low, 0.1% low, P95/P99 y stuttering.",
            OptimizationCategory.Gaming,
            live.Cs2Running ? "CS2 detectado" : "Requiere CS2",
            "Las capturas A/B están disponibles en el panel de benchmark.",
            "Evidencia real para decidir si un cambio se conserva.",
            "Nulo: captura de sólo lectura.",
            CatalogRiskLevel.None,
            "Medium",
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: live.Cs2Running
                ? OptimizationCatalogState.Available
                : OptimizationCatalogState.NotApplicable,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "Usá las capturas A/B existentes; sin FPS el resultado será INCONCLUSO.",
            EvidenceSource: "PresentMonCaptureService",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);

        yield return new OptimizationCatalogEntry(
            "GAME-REGRESSION-GUARD",
            "Regression Guard",
            "Revierte un cambio si el 1% low cae o el P99 empeora.",
            OptimizationCategory.Gaming,
            "Windows 11 · evidencia PresentMon",
            "Activo en la evaluación A/B existente.",
            "Protege contra mejoras aparentes con regresión oculta.",
            "Nulo: sólo decide con evidencia medida.",
            CatalogRiskLevel.None,
            "High",
            RequiresAuthorization: false,
            RequiresMeasurement: true,
            State: OptimizationCatalogState.Available,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "Si falta evidencia, el resultado se marca INCONCLUSO.",
            EvidenceSource: "RegressionGuard",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);
    }

    // ── EVO AI ───────────────────────────────────────────────────────────────

    private static IEnumerable<OptimizationCatalogEntry> BuildAiEntries(
        OptimizationLiveContext live,
        HardwareDiagnosticSnapshot? hardware)
    {
        yield return new OptimizationCatalogEntry(
            "AI-BOTTLENECK-COORDINATION",
            "Coordinación de cuello de botella",
            "Combina telemetría, diagnóstico y catálogo para explicar la prioridad.",
            OptimizationCategory.Ai,
            "Windows 11",
            live.PrimaryBottleneck ?? "SIN EVALUAR",
            "Evita aplicar optimizaciones que no atacan el límite real.",
            "Nulo: sólo explica y prioriza.",
            CatalogRiskLevel.None,
            live.PrimaryBottleneck is null ? "Unknown" : "Medium",
            RequiresAuthorization: false,
            RequiresMeasurement: false,
            State: live.PrimaryBottleneck is null
                ? OptimizationCatalogState.InsufficientEvidence
                : OptimizationCatalogState.Available,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "La explicación se muestra en el panel EVO AI.",
            EvidenceSource: "HardwareBottleneckAnalyzer + live metrics",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);

        yield return new OptimizationCatalogEntry(
            "AI-EVIDENCE-GATE",
            "Compuerta de evidencia",
            "No declara mejoras sin baseline y comparación medidas.",
            OptimizationCategory.Ai,
            "Windows 11",
            "Activa",
            "Evita confundir correlación con causalidad.",
            "Nulo: sólo bloquea declaraciones sin evidencia.",
            CatalogRiskLevel.None,
            "High",
            RequiresAuthorization: false,
            RequiresMeasurement: true,
            State: OptimizationCatalogState.Available,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: "Sin evidencia, el estado queda INSUFFICIENT_EVIDENCE/INCONCLUSIVE.",
            EvidenceSource: "OptimizationEvidenceEvaluator + RegressionGuard",
            MeasuredAt: hardware?.MeasuredAt ?? DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Advisory);
    }

    // ── Utilidades ───────────────────────────────────────────────────────────

    private static OptimizationCatalogEntry Advisory(
        string id,
        string name,
        string description,
        OptimizationCategory category,
        string compatibility,
        string currentState,
        string benefit,
        string risk,
        CatalogRiskLevel riskLevel,
        string confidence,
        OptimizationCatalogState state,
        string detail)
    {
        return new OptimizationCatalogEntry(
            id,
            name,
            description,
            category,
            compatibility,
            currentState,
            benefit,
            risk,
            riskLevel,
            confidence,
            RequiresAuthorization: true,
            RequiresMeasurement: true,
            State: state,
            CanApply: false,
            CanRollback: false,
            CanMeasure: false,
            StatusDetail: detail,
            EvidenceSource: "Diagnóstico EVO",
            MeasuredAt: DateTimeOffset.MinValue,
            ExecutionKind: OptimizationExecutionKind.Unavailable);
    }

    private static double? Max(double? first, double? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return Math.Max(first.Value, second.Value);
    }

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value.ToUpperInvariant())
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');

        return builder.ToString();
    }
}