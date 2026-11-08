using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EvoOptimizer.Core.Analysis;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Analysis;
using EvoOptimizer.Infrastructure.Benchmark;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.History;
using EvoOptimizer.Infrastructure.Measurement;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;
using CoreAdaptiveOptimizationEngine = EvoOptimizer.Core.Optimization.AdaptiveOptimizationEngine;

namespace EvoOptimizer.App.ViewModels;

/// <summary>
/// ViewModel del Centro de Control. Orquesta el monitor en vivo, la topología híbrida Intel,
/// la optimización de procesos de CS2 (prioridad y afinidad a P-Cores), el análisis de cuellos
/// de botella y el sistema de experimentación y evaluación comparativa A/B.
/// </summary>
public sealed partial class EvoControlCenterViewModel :
    INotifyPropertyChanged, IAsyncDisposable
{
    private readonly LivePerformanceMonitor _monitor;
    private readonly LiveAnomalyDetector _anomalyDetector;
    private readonly CoreAdaptiveOptimizationEngine _optimizationEngine;
    private readonly HardwareBottleneckAnalyzer _bottleneckAnalyzer;
    private readonly IntelHybridTopologyProvider _topologyProvider;
    private readonly WindowsGamingConfigService _gamingConfigService;
    private readonly Cs2ProcessPriorityOptimizer _priorityOptimizer;
    private readonly Cs2AffinityOptimizer _affinityOptimizer;
    private readonly Cs2ExperimentOrchestrator _experimentOrchestrator;
    private readonly WindowsOptimizationService _windowsOptimizationService;
    private readonly OptimizationMeasurementService _measurementService;
    private readonly EvoHistoryStore? _historyStore;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, OptimizationMeasurement> _windowsBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _baselinesLoaded;

    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private bool _isMonitoring;
    private bool _isBenchmarking;
    private bool _disposed;

    private PriorityOptimizationToken? _activePriorityToken;
    private AffinityOptimizationToken? _activeAffinityToken;

    private double _systemCpuPercent;
    private double? _cs2CpuPercent;
    private double _memoryUsagePercent;
    private double _availableMemoryMb;
    private double _commitUsedMb;
    private double _commitLimitMb;
    private double _cs2MemoryMb;
    private double? _gpuUsagePercent;
    private double? _gpuSharedMemoryMb;
    private int _activeProcessCount;
    private int _cs2ProcessCount;
    private DateTimeOffset? _lastUpdated;
    private string _monitorStatus = "DETENIDO";
    private string _systemHealth = "SIN DATOS";
    private string _primaryBottleneckText = "SIN EVALUAR";
    private string _topologySummary = "";
    private string _gamingConfigSummary = "";

    private string _priorityStatusText = "Prioridad: Normal (Por defecto)";
    private string _affinityStatusText = "Afinidad: Todos los núcleos (Por defecto)";
    private string _benchmarkStatusText = "Listo para iniciar benchmark.";
    private string _baselineMetricsText = "Línea base (Config A): Sin registrar";
    private string _candidateMetricsText = "Candidato (Config B): Sin registrar";
    private string _comparisonResultText = "Evaluación A/B: Pendiente de datos";
    private string _comparisonExplanation = "Ejecute una captura de línea base y luego aplique una optimización para comparar el 1% Low y la consistencia de frametimes.";
    private string _experimentVerdictText = "SIN EXPERIMENTO ACTIVO";
    private string _windowsOptimizationStatusText =
        "Revisá cada ajuste y autorizá sólo los que quieras aplicar.";
    private string _windowsMeasurementStatusText =
        "MEDICIÓN OBLIGATORIA: capturá una línea base antes de aplicar. " +
        "Sin evidencia antes/después no se declara ninguna mejora.";
    private string _windowsEvidenceText =
        "Todavía no hay evidencia registrada. EVO no conserva cambios por teoría " +
        "ni por benchmarks externos.";
    private bool _isApplyingWindowsChange;
    private bool _isMeasuringWindows;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Colecciones observables ──────────────────────────────────────────────

    public ObservableCollection<LiveAnomaly> Anomalies { get; } = new();

    public ObservableCollection<string> Alerts { get; } = new();

    public ObservableCollection<AdaptiveOptimizationRecommendation> OptimizationRecommendations { get; } = new();

    public ObservableCollection<ProcessSummary> TopProcesses { get; } = new();

    public ObservableCollection<BottleneckFinding> BottleneckFindings { get; } = new();

    // ── Optimización de Windows ──────────────────────────────────────────────

    public ObservableCollection<WindowsOptimizationItem> WindowsOptimizationItems { get; } = new();

    public ObservableCollection<WindowsStartupCandidate> StartupCandidates { get; } = new();

    public ObservableCollection<WindowsOptimizationChange> WindowsChanges { get; } = new();

    // ── Propiedades de Hardware y Sistema ────────────────────────────────────

    public CpuTopologyInfo Topology { get; }

    public WindowsGamingConfig GamingConfig { get; private set; }

    public string TopologySummary
    {
        get => _topologySummary;
        private set => SetField(ref _topologySummary, value);
    }

    public string GamingConfigSummary
    {
        get => _gamingConfigSummary;
        private set => SetField(ref _gamingConfigSummary, value);
    }

    public string PrimaryBottleneckText
    {
        get => _primaryBottleneckText;
        private set => SetField(ref _primaryBottleneckText, value);
    }

    // ── Estado de Optimización de CS2 ────────────────────────────────────────

    public string PriorityStatusText
    {
        get => _priorityStatusText;
        private set => SetField(ref _priorityStatusText, value);
    }

    public string AffinityStatusText
    {
        get => _affinityStatusText;
        private set => SetField(ref _affinityStatusText, value);
    }

    public bool HasActivePriorityChange => _activePriorityToken != null;

    public bool HasActiveAffinityChange => _activeAffinityToken != null;

    public bool HasActiveOptimizations => HasActivePriorityChange || HasActiveAffinityChange;

    public string PcoresButtonContent =>
        Topology.IsHybrid
            ? $"AFINIDAD {Topology.PerformanceCores} P-CORES ({Topology.PerformanceLogicalProcessors} HILOS)"
            : "AFINIDAD NÚCLEOS DE RENDIMIENTO";

    public string BalancedButtonContent =>
        Topology.IsHybrid
            ? $"AFINIDAD BALANCEADA ({Topology.PerformanceCores}P + 2E)"
            : "AFINIDAD BALANCEADA";

    // ── Sistema de Experimentación y Benchmark A/B ───────────────────────────

    public bool IsBenchmarking
    {
        get => _isBenchmarking;
        private set
        {
            if (SetField(ref _isBenchmarking, value))
                OnPropertyChanged(nameof(CanRunBenchmark));
        }
    }

    public bool CanRunBenchmark => !IsBenchmarking && !_disposed;

    public string BenchmarkStatusText
    {
        get => _benchmarkStatusText;
        private set => SetField(ref _benchmarkStatusText, value);
    }

    public string BaselineMetricsText
    {
        get => _baselineMetricsText;
        private set => SetField(ref _baselineMetricsText, value);
    }

    public string CandidateMetricsText
    {
        get => _candidateMetricsText;
        private set => SetField(ref _candidateMetricsText, value);
    }

    public string ComparisonResultText
    {
        get => _comparisonResultText;
        private set => SetField(ref _comparisonResultText, value);
    }

    public string ComparisonExplanation
    {
        get => _comparisonExplanation;
        private set => SetField(ref _comparisonExplanation, value);
    }

    public string ExperimentVerdictText
    {
        get => _experimentVerdictText;
        private set => SetField(ref _experimentVerdictText, value);
    }

    // ── Métricas en tiempo real ──────────────────────────────────────────────

    public double SystemCpuPercent
    {
        get => _systemCpuPercent;
        private set => SetField(ref _systemCpuPercent, value);
    }

    public double? Cs2CpuPercent
    {
        get => _cs2CpuPercent;
        private set => SetField(ref _cs2CpuPercent, value);
    }

    public double MemoryUsagePercent
    {
        get => _memoryUsagePercent;
        private set => SetField(ref _memoryUsagePercent, value);
    }

    public double AvailableMemoryMb
    {
        get => _availableMemoryMb;
        private set => SetField(ref _availableMemoryMb, value);
    }

    public double CommitUsedMb
    {
        get => _commitUsedMb;
        private set => SetField(ref _commitUsedMb, value);
    }

    public double CommitLimitMb
    {
        get => _commitLimitMb;
        private set => SetField(ref _commitLimitMb, value);
    }

    public double Cs2MemoryMb
    {
        get => _cs2MemoryMb;
        private set => SetField(ref _cs2MemoryMb, value);
    }

    public double? GpuUsagePercent
    {
        get => _gpuUsagePercent;
        private set => SetField(ref _gpuUsagePercent, value);
    }

    public double? GpuSharedMemoryMb
    {
        get => _gpuSharedMemoryMb;
        private set => SetField(ref _gpuSharedMemoryMb, value);
    }

    public int ActiveProcessCount
    {
        get => _activeProcessCount;
        private set => SetField(ref _activeProcessCount, value);
    }

    public int Cs2ProcessCount
    {
        get => _cs2ProcessCount;
        private set => SetField(ref _cs2ProcessCount, value);
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set => SetField(ref _lastUpdated, value);
    }

    public string MonitorStatus
    {
        get => _monitorStatus;
        private set => SetField(ref _monitorStatus, value);
    }

    public string SystemHealth
    {
        get => _systemHealth;
        private set => SetField(ref _systemHealth, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetField(ref _isMonitoring, value))
                OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool CanStart => !IsMonitoring && !_disposed;

    // ── Estado de la optimización de Windows ─────────────────────────────────

    public string WindowsOptimizationStatusText
    {
        get => _windowsOptimizationStatusText;
        private set => SetField(ref _windowsOptimizationStatusText, value);
    }

    public string WindowsMeasurementStatusText
    {
        get => _windowsMeasurementStatusText;
        private set => SetField(ref _windowsMeasurementStatusText, value);
    }

    public string WindowsEvidenceText
    {
        get => _windowsEvidenceText;
        private set => SetField(ref _windowsEvidenceText, value);
    }

    public bool IsApplyingWindowsChange
    {
        get => _isApplyingWindowsChange;
        private set
        {
            if (SetField(ref _isApplyingWindowsChange, value))
            {
                OnPropertyChanged(nameof(CanApplyWindowsChange));
                OnPropertyChanged(nameof(CanMeasureWindows));
            }
        }
    }

    public bool IsMeasuringWindows
    {
        get => _isMeasuringWindows;
        private set
        {
            if (SetField(ref _isMeasuringWindows, value))
            {
                OnPropertyChanged(nameof(CanMeasureWindows));
                OnPropertyChanged(nameof(CanApplyWindowsChange));
            }
        }
    }

    public bool CanApplyWindowsChange =>
        !IsApplyingWindowsChange && !IsMeasuringWindows && !_disposed;

    public bool CanMeasureWindows =>
        !IsApplyingWindowsChange && !IsMeasuringWindows && !_disposed;

    public bool HasActiveWindowsChanges => WindowsChanges.Count > 0;

    public bool HasWindowsBaselines => _windowsBaselines.Count > 0;

    public bool HasWindowsBaseline(WindowsOptimizationTarget target) =>
        _windowsBaselines.ContainsKey(WindowsKey(target));

    public bool HasStartupBaseline(string valueName) =>
        _windowsBaselines.ContainsKey(StartupKey(valueName));

    // ── Constructor ──────────────────────────────────────────────────────────

    public EvoControlCenterViewModel(
        LivePerformanceMonitor? monitor = null,
        LiveAnomalyDetector? anomalyDetector = null,
        CoreAdaptiveOptimizationEngine? optimizationEngine = null,
        HardwareBottleneckAnalyzer? bottleneckAnalyzer = null,
        IntelHybridTopologyProvider? topologyProvider = null,
        WindowsGamingConfigService? gamingConfigService = null,
        Cs2ProcessPriorityOptimizer? priorityOptimizer = null,
        Cs2AffinityOptimizer? affinityOptimizer = null,
        Cs2ExperimentOrchestrator? experimentOrchestrator = null,
        Dispatcher? dispatcher = null,
        WindowsOptimizationService? windowsOptimizationService = null,
        OptimizationMeasurementService? measurementService = null,
        EvoHistoryStore? historyStore = null)
    {
        _monitor = monitor ?? new LivePerformanceMonitor();
        _anomalyDetector = anomalyDetector ?? new LiveAnomalyDetector();
        _optimizationEngine = optimizationEngine ?? new CoreAdaptiveOptimizationEngine();
        _bottleneckAnalyzer = bottleneckAnalyzer ?? new HardwareBottleneckAnalyzer();
        _topologyProvider = topologyProvider ?? new IntelHybridTopologyProvider();
        _gamingConfigService = gamingConfigService ?? new WindowsGamingConfigService();
        _priorityOptimizer = priorityOptimizer ?? new Cs2ProcessPriorityOptimizer();
        _affinityOptimizer = affinityOptimizer ?? new Cs2AffinityOptimizer();
        _experimentOrchestrator = experimentOrchestrator ?? new Cs2ExperimentOrchestrator();
        _historyStore = historyStore ?? CreateDefaultHistoryStore();
        _windowsOptimizationService =
            windowsOptimizationService ?? new WindowsOptimizationService(
                configService: _gamingConfigService,
                history: _historyStore);
        _measurementService = measurementService ??
            new OptimizationMeasurementService(history: _historyStore);
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        Topology = _topologyProvider.DiscoverTopology();
        GamingConfig = _gamingConfigService.ReadConfiguration();

        TopologySummary = Topology.IsHybrid
            ? $"{Topology.ProcessorName} · {Topology.TotalPhysicalCores} núcleos ({Topology.PerformanceCores} P-Cores / {Topology.PerformanceLogicalProcessors} hilos + {Topology.EfficientCores} E-Cores)"
            : $"{Topology.ProcessorName} · {Topology.LogicalProcessors} procesadores lógicos";

        GamingConfigSummary = $"Modo Juego: {(GamingConfig.GameModeEnabled ? "Activo" : "Desactivado")} · GameDVR: {(GamingConfig.GameDvrEnabled ? "Activo (Consumo extra)" : "Desactivado (Óptimo)")} · HAGS: {(GamingConfig.HagsEnabled ? "Activo" : "Desactivado")} · Plan: {GamingConfig.PowerPlanName}";

        _monitor.SnapshotCaptured += OnSnapshotCaptured;

        InitializeOptimizationHubState();
    }

    /// <summary>
    /// Historial append-only en SQLite para los cambios de Windows. Si la base no
    /// puede inicializarse, el diario JSON sigue siendo la fuente de rollback.
    /// </summary>
    private static EvoHistoryStore? CreateDefaultHistoryStore()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "EvoFpsOptimizer",
                "history.db");

            return new EvoHistoryStore(path);
        }
        catch
        {
            return null;
        }
    }

    // ── Acciones de Optimización de CPU para CS2 ─────────────────────────────

    public void ApplyAboveNormalPriority()
    {
        var result = _priorityOptimizer.Apply();
        PriorityStatusText = result.Status switch
        {
            PriorityOptimizationStatus.Applied => "Prioridad: AboveNormal (Optimización activa)",
            PriorityOptimizationStatus.AlreadyApplied => "Prioridad: Ya se encuentra en AboveNormal",
            _ => $"Prioridad: {result.Message}"
        };

        if (result.Status == PriorityOptimizationStatus.Applied)
        {
            _activePriorityToken = result.Token;
            OnPropertyChanged(nameof(HasActivePriorityChange));
            OnPropertyChanged(nameof(HasActiveOptimizations));
        }
    }

    public bool RollbackPriority()
    {
        if (_activePriorityToken is null)
        {
            PriorityStatusText = "Prioridad: no hay un cambio activo de EVO que restaurar.";
            return true;
        }

        var result = _priorityOptimizer.Rollback(_activePriorityToken.Id);
        PriorityStatusText = result.Status == PriorityOptimizationStatus.RolledBack
            ? $"Prioridad restaurada: {result.Message}"
            : $"Prioridad no restaurada: {result.Message}";

        if (result.Status is PriorityOptimizationStatus.RolledBack
            or PriorityOptimizationStatus.IdentityMismatch)
        {
            _activePriorityToken = null;
            OnPropertyChanged(nameof(HasActivePriorityChange));
            OnPropertyChanged(nameof(HasActiveOptimizations));
        }

        return result.Status == PriorityOptimizationStatus.RolledBack;
    }

    public void ApplyPcoresAffinity()
    {
        var result = _affinityOptimizer.Apply(Topology, GamingAffinityMode.PerformanceCoresOnly);
        AffinityStatusText = result.Status switch
        {
            AffinityOptimizationStatus.Applied => $"Afinidad: {Topology.PerformanceCores} P-Cores ({Topology.PerformanceLogicalProcessors} hilos, Máscara 0x{result.Token?.AppliedAffinityMask ?? Topology.GetAffinityMask(GamingAffinityMode.PerformanceCoresOnly):X})",
            AffinityOptimizationStatus.AlreadyApplied => "Afinidad: Ya se encuentra en P-Cores",
            _ => $"Afinidad: {result.Message}"
        };

        if (result.Status == AffinityOptimizationStatus.Applied)
        {
            _activeAffinityToken = result.Token;
            OnPropertyChanged(nameof(HasActiveAffinityChange));
            OnPropertyChanged(nameof(HasActiveOptimizations));
        }
    }

    public void ApplyBalancedAffinity()
    {
        var result = _affinityOptimizer.Apply(Topology, GamingAffinityMode.PerformanceAndLightEfficiency);
        AffinityStatusText = result.Status switch
        {
            AffinityOptimizationStatus.Applied => $"Afinidad: {Topology.PerformanceCores} P-Cores + E-Cores (Máscara 0x{result.Token?.AppliedAffinityMask ?? Topology.GetAffinityMask(GamingAffinityMode.PerformanceAndLightEfficiency):X})",
            AffinityOptimizationStatus.AlreadyApplied => "Afinidad: Ya se encuentra en modo balanceado",
            _ => $"Afinidad: {result.Message}"
        };

        if (result.Status == AffinityOptimizationStatus.Applied)
        {
            _activeAffinityToken = result.Token;
            OnPropertyChanged(nameof(HasActiveAffinityChange));
            OnPropertyChanged(nameof(HasActiveOptimizations));
        }
    }

    public bool RollbackAffinity()
    {
        if (_activeAffinityToken is null)
        {
            AffinityStatusText = "Afinidad: no hay un cambio activo de EVO que restaurar.";
            return true;
        }

        var result = _affinityOptimizer.Rollback(_activeAffinityToken.Id);
        AffinityStatusText = result.Status == AffinityOptimizationStatus.RolledBack
            ? $"Afinidad restaurada: {result.Message}"
            : $"Afinidad no restaurada: {result.Message}";

        if (result.Status is AffinityOptimizationStatus.RolledBack
            or AffinityOptimizationStatus.IdentityMismatch)
        {
            _activeAffinityToken = null;
            OnPropertyChanged(nameof(HasActiveAffinityChange));
            OnPropertyChanged(nameof(HasActiveOptimizations));
        }

        return result.Status == AffinityOptimizationStatus.RolledBack;
    }

    public void RollbackAllOptimizations()
    {
        RollbackPriority();
        RollbackAffinity();
        BenchmarkStatusText = HasActiveOptimizations
            ? "Rollback incompleto. Revisá los estados individuales; EVO mantiene los cambios pendientes para no ocultar el error."
            : "No quedan cambios de prioridad o afinidad de EVO pendientes de rollback.";
    }

    // ── Optimización de Windows (Modo Juego, Game DVR, energía, inicio) ──────

    public async Task RefreshWindowsOptimizationAsync()
    {
        if (_disposed)
            return;

        await EnsureBaselinesLoadedAsync();

        try
        {
            var plan = await Task.Run(
                () => _windowsOptimizationService.GetPlanAsync());
            var startup = await Task.Run(
                () => _windowsOptimizationService.GetStartupCandidatesAsync());
            var changes = await _windowsOptimizationService.GetChangesAsync();
            var config = await Task.Run(
                () => _gamingConfigService.ReadConfiguration());

            await _dispatcher.InvokeAsync(() =>
            {
                WindowsOptimizationItems.Clear();
                foreach (var item in plan)
                    WindowsOptimizationItems.Add(item);

                StartupCandidates.Clear();
                foreach (var candidate in startup)
                    StartupCandidates.Add(candidate);

                WindowsChanges.Clear();
                foreach (var change in changes
                    .Where(x => x.Status is WindowsOptimizationStatus.Pending
                        or WindowsOptimizationStatus.Applied
                        or WindowsOptimizationStatus.Verified
                        or WindowsOptimizationStatus.PerformanceVerified
                        or WindowsOptimizationStatus.PerformanceInconclusive
                        or WindowsOptimizationStatus.RollbackFailed)
                    .OrderByDescending(x => x.CreatedAt))
                {
                    WindowsChanges.Add(change);
                }

                OnPropertyChanged(nameof(HasActiveWindowsChanges));

                GamingConfig = config;
                GamingConfigSummary = $"Modo Juego: {(config.GameModeEnabled ? "Activo" : "Desactivado")} · GameDVR: {(config.GameDvrEnabled ? "Activo (Consumo extra)" : "Desactivado (Óptimo)")} · HAGS: {(config.HagsEnabled ? "Activo" : "Desactivado")} · Plan: {config.PowerPlanName}";
            });
        }
        catch (Exception ex)
        {
            WindowsOptimizationStatusText =
                $"No se pudo leer el estado de Windows: {ex.Message}";
        }
    }

    public async Task ApplyWindowsOptimizationAsync(
        WindowsOptimizationTarget target,
        bool userAuthorized)
    {
        if (IsApplyingWindowsChange || IsMeasuringWindows)
            return;

        if (!HasWindowsBaseline(target))
        {
            WindowsOptimizationStatusText =
                "MEDICIÓN REQUERIDA: capturá la línea base de este ajuste antes de aplicarlo. " +
                "EVO no modifica Windows sin evidencia antes/después.";
            WindowsMeasurementStatusText = WindowsOptimizationStatusText;
            return;
        }

        IsApplyingWindowsChange = true;
        WindowsOptimizationStatusText = "Aplicando el ajuste y verificando el resultado...";

        try
        {
            var result = await Task.Run(
                () => _windowsOptimizationService.ApplyAsync(
                    target,
                    userAuthorized,
                    hasMeasuredBaseline: true));

            WindowsOptimizationStatusText = result.Message;

            if (result.Status == WindowsOptimizationStatus.Verified)
            {
                WindowsMeasurementStatusText =
                    "Ajuste aplicado y verificado a nivel de build (BUILD_VERIFIED). " +
                    "Ahora ejecutá 'MEDIR POSTERIOR' para comparar contra la línea base " +
                    "y obtener PERFORMANCE_VERIFIED o un rollback automático.";
            }
        }
        catch (Exception ex)
        {
            WindowsOptimizationStatusText =
                $"Error aplicando el ajuste: {ex.Message}";
        }
        finally
        {
            IsApplyingWindowsChange = false;
            await RefreshWindowsOptimizationAsync();
        }
    }

    public async Task DisableStartupAppAsync(
        string valueName,
        bool userAuthorized)
    {
        if (IsApplyingWindowsChange || IsMeasuringWindows)
            return;

        if (!HasStartupBaseline(valueName))
        {
            WindowsOptimizationStatusText =
                $"MEDICIÓN REQUERIDA: capturá la línea base de '{valueName}' antes de desactivarlo.";
            WindowsMeasurementStatusText = WindowsOptimizationStatusText;
            return;
        }

        IsApplyingWindowsChange = true;
        WindowsOptimizationStatusText =
            $"Desactivando '{valueName}' del inicio de Windows...";

        try
        {
            var result = await Task.Run(
                () => _windowsOptimizationService.ApplyStartupAsync(
                    valueName,
                    userAuthorized,
                    hasMeasuredBaseline: true));

            WindowsOptimizationStatusText = result.Message;

            if (result.Status == WindowsOptimizationStatus.Verified)
            {
                WindowsMeasurementStatusText =
                    "Entrada de inicio quitada y verificada. Reiniciá Windows y repetí " +
                    "la misma carga antes de medir CPU, RAM, Commit y procesos. EVO no " +
                    "mide la duración de arranque por aplicación, por lo que no se afirma " +
                    "una mejora del tiempo de inicio.";
            }
        }
        catch (Exception ex)
        {
            WindowsOptimizationStatusText =
                $"Error desactivando el inicio: {ex.Message}";
        }
        finally
        {
            IsApplyingWindowsChange = false;
            await RefreshWindowsOptimizationAsync();
        }
    }

    public async Task RollbackWindowsChangeAsync(Guid changeId)
    {
        if (IsApplyingWindowsChange)
            return;

        IsApplyingWindowsChange = true;
        WindowsOptimizationStatusText = "Revirtiendo el ajuste...";

        try
        {
            var result = await Task.Run(
                () => _windowsOptimizationService.RollbackAsync(changeId));

            WindowsOptimizationStatusText = result.Message;
        }
        catch (Exception ex)
        {
            WindowsOptimizationStatusText =
                $"Error revirtiendo el ajuste: {ex.Message}";
        }
        finally
        {
            IsApplyingWindowsChange = false;
            await RefreshWindowsOptimizationAsync();
        }
    }

    public async Task RollbackAllWindowsChangesAsync()
    {
        if (IsApplyingWindowsChange)
            return;

        IsApplyingWindowsChange = true;
        WindowsOptimizationStatusText = "Revirtiendo todos los cambios de Windows...";

        try
        {
            var results = await Task.Run(
                () => _windowsOptimizationService.RollbackAllAsync());

            WindowsOptimizationStatusText = results.Count == 0
                ? "No hay cambios de Windows activos para revertir."
                : string.Join(" ", results.Select(x => x.Message));
        }
        catch (Exception ex)
        {
            WindowsOptimizationStatusText =
                $"Error revirtiendo los cambios: {ex.Message}";
        }
        finally
        {
            IsApplyingWindowsChange = false;
            await RefreshWindowsOptimizationAsync();
        }
    }

    // ── Medición obligatoria antes/después ───────────────────────────────────

    public async Task CaptureWindowsBaselineAsync(
        WindowsOptimizationTarget target)
    {
        if (IsMeasuringWindows || IsApplyingWindowsChange || _disposed)
            return;

        var item = WindowsOptimizationItems
            .FirstOrDefault(x => x.Target == target);

        string name = item?.Title ?? target.ToString();

        var context = BuildWindowsMeasurementContext(
            WindowsKey(target),
            name,
            OptimizationModificationKind.Windows);

        await CaptureBaselineInternalAsync(context);
    }

    public async Task CaptureStartupBaselineAsync(string valueName)
    {
        if (IsMeasuringWindows || IsApplyingWindowsChange || _disposed)
            return;

        if (string.IsNullOrWhiteSpace(valueName))
            return;

        var context = BuildWindowsMeasurementContext(
            StartupKey(valueName),
            valueName,
            OptimizationModificationKind.Process);

        await CaptureBaselineInternalAsync(context);
    }

    private async Task CaptureBaselineInternalAsync(
        OptimizationMeasurementContext context)
    {
        IsMeasuringWindows = true;
        WindowsMeasurementStatusText =
            $"Capturando línea base de '{context.ModificationName}'... (30 s)";

        try
        {
            var capture = await _measurementService.CaptureAsync(
                context,
                new OptimizationMeasurementCaptureOptions(
                    DurationSeconds: 30,
                    CaptureFps: true));

            var metrics = capture.Measurement.Metrics;
            WindowsEvidenceText = BuildMeasurementSummary(
                "LÍNEA BASE CAPTURADA",
                capture.Measurement,
                capture.Messages);

            var persistence = await _measurementService.SaveBaselineAsync(
                capture.Measurement);

            if (!persistence.Persisted)
            {
                _windowsBaselines.Remove(context.ModificationKey);
                OnPropertyChanged(nameof(HasWindowsBaselines));
                WindowsMeasurementStatusText =
                    $"Línea base capturada, pero no persistida: {persistence.Error}. " +
                    "No se habilitará el cambio hasta poder guardar y recuperar la medición.";
                return;
            }

            _windowsBaselines[context.ModificationKey] = capture.Measurement;
            OnPropertyChanged(nameof(HasWindowsBaselines));

            WindowsMeasurementStatusText = capture.FpsCaptured
                ? $"Línea base registrada para '{context.ModificationName}': " +
                  "persistida en el historial; " +
                  $"{metrics.SampleCount} muestras de sistema y " +
                  $"{metrics.FrameCount} frames FPS."
                : $"Línea base de sistema registrada para " +
                  $"'{context.ModificationName}' y persistida en el historial. FPS no medidos: " +
                  $"{string.Join(" ", capture.Messages)} Sin FPS la mejora de " +
                  "rendimiento no podrá verificarse y el resultado será INCONCLUSO.";
        }
        catch (OperationCanceledException)
        {
            WindowsMeasurementStatusText = "Medición cancelada.";
        }
        catch (Exception ex)
        {
            WindowsMeasurementStatusText =
                $"No se pudo capturar la línea base: {ex.Message}";
        }
        finally
        {
            IsMeasuringWindows = false;
        }
    }

    public async Task EvaluateWindowsMeasurementAsync(Guid changeId)
    {
        if (IsMeasuringWindows || IsApplyingWindowsChange || _disposed)
            return;

        var change = WindowsChanges.FirstOrDefault(x => x.Id == changeId);

        if (change is null)
        {
            var all = await _windowsOptimizationService.GetChangesAsync();
            change = all.FirstOrDefault(x => x.Id == changeId);
        }

        if (change is null)
        {
            WindowsMeasurementStatusText =
                "No se encontró el cambio que se quiere medir.";
            return;
        }

        string key = change.Target == WindowsOptimizationTarget.StartupApp
            ? change.SettingKey
            : WindowsKey(change.Target);

        if (!_windowsBaselines.TryGetValue(key, out var baseline))
        {
            WindowsMeasurementStatusText =
                $"No hay línea base para '{change.SettingKey}'. " +
                "Capturá la línea base antes de aplicar y repetí la medición posterior.";
            return;
        }

        if (change.Target == WindowsOptimizationTarget.StartupApp &&
            !StartupBootSessionGuard.HasRebooted(
                baseline.Context.BootSessionId,
                StartupBootSessionGuard.CaptureCurrentSessionId()))
        {
            WindowsMeasurementStatusText =
                "REINICIO REQUERIDO: la entrada fue cambiada, pero todavía no se " +
                "detectó un reinicio de Windows. No se medirá ni declarará una mejora.";
            return;
        }

        IsMeasuringWindows = true;
        WindowsMeasurementStatusText =
            $"Capturando medición posterior de " +
            $"'{baseline.Context.ModificationName}'... (30 s)";

        try
        {
            var capture = await _measurementService.CaptureAsync(
                baseline.Context,
                new OptimizationMeasurementCaptureOptions(
                    DurationSeconds: 30,
                    CaptureFps: true));

            var result = await _windowsOptimizationService
                .EvaluateMeasurementAsync(
                    changeId,
                    baseline,
                    capture.Measurement);

            WindowsMeasurementStatusText = result.Message;
            WindowsEvidenceText = BuildEvidenceSummary(result.Change?.Evidence);

            if (result.Status == WindowsOptimizationStatus.RolledBack)
            {
                _windowsBaselines.Remove(key);
                OnPropertyChanged(nameof(HasWindowsBaselines));
            }
        }
        catch (OperationCanceledException)
        {
            WindowsMeasurementStatusText = "Medición posterior cancelada.";
        }
        catch (Exception ex)
        {
            WindowsMeasurementStatusText =
                $"No se pudo evaluar la medición posterior: {ex.Message}";
        }
        finally
        {
            IsMeasuringWindows = false;
            await RefreshWindowsOptimizationAsync();
        }
    }

    private async Task EnsureBaselinesLoadedAsync()
    {
        if (_baselinesLoaded)
            return;

        try
        {
            var loaded = await _measurementService.LoadBaselinesAsync();

            foreach (var pair in loaded)
                _windowsBaselines[pair.Key] = pair.Value;

            if (_windowsBaselines.Count > 0)
                OnPropertyChanged(nameof(HasWindowsBaselines));
            _baselinesLoaded = true;
        }
        catch (Exception ex)
        {
            WindowsMeasurementStatusText =
                $"No se pudieron cargar las líneas base persistidas: {ex.Message}";
        }
    }

    private OptimizationMeasurementContext BuildWindowsMeasurementContext(
        string key,
        string name,
        OptimizationModificationKind kind)
    {
        string hardwareId =
            $"{Environment.MachineName}|{Topology.ProcessorName}|" +
            $"{Topology.TotalPhysicalCores}c|{Topology.LogicalProcessors}t";

        return new OptimizationMeasurementContext(
            Guid.NewGuid(),
            key,
            name,
            kind,
            "cs2",
            "WindowsOptimization",
            "unknown",
            "PresentMon+LivePerformanceMonitor",
            hardwareId,
            "windows");
    }

    private static string WindowsKey(WindowsOptimizationTarget target) =>
        $"windows:{target}";

    private static string StartupKey(string valueName) =>
        $"startup:{valueName}";

    private static string BuildMeasurementSummary(
        string label,
        OptimizationMeasurement measurement,
        IReadOnlyList<string> messages)
    {
        var metrics = measurement.Metrics;
        var lines = new List<string>
        {
            $"{label} · {measurement.Context.ModificationName}",
            $"FPS: {Format(metrics.AverageFps)} · " +
            $"1% Low: {Format(metrics.OnePercentLowFps)} · " +
            $"0.1% Low: {Format(metrics.PointOnePercentLowFps)}",
            $"P95: {Format(metrics.P95FrametimeMs)} ms · " +
            $"P99: {Format(metrics.P99FrametimeMs)} ms · " +
            $"Stutter: {Format(metrics.StutterRatePercent)}%",
            $"CPU: {Format(metrics.CpuUsagePercent)}% · " +
            $"GPU: {Format(metrics.GpuUsagePercent)}% · " +
            $"RAM: {Format(metrics.MemoryUsagePercent)}% · " +
            $"Commit: {Format(metrics.CommitUsedMb)} MB · " +
            $"Procesos: {metrics.ActiveProcessCount?.ToString() ?? "N/D"}",
            $"Muestras: {metrics.SampleCount} · " +
            $"Duración: {metrics.DurationSeconds:F1}s · " +
            $"Frames: {metrics.FrameCount}"
        };

        lines.AddRange(measurement.Notes.Select(x => "· " + x));
        lines.AddRange(messages.Select(x => "⚠ " + x));

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildEvidenceSummary(
        OptimizationEvidenceResult? evidence)
    {
        if (evidence is null)
            return "Sin evidencia antes/después. El cambio no se declara beneficioso.";

        var lines = new List<string>
        {
            $"RESULTADO: {evidence.VerdictLabel}",
            $"Decisión: {(evidence.ShouldKeep
                ? "CONSERVADO"
                : evidence.ShouldRollback
                    ? "REVERTIDO"
                    : "REQUIERE DECISIÓN DEL USUARIO")}",
            $"Modificación: {evidence.ModificationName} " +
            $"({evidence.ModificationKey})"
        };

        lines.AddRange(evidence.Evidence);

        if (evidence.Warnings.Count > 0)
        {
            lines.Add("Advertencias:");
            lines.AddRange(evidence.Warnings.Select(x => "⚠ " + x));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(double? value) =>
        value.HasValue ? value.Value.ToString("F2") : "N/D";

    // ── Ejecución de Benchmark y Experimentación A/B ─────────────────────────

    public async Task RunBenchmarkSessionAsync(bool asBaseline, int durationSeconds = 30)
    {
        if (IsBenchmarking)
            return;

        if (!asBaseline && _experimentOrchestrator.Baseline is null)
        {
            BenchmarkStatusText =
                "No existe una línea base válida. Capturá Config A antes de Config B.";
            return;
        }

        if (Cs2ProcessCount == 0)
        {
            BenchmarkStatusText = "CS2 no está en ejecución. Para iniciar el benchmark abra Counter-Strike 2.";
            return;
        }

        IsBenchmarking = true;
        durationSeconds = Math.Clamp(durationSeconds, 30, 600);
        string configLabel = asBaseline ? "Config A (Línea Base)" : "Config B (Optimización)";
        BenchmarkStatusText =
            $"Midiendo {configLabel} con PresentMon y telemetría real ({durationSeconds}s).";

        try
        {
            if (asBaseline)
            {
                _experimentOrchestrator.Clear();
                BaselineMetricsText = "Línea base: sin captura válida";
                CandidateMetricsText = "Candidato: sin captura válida";
                ComparisonResultText =
                    "Evaluación A/B: pendiente de mediciones reales";
            }

            var context = new OptimizationMeasurementContext(
                Guid.NewGuid(),
                "cs2:manual-ab",
                configLabel,
                OptimizationModificationKind.Gaming,
                "CS2",
                "Partida actual; EVO no controla ni verifica el mapa/escenario",
                "UNKNOWN",
                "PresentMon + LivePerformanceMonitor",
                Topology.ProcessorName,
                $"{TopologySummary}|{GamingConfigSummary}");

            OptimizationMeasurementCaptureResult capture =
                await _measurementService.CaptureAsync(
                    context,
                    new OptimizationMeasurementCaptureOptions(
                        DurationSeconds: durationSeconds,
                        CaptureFps: true),
                    _cancellation?.Token ?? CancellationToken.None);

            OptimizationMetricValues measured = capture.Measurement.Metrics;

            if (!capture.FpsCaptured ||
                measured.AverageFps is null ||
                measured.OnePercentLowFps is null ||
                measured.PointOnePercentLowFps is null ||
                measured.P95FrametimeMs is null ||
                measured.P99FrametimeMs is null ||
                measured.StutterRatePercent is null ||
                measured.FrameCount < 1000 ||
                measured.FpsDurationSeconds is null ||
                measured.FpsDurationSeconds < 30)
            {
                ExperimentVerdictText = "NO VERIFICABLE";
                ComparisonExplanation =
                    "No se alcanzaron 30 s y 1000 frames válidos de PresentMon. " +
                    "La telemetría de sistema, si existe, no sustituye la medición de FPS.";
                BenchmarkStatusText =
                    $"No se registraron métricas FPS/A-B para {configLabel}. " +
                    string.Join(" ", capture.Messages);
                return;
            }

            var metrics = new Cs2BenchmarkMetrics(
                DateTimeOffset.UtcNow,
                configLabel,
                measured.AverageFps.Value,
                measured.OnePercentLowFps.Value,
                measured.PointOnePercentLowFps.Value,
                measured.P95FrametimeMs.Value,
                measured.P99FrametimeMs.Value,
                measured.StutterRatePercent.Value,
                measured.FrameCount,
                measured.FpsDurationSeconds.Value,
                measured.CpuUsagePercent,
                null,
                measured.GpuUsagePercent,
                null,
                measured.MemoryUsagePercent,
                measured.CommitUsedMb);

            if (asBaseline)
            {
                _experimentOrchestrator.RecordBaseline(metrics);
                BaselineMetricsText =
                    $"Línea base medida: {metrics.AverageFps:F1} FPS avg · " +
                    $"1% Low: {metrics.OnePercentLowFps:F1} · " +
                    $"P99: {metrics.P99FrametimeMs:F1} ms";
                BenchmarkStatusText =
                    $"PresentMon midió {metrics.FrameCount} frames en " +
                    $"{metrics.DurationSeconds:F1}s. Capturá Config B en el mismo escenario.";
                ExperimentVerdictText = "LÍNEA BASE LISTA (Config A)";
            }
            else
            {
                _experimentOrchestrator.RecordCandidate(metrics);
                CandidateMetricsText =
                    $"Candidato medido: {metrics.AverageFps:F1} FPS avg · " +
                    $"1% Low: {metrics.OnePercentLowFps:F1} · " +
                    $"P99: {metrics.P99FrametimeMs:F1} ms";

                var comparison = _experimentOrchestrator.LatestComparison;
                if (comparison != null)
                {
                    ComparisonResultText = $"Δ 1% Low: {comparison.DeltaOnePercentLowPercent:+0.0;-0.0}% · Δ P99: {comparison.DeltaP99FrametimePercent:+0.0;-0.0}% · Δ Avg: {comparison.DeltaAverageFpsPercent:+0.0;-0.0}%";
                    ComparisonExplanation =
                        comparison.Explanation +
                        " Escenario y cambios externos no se controlan automáticamente; " +
                        "repetí pruebas comparables antes de atribuir causalidad.";
                    ExperimentVerdictText = comparison.Verdict switch
                    {
                        ExperimentVerdict.ImprovementVerified => "MEJORA OBSERVADA · REPETIR PARA CONFIRMAR",
                        ExperimentVerdict.RegressionDetected => "REGRESIÓN OBSERVADA · REPETIR PARA CONFIRMAR",
                        ExperimentVerdict.GpuBottleneckDominant => "USO GPU ELEVADO OBSERVADO",
                        _ => "DIFERENCIA NO SIGNIFICATIVA (Ruido)"
                    };
                    BenchmarkStatusText = $"Evaluación A/B finalizada: {ExperimentVerdictText}.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusText =
                "Medición A/B cancelada; no se guardó una captura parcial como resultado.";
        }
        catch (Exception ex)
        {
            BenchmarkStatusText = $"Error durante benchmark: {ex.Message}";
        }
        finally
        {
            IsBenchmarking = false;
        }
    }

    // ── Control del ciclo de vida del monitor ────────────────────────────────

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsMonitoring)
            return Task.CompletedTask;

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        var token = _cancellation.Token;

        IsMonitoring = true;
        MonitorStatus = "INICIANDO";
        SystemHealth = "RECOPILANDO DATOS";

        _monitorTask = Task.Run(
            () => _monitor.StartAsync(token),
            token);

        _ = ObserveMonitorAsync(_monitorTask);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsMonitoring)
            return;

        MonitorStatus = "DETENIENDO";
        _cancellation?.Cancel();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Detención solicitada por el usuario.
            }
        }

        IsMonitoring = false;
        MonitorStatus = "DETENIDO";
    }

    private async Task ObserveMonitorAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Finalización normal.
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                MonitorStatus = "ERROR";
                SystemHealth = "ERROR DE MONITORIZACIÓN";
                Alerts.Insert(0, ex.Message);
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsMonitoring = false;

                if (MonitorStatus != "ERROR")
                    MonitorStatus = "DETENIDO";
            });
        }
    }

    private void OnSnapshotCaptured(LivePerformanceSnapshot snapshot)
    {
        _ = _dispatcher.InvokeAsync(() => UpdateDashboard(snapshot));
    }

    // ── Actualización del dashboard ──────────────────────────────────────────

    private void UpdateDashboard(LivePerformanceSnapshot snapshot)
    {
        if (_disposed)
            return;

        // Métricas en tiempo real
        SystemCpuPercent = snapshot.SystemCpuPercent;
        Cs2CpuPercent = snapshot.Cs2CpuPercent;
        MemoryUsagePercent = snapshot.MemoryUsagePercent;
        AvailableMemoryMb = snapshot.AvailableMemoryMb;
        CommitUsedMb = snapshot.CommitUsedMb;
        CommitLimitMb = snapshot.CommitLimitMb;
        Cs2MemoryMb = snapshot.Cs2MemoryMb;
        GpuUsagePercent = snapshot.GpuUsagePercent;
        GpuSharedMemoryMb = snapshot.GpuSharedMemoryMb;
        ActiveProcessCount = snapshot.ActiveProcessCount;
        Cs2ProcessCount = snapshot.Cs2ProcessCount;
        LastUpdated = snapshot.Timestamp;

        // Procesos competidores en tiempo real
        TopProcesses.Clear();
        foreach (var p in snapshot.TopProcesses)
            TopProcesses.Add(p);

        // Detección de anomalías en telemetría
        var history = _monitor.GetHistory();
        var anomalyReport = _anomalyDetector.Analyze(history);

        Anomalies.Clear();
        foreach (var anomaly in anomalyReport.Anomalies)
            Anomalies.Add(anomaly);

        // Alertas combinadas
        Alerts.Clear();
        foreach (var alert in snapshot.Alerts)
            Alerts.Add(alert);

        foreach (var anomaly in anomalyReport.Anomalies)
        {
            if (!Alerts.Contains(anomaly.Code))
                Alerts.Add($"{anomaly.Title}: {anomaly.Evidence}");
        }

        // Análisis de cuello de botella mediante HardwareBottleneckAnalyzer
        if (history.Count >= 5)
        {
            var bottleneckSamples = history.Select(s => new HardwarePerformanceSample(
                CpuUsagePercent: s.SystemCpuPercent,
                GpuUsagePercent: s.GpuUsagePercent ?? 0,
                MemoryUsagePercent: s.MemoryUsagePercent,
                AvailableMemoryMb: s.AvailableMemoryMb,
                DiskActivePercent: 0,
                DiskResponseTimeMs: 0,
                CpuTemperatureCelsius: 0,
                GpuTemperatureCelsius: 0,
                CpuFrequencyMhz: 0,
                GpuFrequencyMhz: 0,
                CpuPowerWatts: 0,
                GpuPowerWatts: 0,
                FrametimeMs: 0,
                GpuMemoryUsageMb: s.GpuSharedMemoryMb ?? 0,
                TotalGpuMemoryMb: 2048));

            var bottleneckReport = _bottleneckAnalyzer.Analyze(bottleneckSamples);

            PrimaryBottleneckText = bottleneckReport.PrimaryBottleneck switch
            {
                BottleneckType.Gpu => "GPU (Intel UHD Graphics - Límite de procesamiento gráfico)",
                BottleneckType.Cpu => "CPU (Saturación de procesamiento central)",
                BottleneckType.Memory => "MEMORIA RAM (Presión de memoria física / commit)",
                BottleneckType.Storage => "ALMACENAMIENTO (Actividad de disco)",
                BottleneckType.Thermal => "TÉRMICO (Throttling de temperatura)",
                BottleneckType.Power => "ENERGÍA (Límite de potencia)",
                _ => "EQUILIBRADO / SIN CUELLO DE BOTELLA DEMOSTRABLE"
            };

            BottleneckFindings.Clear();
            foreach (var finding in bottleneckReport.Findings)
                BottleneckFindings.Add(finding);
        }
        else
        {
            PrimaryBottleneckText = "RECOPILANDO MUESTRAS (Mínimo 5 requeridas)";
        }

        // Motor de optimización adaptativa (Core): combina señales de anomalía + configuración de Windows
        var signals = new List<OptimizationSignal>(anomalyReport.OptimizationSignals);
        signals.AddRange(_gamingConfigService.GenerateSignals(GamingConfig));

        if (signals.Count > 0)
        {
            var optimizationReport = _optimizationEngine.Evaluate(signals);

            OptimizationRecommendations.Clear();
            foreach (var rec in optimizationReport.Recommendations)
                OptimizationRecommendations.Add(rec);
        }

        // Estado general del sistema
        SystemHealth = !anomalyReport.HasEnoughData
            ? "RECOPILANDO DATOS"
            : anomalyReport.Anomalies.Any(x =>
                x.Severity == LiveAnomalySeverity.Critical)
                ? "ATENCIÓN CRÍTICA"
                : anomalyReport.Anomalies.Any(x =>
                    x.Severity == LiveAnomalySeverity.Warning)
                    ? "ATENCIÓN"
                    : "ESTABLE";

        MonitorStatus = "MONITOREANDO";

        NotifyLiveSnapshotForHub(snapshot);
    }

    // ── INPC ─────────────────────────────────────────────────────────────────

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    // ── Disposable ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (IsMonitoring)
            await StopAsync();

        _disposed = true;
        _hubCancellation?.Cancel();
        _hubCancellation?.Dispose();
        _monitor.SnapshotCaptured -= OnSnapshotCaptured;
        _cancellation?.Dispose();
        _monitor.Dispose();
    }
}
