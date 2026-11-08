using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EvoOptimizer.App.Navigation;

public enum EvoModuleStatus
{
    Available,
    Partial,
    Planned,
    Backlog,
    Blocked
}

public enum EvoModuleRoute
{
    Dashboard,
    Diagnosis,
    ControlCenter,
    Scanner,
    LiveMonitor,
    Tweaks,
    Debloat,
    HardwareDiagnostics,
    OptimizationCatalog,
    Cs2Configuration,
    Benchmark,
    EvoAi,
    HistoryDashboard,
    SystemCleaner,
    NetworkDiagnostics,
    GamingOptimizer,
    GameBooster,
    Placeholder
}

public enum EvoDiagnosticScope
{
    General,
    System,
    Gaming,
    Hardware,
    Unavailable
}

public sealed record EvoNavigationCategory(
    string Number,
    string Name,
    string Icon,
    bool InitiallyExpanded,
    EvoDiagnosticScope AnalysisScope,
    string AnalysisDetails,
    IReadOnlyList<EvoNavigationModule> Modules)
{
    public bool CanAnalyze => AnalysisScope != EvoDiagnosticScope.Unavailable;
}

public sealed class EvoNavigationModule(
    string id,
    string name,
    string description,
    EvoModuleStatus status,
    string existingComponent,
    string statusDetails,
    EvoModuleRoute route) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public EvoModuleStatus Status { get; } = status;
    public string ExistingComponent { get; } = existingComponent;
    public string StatusDetails { get; } = statusDetails;
    public EvoModuleRoute Route { get; } = route;

    public string StatusLabel => Status switch
    {
        EvoModuleStatus.Available => "DISPONIBLE",
        EvoModuleStatus.Partial => "PARCIAL",
        EvoModuleStatus.Planned => "PENDIENTE",
        EvoModuleStatus.Backlog => "BACKLOG",
        EvoModuleStatus.Blocked => "BLOQUEADO",
        _ => throw new InvalidOperationException($"Unknown module status: {Status}")
    };

    public string StatusColor => Status switch
    {
        EvoModuleStatus.Available => "#42D392",
        EvoModuleStatus.Partial => "#F5B84B",
        EvoModuleStatus.Planned => "#93A0AF",
        EvoModuleStatus.Backlog => "#FF8B72",
        EvoModuleStatus.Blocked => "#FF6670",
        _ => "#93A0AF"
    };

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class EvoNavigationCatalog
{
    public static IReadOnlyList<EvoNavigationCategory> Categories { get; } =
    [
        new("01", "INICIO", "\uE80F", true, EvoDiagnosticScope.General,
        "Auditoría general de solo lectura y telemetría existente.",
        [
            M("home.dashboard", "Dashboard", "Resumen del equipo y telemetría disponible.", EvoModuleStatus.Available, "EvoDashboardShell + DashboardViewModel", "Dashboard integrado con datos reales de CPU/RAM y disponibilidad explícita para las métricas sin fuente.", EvoModuleRoute.Dashboard)
        ]),
        new("02", "SISTEMA", "\uE713", false, EvoDiagnosticScope.System,
        "Auditoría de solo lectura filtrada a almacenamiento, energía, servicios, inicio y procesos. No aplica cambios.",
        [
            M("home.control-center", "Control Center", "Centro de mando para diagnóstico y operaciones existentes.", EvoModuleStatus.Available, "EvoControlCenterWindow + EvoControlCenterViewModel", "Incluye las funciones existentes de optimización; cada cambio conserva sus confirmaciones.", EvoModuleRoute.ControlCenter),
            M("diagnosis.system-diagnostics", "Diagnóstico del sistema", "Hallazgos explicables de Windows, hardware y telemetría.", EvoModuleStatus.Available, "WindowsSystemAuditor + AdaptiveDiagnosticEngine + LiveTelemetryDiagnosticEvaluator", "Ejecuta el análisis existente y exporta el informe.", EvoModuleRoute.Diagnosis),
            M("diagnosis.smart-scan", "Smart Scan", "Escaneo de solo lectura del catálogo de tweaks.", EvoModuleStatus.Available, "EvoTweakPanel · Scanner", "Abre el escáner existente; no escribe cambios.", EvoModuleRoute.Scanner),
            M("optimization.tweaks-lab", "Tweaks", "Catálogo central de ajustes de Windows.", EvoModuleStatus.Available, "EvoTweakPanel · Tweaks + RegistryTweakService", "Usa el catálogo existente con confirmación, verificación y rollback.", EvoModuleRoute.Tweaks),
            M("windows.debloat", "Debloat", "Gestión selectiva y restaurable de paquetes UWP.", EvoModuleStatus.Available, "EvoTweakPanel · Debloat + DebloatService", "Abre el flujo existente con confirmación y restauración.", EvoModuleRoute.Debloat),
            M("windows.cleaner", "Limpieza avanzada", "Análisis y limpieza selectiva de temporales y cachés identificadas.", EvoModuleStatus.Partial, "AdvancedSystemCleanupService + AdvancedSystemCleanupViewModel", "Limpieza real con exclusiones y registro por archivo; destinos no compatibles permanecen en solo lectura.", EvoModuleRoute.SystemCleaner),
            M("crosscutting.evo-ai", "EVO AI", "Decisiones y advertencias conectadas a los motores existentes.", EvoModuleStatus.Partial, "EvoAiDecisions + EvoControlCenterViewModel", "Presenta decisiones y advertencias; no aplica cambios automáticamente.", EvoModuleRoute.EvoAi)
        ]),
        new("03", "GAMING", "\uE7FC", false, EvoDiagnosticScope.Gaming,
        "Solo detecta el estado del proceso CS2 y la falta de mediciones de rendimiento. No mide FPS ni frametimes.",
        [
            M("gaming.game-profiles", "Perfiles de juego · CS2", "Configuración específica actualmente soportada.", EvoModuleStatus.Partial, "Cs2AutoexecConfigService + Control Center", "Gestión limitada y reversible de autoexec.cfg para CS2.", EvoModuleRoute.Cs2Configuration),
            M("gaming.game-optimizer", "Game Optimizer", "Optimización universal específica por juego.", EvoModuleStatus.Backlog, "T9-001 en backlog", "El módulo universal sigue pendiente.", EvoModuleRoute.Placeholder)
        ]),
        new("04", "HARDWARE", "\uE9D9", false, EvoDiagnosticScope.Hardware,
        "Auditoría de hardware y telemetría disponible filtrada por componente; los sensores ausentes no se infieren.",
        [
            M("diagnosis.hardware-intelligence", "Diagnóstico de hardware", "Identificación y evidencia disponible de los componentes.", EvoModuleStatus.Partial, "RegistryHardwareProfileProvider + AdaptiveDiagnosticEngine", "CPU/RAM y datos de auditoría disponibles; sensores térmicos, SMART y otras fuentes solo aparecen cuando se recopilan.", EvoModuleRoute.HardwareDiagnostics),
            M("diagnosis.compatibility-engine", "Compatibilidad", "Compatibilidad entre hardware, drivers, aplicaciones y ajustes.", EvoModuleStatus.Planned, "Sin motor independiente conectado", "No se genera un resultado de compatibilidad hasta integrar y validar sus fuentes.", EvoModuleRoute.Placeholder)
        ]),
        new("05", "RED", "\uE774", false, EvoDiagnosticScope.Unavailable,
        "Diagnóstico de solo lectura disponible para adaptadores, gateway, conectividad ICMP y resolución DNS.",
        [
            M("windows.network", "Diagnóstico de red", "Conectividad, adaptadores y latencia.", EvoModuleStatus.Partial, "NetworkDiagnosisService + NetworkDiagnosisPanel", "Inventario de adaptadores, gateway IPv4, pruebas ICMP y resolución DNS. No modifica TCP/IP ni DNS; ICMP puede estar filtrado.", EvoModuleRoute.NetworkDiagnostics)
        ]),
        new("06", "LABORATORIO", "\uE9D2", false, EvoDiagnosticScope.Unavailable,
        "Pendiente: captura, benchmark y medición tienen flujos existentes, pero no un análisis de laboratorio integrado en esta área.",
        [
            M("gaming.gaming-monitor", "Live Monitor y benchmarks", "Telemetría y captura de rendimiento disponibles.", EvoModuleStatus.Partial, "EvoLiveMonitorPanel + PresentMon/benchmark services", "La monitorización existente no garantiza FPS/frametime en todos los juegos; pruebas A/B y estabilidad no están integradas como análisis.", EvoModuleRoute.LiveMonitor)
        ]),
        new("07", "HISTORIAL", "\uE81C", false, EvoDiagnosticScope.Unavailable,
        "El análisis por área está pendiente. El visor de registros locales ya está disponible; la restauración de cambios se mantiene en Control Center.",
        [
            M("admin.history", "Operaciones y restauraciones", "Registros recientes y acceso a los cambios activos restaurables.", EvoModuleStatus.Partial, "HistoryDashboardService + EvoHistoryStore + Control Center", "La pantalla muestra registros locales y permite abrir los cambios activos existentes; el flujo de restauración sigue en Control Center.", EvoModuleRoute.HistoryDashboard)
        ])
    ];

    public static IReadOnlyList<EvoNavigationModule> CrosscuttingModules { get; } =
        Categories.SelectMany(category => category.Modules)
            .Where(module => module.Id == "crosscutting.evo-ai")
            .ToArray();

    private static readonly IReadOnlyDictionary<string, EvoNavigationModule> ModulesById =
        BuildModuleIndex();

    static EvoNavigationCatalog() => Select("home.dashboard");

    public static EvoNavigationModule? Find(string id) =>
        ModulesById.GetValueOrDefault(id);

    public static EvoNavigationModule? FindAnalysisModule(EvoDiagnosticScope scope) =>
        scope switch
        {
            EvoDiagnosticScope.General => Find("home.dashboard"),
            EvoDiagnosticScope.System => Find("diagnosis.system-diagnostics"),
            EvoDiagnosticScope.Gaming => Find("gaming.game-profiles"),
            EvoDiagnosticScope.Hardware => Find("diagnosis.hardware-intelligence"),
            EvoDiagnosticScope.Unavailable => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown diagnostic scope.")
        };

    public static IReadOnlyList<T> FilterAnalysisItems<T>(
        EvoDiagnosticScope scope,
        IEnumerable<T> items,
        Func<T, string> idSelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(idSelector);

        return scope switch
        {
            EvoDiagnosticScope.General => items.ToArray(),
            EvoDiagnosticScope.System => items
                .Where(item => HasPrefix(idSelector(item),
                    "SYSTEM_DISK", "POWER_PLAN_", "SERVICES_", "STARTUP_", "PROCESS_"))
                .ToArray(),
            EvoDiagnosticScope.Gaming => items
                .Where(item => IsExactId(idSelector(item), "CS2_PROCESS_STATE", "BENCHMARK_REQUIRED"))
                .ToArray(),
            EvoDiagnosticScope.Hardware => items
                .Where(item => HasPrefix(idSelector(item),
                    "MEMORY_", "SYSTEM_DISK", "CPU_", "GPU_", "DISPLAY_", "STORAGE_",
                    "POWER_PLAN_", "LIVE_HARDWARE_PROFILE", "LIVE_CPU_", "LIVE_MEMORY_",
                    "LIVE_TELEMETRY_"))
                .ToArray(),
            EvoDiagnosticScope.Unavailable => Array.Empty<T>(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown diagnostic scope.")
        };
    }

    public static void Select(string id)
    {
        foreach (var module in ModulesById.Values)
            module.IsSelected = module.Id == id;
    }

    private static IReadOnlyDictionary<string, EvoNavigationModule> BuildModuleIndex()
    {
        var modules = Categories.SelectMany(category => category.Modules)
            .ToArray();
        var duplicate = modules
            .GroupBy(module => module.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate EVO navigation ID: {duplicate.Key}");

        return modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
    }

    private static EvoNavigationModule M(
        string id,
        string name,
        string description,
        EvoModuleStatus status,
        string existingComponent,
        string statusDetails,
        EvoModuleRoute route) =>
        new(id, name, description, status, existingComponent, statusDetails, route);

    private static bool HasPrefix(string id, params string[] prefixes) =>
        prefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsExactId(string id, params string[] expectedIds) =>
        expectedIds.Contains(id, StringComparer.OrdinalIgnoreCase);
}
