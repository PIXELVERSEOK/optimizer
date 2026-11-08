using System.Collections.ObjectModel;
using EvoOptimizer.Core.Audit;
using EvoOptimizer.Core.Diagnosis;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Audit;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App.ViewModels;

/// <summary>
/// Fila legible del diagnóstico de hardware: dato, valor, origen, confiabilidad
/// y fecha de medición. Un dato no disponible se muestra como UNKNOWN.
/// </summary>
public sealed record HardwareFactRow(
    string Category,
    string Name,
    string Value,
    string Source,
    string Confidence,
    string MeasuredAtText);

/// <summary>
/// Extensión del ViewModel: catálogo de optimizaciones, diagnóstico continuo de
/// hardware, EVO AI, gobierno de procesos y perfiles. Reutiliza los motores
/// existentes; no crea ejecutores paralelos.
/// </summary>
public sealed partial class EvoControlCenterViewModel
{
    private WindowsSystemAuditor? _auditor;
    private AdaptiveDiagnosticEngine? _diagnosticEngine;
    private ProcessGovernanceService? _governance;
    private readonly MemoryPressureEvaluator _memoryPressureEvaluator = new();
    private HardwareDiagnosticSnapshot? _hardwareSnapshot;
    private CancellationTokenSource? _hubCancellation;
    private bool _hubStarted;
    private DateTimeOffset _lastGovernanceRefresh = DateTimeOffset.MinValue;
    private bool _autoProfileAppliedByHub;

    private string _catalogSearchText = string.Empty;
    private string _selectedCatalogCategory = "TODAS";
    private int? _evoPerformanceScore;
    private string _evoPerformanceScoreText = "SIN DATOS";
    private string _evoPerformanceBreakdownText =
        "El puntaje se calcula con telemetría real. Sin muestras no se inventa.";
    private string _diagnosticStatusText = "DIAGNÓSTICO NO EJECUTADO";
    private string _diagnosticSummaryText =
        "Ejecutá el diagnóstico para leer CPU, RAM, almacenamiento, TRIM, plan " +
        "de energía, servicios y procesos. Todo dato ausente queda UNKNOWN.";
    private DateTimeOffset? _lastDiagnosticAt;
    private string _activeProfile = "PERSONALIZADO";
    private string _profileStatusText =
        "Los perfiles agrupan optimizaciones existentes. No habilitan cambios nuevos.";
    private string _autoGameProfileStatusText =
        "Perfil automático de CS2 desactivado.";
    private bool _isAutoGameProfileEnabled;
    private string _evoAiSummaryText =
        "EVO AI todavía no tiene evidencia. No se declara ninguna mejora.";
    private string _catalogStatusText =
        "El catálogo se genera con el estado real de Windows y de los motores.";
    private string _memoryPressureStatusText =
        "Presión de memoria: sin muestras en vivo.";
    private bool _isRefreshingCatalog;
    private bool _isRefreshingDiagnostics;

    private readonly List<OptimizationCatalogEntry> _catalogEntries = new();

    // ── Colecciones ──────────────────────────────────────────────────────────

    public ObservableCollection<OptimizationCatalogEntry> CatalogView { get; } = new();

    public ObservableCollection<OptimizationCatalogEntry> ActiveOptimizations { get; } = new();

    public ObservableCollection<HardwareFactRow> HardwareFactRows { get; } = new();

    public ObservableCollection<DiagnosticFinding> DiagnosticFindings { get; } = new();

    public ObservableCollection<EvoAiDecision> EvoAiDecisions { get; } = new();

    public ObservableCollection<string> EvoAiConflicts { get; } = new();

    public ObservableCollection<string> EvoAiWarnings { get; } = new();

    public ObservableCollection<GovernedProcess> GovernedProcesses { get; } = new();

    public ObservableCollection<string> CatalogCategories { get; } = new();

    public string MemoryPressureStatusText
    {
        get => _memoryPressureStatusText;
        private set => SetField(ref _memoryPressureStatusText, value);
    }

    // ── Propiedades del catálogo ─────────────────────────────────────────────

    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (SetField(ref _catalogSearchText, value))
                RebuildCatalogView();
        }
    }

    public string SelectedCatalogCategory
    {
        get => _selectedCatalogCategory;
        set
        {
            if (SetField(ref _selectedCatalogCategory, value))
                RebuildCatalogView();
        }
    }

    public string CatalogStatusText
    {
        get => _catalogStatusText;
        private set => SetField(ref _catalogStatusText, value);
    }

    public bool IsRefreshingCatalog
    {
        get => _isRefreshingCatalog;
        private set => SetField(ref _isRefreshingCatalog, value);
    }

    public int CatalogTotalCount => _catalogEntries.Count;

    public int CatalogAvailableCount => _catalogEntries.Count(x =>
        x.State == OptimizationCatalogState.Available);

    public int CatalogAppliedCount => _catalogEntries.Count(x => x.IsApplied);

    public int CatalogVerifiedCount => _catalogEntries.Count(x =>
        x.State == OptimizationCatalogState.ImprovementVerified);

    public int CatalogRollbackCount => _catalogEntries.Count(x =>
        x.CanRollback);

    public int CatalogPendingCount => _catalogEntries.Count(x =>
        x.State is OptimizationCatalogState.Untested
            or OptimizationCatalogState.Inconclusive
            or OptimizationCatalogState.InsufficientEvidence);

    public string CatalogCountsText =>
        $"{CatalogTotalCount} optimizaciones · {CatalogAvailableCount} disponibles · " +
        $"{CatalogAppliedCount} aplicadas · {CatalogVerifiedCount} verificadas · " +
        $"{CatalogPendingCount} sin evidencia";

    // ── Diagnóstico ──────────────────────────────────────────────────────────

    public string DiagnosticStatusText
    {
        get => _diagnosticStatusText;
        private set => SetField(ref _diagnosticStatusText, value);
    }

    public string DiagnosticSummaryText
    {
        get => _diagnosticSummaryText;
        private set => SetField(ref _diagnosticSummaryText, value);
    }

    public string LastDiagnosticAtText => _lastDiagnosticAt.HasValue
        ? $"Última medición: {_lastDiagnosticAt:yyyy-MM-dd HH:mm:ss}"
        : "Sin fecha de medición";

    public bool IsRefreshingDiagnostics
    {
        get => _isRefreshingDiagnostics;
        private set
        {
            if (SetField(ref _isRefreshingDiagnostics, value))
                OnPropertyChanged(nameof(CanRefreshDiagnostics));
        }
    }

    public bool CanRefreshDiagnostics => !IsRefreshingDiagnostics && !_disposed;

    // ── EVO AI / Puntaje ─────────────────────────────────────────────────────

    public int? EvoPerformanceScore
    {
        get => _evoPerformanceScore;
        private set => SetField(ref _evoPerformanceScore, value);
    }

    public string EvoPerformanceScoreText
    {
        get => _evoPerformanceScoreText;
        private set => SetField(ref _evoPerformanceScoreText, value);
    }

    public string EvoPerformanceBreakdownText
    {
        get => _evoPerformanceBreakdownText;
        private set => SetField(ref _evoPerformanceBreakdownText, value);
    }

    public string EvoAiSummaryText
    {
        get => _evoAiSummaryText;
        private set => SetField(ref _evoAiSummaryText, value);
    }

    // ── Perfiles y automatización ────────────────────────────────────────────

    public string ActiveProfile
    {
        get => _activeProfile;
        private set => SetField(ref _activeProfile, value);
    }

    public string ProfileStatusText
    {
        get => _profileStatusText;
        private set => SetField(ref _profileStatusText, value);
    }

    public bool IsAutoGameProfileEnabled
    {
        get => _isAutoGameProfileEnabled;
        set
        {
            if (!SetField(ref _isAutoGameProfileEnabled, value))
                return;

            AutoGameProfileStatusText = value
                ? "Perfil automático de CS2 ACTIVO: al detectar el juego aplica " +
                  "prioridad AboveNormal y afinidad híbrida ya existentes; al salir, revierte."
                : "Perfil automático de CS2 desactivado.";

            if (!value && _autoProfileAppliedByHub)
            {
                RollbackPriority();
                RollbackAffinity();
                _autoProfileAppliedByHub = HasActiveOptimizations;
                if (_autoProfileAppliedByHub)
                {
                    AutoGameProfileStatusText =
                        "Perfil automático desactivado; quedó un rollback pendiente. " +
                        $"{PriorityStatusText} {AffinityStatusText}";
                }
            }

            _ = RefreshCatalogAsync();
        }
    }

    public string AutoGameProfileStatusText
    {
        get => _autoGameProfileStatusText;
        private set => SetField(ref _autoGameProfileStatusText, value);
    }

    // ── Servicios (lazy, sin duplicar motores) ───────────────────────────────

    private WindowsSystemAuditor Auditor => _auditor ??= new WindowsSystemAuditor();

    private AdaptiveDiagnosticEngine DiagnosticEngine =>
        _diagnosticEngine ??= new AdaptiveDiagnosticEngine();

    private ProcessGovernanceService Governance =>
        _governance ??= new ProcessGovernanceService(_historyStore);

    private void InitializeOptimizationHubState()
    {
        CatalogCategories.Clear();
        CatalogCategories.Add("TODAS");
        foreach (var label in Enum.GetValues<OptimizationCategory>()
                     .Select(c => LabelOf(c)))
        {
            CatalogCategories.Add(label);
        }
    }

    private static string LabelOf(OptimizationCategory category) => category switch
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
        _ => category.ToString().ToUpperInvariant()
    };

    // ── Arranque del hub ─────────────────────────────────────────────────────

    public async Task StartOptimizationHubAsync()
    {
        if (_hubStarted || _disposed)
            return;

        _hubStarted = true;
        _hubCancellation = new CancellationTokenSource();

        await RefreshCatalogAsync();
        await RefreshDiagnosticsAsync();

        _ = RunDiagnosticLoopAsync(_hubCancellation.Token);
    }

    private async Task RunDiagnosticLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                await RefreshDiagnosticsAsync(cancellationToken);
                await RefreshCatalogAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal del hub.
        }
    }

    // ── Diagnóstico continuo de hardware ─────────────────────────────────────

    public async Task RefreshDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsRefreshingDiagnostics || _disposed)
            return;

        IsRefreshingDiagnostics = true;
        DiagnosticStatusText = "MIDIENDO HARDWARE (solo lectura)...";

        try
        {
            var report = await Task.Run(
                () => Auditor.CaptureAsync(cancellationToken),
                cancellationToken);

            var snapshot = HardwareEvidenceEvaluator.Evaluate(report);
            var diagnostic = DiagnosticEngine.Analyze(report);

            // Topología P/E real vía Win32 (solo lectura). Se remide en cada
            // ciclo para que la fecha de medición acompañe al dato; no se
            // infiere de la cantidad de núcleos informada por CIM.
            var topology = await Task.Run(
                () => _topologyProvider.DiscoverTopology(),
                cancellationToken);
            var topologyMeasuredAt = DateTimeOffset.Now;

            await _dispatcher.InvokeAsync(() =>
            {
                _hardwareSnapshot = snapshot;
                _lastDiagnosticAt = report.CapturedAt;

                FillHardwareRows(snapshot, topology, topologyMeasuredAt);

                DiagnosticFindings.Clear();
                foreach (var finding in diagnostic.Findings
                    .OrderByDescending(x => x.Severity))
                {
                    DiagnosticFindings.Add(finding);
                }

                DiagnosticStatusText = diagnostic.Findings.Any(x =>
                    x.Severity == DiagnosticSeverity.Critical)
                    ? "DIAGNÓSTICO CON HALLAZGOS CRÍTICOS"
                    : diagnostic.Findings.Any(x =>
                        x.Severity == DiagnosticSeverity.Warning)
                        ? "DIAGNÓSTICO CON ADVERTENCIAS"
                        : "DIAGNÓSTICO COMPLETADO";

                DiagnosticSummaryText = BuildDiagnosticSummary(snapshot, diagnostic);

                OnPropertyChanged(nameof(LastDiagnosticAtText));
                UpdateEvoPerformanceScore();
            });
        }
        catch (OperationCanceledException)
        {
            DiagnosticStatusText = "Diagnóstico cancelado.";
        }
        catch (Exception ex)
        {
            DiagnosticStatusText = $"No se pudo medir el hardware: {ex.Message}";
        }
        finally
        {
            IsRefreshingDiagnostics = false;
        }
    }

    private void FillHardwareRows(
        HardwareDiagnosticSnapshot snapshot,
        CpuTopologyInfo topology,
        DateTimeOffset topologyMeasuredAt)
    {
        HardwareFactRows.Clear();

        string stamp = snapshot.MeasuredAt.ToString("yyyy-MM-dd HH:mm");
        string source = "WindowsSystemAuditor · CIM/WMI (solo lectura)";

        var cpu = snapshot.Cpu;
        HardwareFactRows.Add(new(
            "CPU",
            "Procesador",
            cpu.Name ?? "UNKNOWN",
            source,
            cpu.Name is null ? "Unknown" : "High",
            stamp));
        HardwareFactRows.Add(new(
            "CPU",
            "Núcleos físicos / lógicos",
            $"{FormatInt(cpu.PhysicalCores)} / {FormatInt(cpu.LogicalProcessors)}",
            source,
            cpu.PhysicalCores is null ? "Unknown" : "High",
            stamp));
        HardwareFactRows.Add(new(
            "CPU",
            "Frecuencia máxima / actual",
            $"{FormatInt(cpu.MaxClockMhz)} MHz / {FormatInt(cpu.CurrentClockMhz)} MHz",
            source,
            cpu.MaxClockMhz is null ? "Unknown" : "Medium",
            stamp));
        HardwareFactRows.Add(new(
            "CPU",
            "Utilización instantánea",
            cpu.InstantaneousUtilizationPercent is null
                ? "UNKNOWN"
                : $"{cpu.InstantaneousUtilizationPercent}% (LoadPercentage)",
            source,
            cpu.InstantaneousUtilizationPercent is null ? "Unknown" : "Medium",
            stamp));
        HardwareFactRows.Add(new(
            "CPU",
            "Potencia instantánea",
            cpu.InstantaneousPowerWatts is null
                ? "UNKNOWN"
                : $"{cpu.InstantaneousPowerWatts:F1} W",
            "Requiere RAPL/PDH · no disponible",
            cpu.PowerConfidence.ToString(),
            stamp));

        string topologyStamp = topologyMeasuredAt.ToString("yyyy-MM-dd HH:mm");
        HardwareFactRows.Add(new(
            "CPU",
            "Topología P/E (núcleos híbridos)",
            DescribeTopology(topology),
            "Win32 GetLogicalProcessorInformationEx (solo lectura)",
            topology.IsTopologyReliable ? "High" : "Unknown",
            topologyStamp));

        if (snapshot.Graphics.Count == 0)
        {
            HardwareFactRows.Add(new(
                "GPU",
                "Adaptador gráfico",
                "UNKNOWN (la consulta no devolvió datos)",
                source,
                "Unknown",
                stamp));
        }

        foreach (var gpu in snapshot.Graphics)
        {
            string label = gpu.Name ?? $"GPU {gpu.Index + 1}";

            HardwareFactRows.Add(new(
                "GPU",
                label,
                $"{gpu.AdapterCompatibility ?? "UNKNOWN"} · " +
                $"{gpu.VideoProcessor ?? "UNKNOWN"} · Estado: {gpu.Status ?? "UNKNOWN"}",
                source,
                gpu.Name is null ? "Unknown" : "High",
                stamp));

            HardwareFactRows.Add(new(
                "GPU",
                $"{label} · driver",
                gpu.HasDriverEvidence
                    ? $"Versión: {gpu.DriverVersion ?? "UNKNOWN"} · Fecha: {gpu.DriverDate ?? "UNKNOWN"}"
                    : "UNKNOWN (Windows no informó versión ni fecha)",
                source,
                gpu.HasDriverEvidence ? "High" : "Unknown",
                stamp));

            if (gpu.HorizontalResolution is > 0 && gpu.VerticalResolution is > 0)
            {
                HardwareFactRows.Add(new(
                    "GPU",
                    $"{label} · modo actual",
                    $"{gpu.HorizontalResolution}×{gpu.VerticalResolution} · " +
                    $"{(gpu.RefreshRateHz is > 0 ? $"{gpu.RefreshRateHz} Hz" : "Hz UNKNOWN")}",
                    source,
                    "Medium",
                    stamp));
            }
        }

        var memory = snapshot.Memory;
        HardwareFactRows.Add(new(
            "RAM",
            "Canales verificados",
            memory.ModeText,
            "DeviceLocator/BankLabel",
            memory.Confidence.ToString(),
            stamp));
        HardwareFactRows.Add(new(
            "RAM",
            "Módulos instalados / ranuras",
            $"{memory.ModuleCount} / {FormatInt(memory.SlotCount)}",
            source,
            "High",
            stamp));

        foreach (var module in memory.Modules)
        {
            string speed = module.ConfiguredMtps is null
                ? "UNKNOWN"
                : $"{module.ConfiguredMtps} MT/s configurados" +
                  (module.NominalMtps is null ? "" : $" / {module.NominalMtps} nominales");

            HardwareFactRows.Add(new(
                "RAM",
                $"Módulo {module.Index + 1} · {module.DeviceLocator ?? "UNKNOWN"}",
                $"{FormatBytes(module.CapacityBytes)} · {speed} · {module.Manufacturer ?? "UNKNOWN"}",
                source,
                module.ConfiguredMtps is null ? "Low" : "High",
                stamp));
        }

        foreach (var storage in snapshot.Storage)
        {
            string value =
                $"{storage.MediaType ?? "UNKNOWN"} · {storage.BusType ?? "UNKNOWN"} · " +
                $"{FormatBytes(storage.SizeBytes)} · Salud: {storage.HealthStatus ?? "UNKNOWN"} · " +
                $"SMART: {storage.SmartCoverageText}";

            HardwareFactRows.Add(new(
                "ALMACENAMIENTO",
                storage.Name ?? "Disco UNKNOWN",
                value,
                "Get-PhysicalDisk/Get-StorageReliabilityCounter",
                storage.SmartCoverage == SmartCoverage.Unavailable ? "Unknown" : "Medium",
                stamp));

            if (storage.TemperatureCelsius is not null || storage.WearPercent is not null)
            {
                HardwareFactRows.Add(new(
                    "ALMACENAMIENTO",
                    $"{storage.Name ?? "Disco"} · sensores",
                    $"Temperatura: {(storage.TemperatureCelsius is null ? "UNKNOWN" : $"{storage.TemperatureCelsius:F0} °C")} · " +
                    $"Desgaste: {(storage.WearPercent is null ? "UNKNOWN" : $"{storage.WearPercent:F0}%")} · " +
                    $"Horas: {(storage.PowerOnHours is null ? "UNKNOWN" : $"{storage.PowerOnHours:F0}")}",
                    "Get-StorageReliabilityCounter",
                    storage.SmartCoverage.ToString(),
                    stamp));
            }
        }

        if (snapshot.Storage.Count == 0)
        {
            HardwareFactRows.Add(new(
                "ALMACENAMIENTO",
                "Discos",
                "UNKNOWN (la consulta no devolvió datos)",
                source,
                "Unknown",
                stamp));
        }

        HardwareFactRows.Add(new(
            "ALMACENAMIENTO",
            "TRIM",
            snapshot.Trim.Enabled is null
                ? "UNKNOWN"
                : snapshot.Trim.Enabled.Value
                    ? "ACTIVO"
                    : "DESACTIVADO",
            "fsutil behavior query DisableDeleteNotify",
            snapshot.Trim.Confidence.ToString(),
            stamp));

        HardwareFactRows.Add(new(
            "ENERGÍA",
            "Plan activo",
            snapshot.PowerPlan.Name ?? "UNKNOWN",
            "powercfg /getactivescheme",
            snapshot.PowerPlan.Confidence.ToString(),
            stamp));

        HardwareFactRows.Add(new(
            "SERVICIOS",
            "Servicios automáticos detenidos",
            $"{snapshot.Services.StoppedAutoStartCount} de {snapshot.Services.AutoStartCount}",
            "WindowsSystemAuditor · Win32_Service",
            "High",
            stamp));

        IReadOnlyList<WindowsServiceEvidence> serviceInventory =
            snapshot.Services.Inventory ?? Array.Empty<WindowsServiceEvidence>();

        HardwareFactRows.Add(new(
            "SERVICIOS",
            "Inventario consultado",
            $"{serviceInventory.Count} servicios",
            "WindowsSystemAuditor · Win32_Service",
            serviceInventory.Count == 0 ? "Unknown" : "High",
            stamp));

        foreach (WindowsServiceEvidence service in serviceInventory)
        {
            HardwareFactRows.Add(new(
                "SERVICIOS",
                service.DisplayName,
                $"{service.Name} · Estado: {service.State} · Inicio: {service.StartMode} · " +
                $"PID: {(service.ProcessId is > 0 ? service.ProcessId.Value.ToString() : "N/D")}",
                "WindowsSystemAuditor · Win32_Service",
                service.State == "UNKNOWN" || service.StartMode == "UNKNOWN"
                    ? "Low"
                    : "High",
                stamp));
        }

        HardwareFactRows.Add(new(
            "INICIO",
            "Aplicaciones de inicio",
            snapshot.Startup.Names.Count == 0
                ? "SIN DATOS"
                : string.Join(", ", snapshot.Startup.Names.Take(8)),
            "Win32_StartupCommand",
            snapshot.Startup.Names.Count == 0 ? "Unknown" : "High",
            stamp));
    }

    private static string DescribeTopology(CpuTopologyInfo topology)
    {
        if (!topology.IsTopologyReliable)
            return "UNKNOWN (topología no verificable)";

        if (!topology.IsHybrid)
            return $"Estándar: {topology.TotalPhysicalCores} núcleos / " +
                   $"{topology.LogicalProcessors} hilos (sin clases P/E)";

        return $"{topology.PerformanceCores} P-Cores ({topology.PerformanceLogicalProcessors} hilos) + " +
               $"{topology.EfficientCores} E-Cores ({topology.EfficientLogicalProcessors} hilos)";
    }

    private static string BuildDiagnosticSummary(
        HardwareDiagnosticSnapshot snapshot,
        DiagnosticReport report)
    {
        int critical = report.Findings.Count(x =>
            x.Severity == DiagnosticSeverity.Critical);
        int warnings = report.Findings.Count(x =>
            x.Severity == DiagnosticSeverity.Warning);

        return
            $"{snapshot.ComputerName} · {snapshot.Cpu.Name ?? "CPU UNKNOWN"} · " +
            $"{snapshot.Memory.ModeText} · {snapshot.Storage.Count} disco(s) · " +
            $"{critical} hallazgo(s) crítico(s) · {warnings} advertencia(s). " +
            "Datos sin fuente verificable se muestran como UNKNOWN.";
    }

    // ── Catálogo de optimizaciones ───────────────────────────────────────────

    public async Task RefreshCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsRefreshingCatalog || _disposed)
            return;

        IsRefreshingCatalog = true;

        try
        {
            var snapshot = await _windowsOptimizationService
                .CaptureSnapshotAsync(cancellationToken);
            var plan = await _windowsOptimizationService.GetPlanAsync(cancellationToken);
            var changes = await _windowsOptimizationService.GetChangesAsync(cancellationToken);

            var entries = OptimizationCatalogBuilder.Build(
                DateTimeOffset.UtcNow,
                snapshot,
                plan,
                changes,
                _hardwareSnapshot,
                BuildLiveContext(),
                cs2PriorityApplied: _activePriorityToken is not null,
                cs2AffinityApplied: _activeAffinityToken is not null,
                autoGameProfileEnabled: IsAutoGameProfileEnabled);

            await _dispatcher.InvokeAsync(() =>
            {
                _catalogEntries.Clear();
                _catalogEntries.AddRange(entries);

                RebuildCatalogView();
                UpdateActiveOptimizations();
                UpdateEvoPerformanceScore();
                UpdateEvoAi(entries);

                CatalogStatusText =
                    $"{CatalogCountsText} · origen: Windows, telemetría y diario de cambios.";
            });
        }
        catch (Exception ex)
        {
            CatalogStatusText = $"No se pudo actualizar el catálogo: {ex.Message}";
        }
        finally
        {
            IsRefreshingCatalog = false;
        }
    }

    private OptimizationLiveContext BuildLiveContext()
    {
        var latest = _monitor.GetLatest();

        double? commitPercent = null;
        if (latest is not null && latest.CommitLimitMb > 0)
            commitPercent = latest.CommitUsedMb / latest.CommitLimitMb * 100.0;

        string? bottleneck =
            PrimaryBottleneckText.StartsWith("SIN", StringComparison.OrdinalIgnoreCase) ||
            PrimaryBottleneckText.StartsWith("RECOPILANDO", StringComparison.OrdinalIgnoreCase)
                ? null
                : PrimaryBottleneckText;

        return new OptimizationLiveContext(
            CpuPercent: latest?.SystemCpuPercent,
            GpuPercent: latest?.GpuUsagePercent,
            MemoryUsedPercent: latest?.MemoryUsagePercent,
            CommitUsedPercent: commitPercent,
            Cs2Running: (latest?.Cs2ProcessCount ?? Cs2ProcessCount) > 0,
            PrimaryBottleneck: bottleneck,
            IntelUhdPresent: _hardwareSnapshot is not null &&
                             (Topology.ProcessorName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                              GamingConfigSummary.Contains("Intel", StringComparison.OrdinalIgnoreCase)),
            DiscreteGpuPresent: false,
            HybridTopologyVerified: Topology.IsHybrid,
            PerformanceCores: Topology.PerformanceCores,
            EfficientCores: Topology.EfficientCores,
            LogicalProcessors: Topology.LogicalProcessors,
            CpuTemperatureCelsius: null,
            GpuTemperatureCelsius: null,
            FreeSystemDrivePercent: null,
            HighMemoryPressure: latest is not null &&
                _memoryPressureEvaluator.Evaluate(new MemoryPressureEvidence(
                    MegabytesToBytes(latest.TotalMemoryMb),
                    MegabytesToBytes(latest.AvailableMemoryMb),
                    MegabytesToBytes(latest.CommitUsedMb),
                    MegabytesToBytes(latest.CommitLimitMb),
                    SystemHardFaultsPerSecond: null)).Level is
                    MemoryPressureLevel.High or MemoryPressureLevel.Critical);
    }

    private static long? MegabytesToBytes(double megabytes) =>
        double.IsFinite(megabytes) &&
        megabytes >= 0 &&
        megabytes <= long.MaxValue / (1024d * 1024d)
            ? (long)(megabytes * 1024d * 1024d)
            : null;

    private void RebuildCatalogView()
    {
        IEnumerable<OptimizationCatalogEntry> query = _catalogEntries;

        if (!string.Equals(SelectedCatalogCategory, "TODAS", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => string.Equals(
                x.CategoryLabel,
                SelectedCatalogCategory,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(CatalogSearchText))
        {
            string term = CatalogSearchText.Trim();
            query = query.Where(x =>
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.CategoryLabel.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        CatalogView.Clear();
        foreach (var entry in query
            .OrderBy(x => x.Category)
            .ThenBy(x => x.Name))
        {
            CatalogView.Add(entry);
        }

        OnPropertyChanged(nameof(CatalogTotalCount));
        OnPropertyChanged(nameof(CatalogAvailableCount));
        OnPropertyChanged(nameof(CatalogAppliedCount));
        OnPropertyChanged(nameof(CatalogVerifiedCount));
        OnPropertyChanged(nameof(CatalogRollbackCount));
        OnPropertyChanged(nameof(CatalogPendingCount));
        OnPropertyChanged(nameof(CatalogCountsText));
    }

    private void UpdateActiveOptimizations()
    {
        ActiveOptimizations.Clear();
        foreach (var entry in _catalogEntries.Where(x => x.IsApplied))
            ActiveOptimizations.Add(entry);
    }

    // ── Acciones del catálogo ────────────────────────────────────────────────

    /// <summary>
    /// Ejecuta una acción del catálogo reutilizando los ejecutores existentes.
    /// Acciones: "baseline", "apply", "rollback", "measure".
    /// </summary>
    public async Task ExecuteCatalogActionAsync(
        OptimizationCatalogEntry entry,
        string action)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_disposed)
            return;

        string key = entry.ActionKey ?? string.Empty;

        if (key.StartsWith("windows:", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<WindowsOptimizationTarget>(
                key["windows:".Length..],
                ignoreCase: true,
                out var target))
        {
            switch (action)
            {
                case "baseline":
                    await CaptureWindowsBaselineAsync(target);
                    break;
                case "apply":
                    await ApplyWindowsOptimizationAsync(target, userAuthorized: true);
                    break;
                case "rollback":
                    if (entry.ChangeId is Guid windowsId)
                        await RollbackWindowsChangeAsync(windowsId);
                    break;
                case "measure":
                    if (entry.ChangeId is Guid measureId)
                        await EvaluateWindowsMeasurementAsync(measureId);
                    break;
            }

            await RefreshCatalogAsync();
            return;
        }

        switch (key)
        {
            case "cs2:priority":
                if (action == "rollback")
                    RollbackPriority();
                else
                    ApplyAboveNormalPriority();
                break;

            case "cs2:affinity-p":
                if (action == "rollback")
                    RollbackAffinity();
                else
                    ApplyPcoresAffinity();
                break;

            case "cs2:affinity-balanced":
                if (action == "rollback")
                    RollbackAffinity();
                else
                    ApplyBalancedAffinity();
                break;

            case "game:auto-profile":
                IsAutoGameProfileEnabled = action != "rollback";
                break;

            case "process:governance":
                await RefreshGovernanceAsync(force: true);
                break;

            default:
                CatalogStatusText =
                    $"'{entry.Name}' no tiene un ejecutor automático: {entry.StatusDetail}";
                return;
        }

        if (action == "apply" || action == "rollback")
            ActiveProfile = "PERSONALIZADO";

        await RefreshCatalogAsync();
    }

    // ── Perfiles ─────────────────────────────────────────────────────────────

    public async Task ApplyProfileAsync(string profile)
    {
        if (_disposed)
            return;

        switch (profile.ToUpperInvariant())
        {
            case "GAMING":
                ApplyAboveNormalPriority();

                if (Topology.IsHybrid)
                    ApplyPcoresAffinity();

                IsAutoGameProfileEnabled = true;
                ActiveProfile = "GAMING";
                ProfileStatusText =
                    "Perfil GAMING: prioridad AboveNormal y afinidad a P-Cores sobre los " +
                    "optimizadores existentes. Toda modificación es reversible.";
                break;

            case "MAXPERFORMANCE":
                ApplyAboveNormalPriority();

                if (Topology.IsHybrid)
                    ApplyPcoresAffinity();

                IsAutoGameProfileEnabled = true;
                ActiveProfile = "MAXPERFORMANCE";
                ProfileStatusText =
                    "Perfil MAXPERFORMANCE: prioridad y afinidad máximas permitidas " +
                    "(nunca High/Realtime). Los ajustes de Windows siguen requiriendo " +
                    "autorización individual y medición.";
                break;

            case "EVERYDAY":
                RollbackPriority();
                RollbackAffinity();
                IsAutoGameProfileEnabled = false;
                ActiveProfile = "EVERYDAY";
                ProfileStatusText =
                    "Perfil EVERYDAY: sin cambios de prioridad ni afinidad. " +
                    "Los ajustes de Windows aplicados siguen registrados en el diario.";
                break;
        }

        await RefreshCatalogAsync();
    }

    // ── Gobierno de procesos ─────────────────────────────────────────────────

    private async Task RefreshGovernanceAsync(bool force = false)
    {
        if (_disposed)
            return;

        if (!force &&
            DateTimeOffset.UtcNow - _lastGovernanceRefresh < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastGovernanceRefresh = DateTimeOffset.UtcNow;

        var latest = _monitor.GetLatest();
        if (latest is null || latest.TopProcesses.Count == 0)
            return;

        var governed = await Task.Run(() => Governance.Classify(latest.TopProcesses));

        await _dispatcher.InvokeAsync(() =>
        {
            GovernedProcesses.Clear();
            foreach (var process in governed)
                GovernedProcesses.Add(process);
        });
    }

    public async Task ThrottleProcessAsync(
        GovernedProcess process,
        bool userAuthorized)
    {
        ArgumentNullException.ThrowIfNull(process);

        var result = await Governance.ThrottleAsync(process.ProcessId, userAuthorized);

        CatalogStatusText = result.Message;

        await RefreshGovernanceAsync(force: true);
        await RefreshCatalogAsync();
    }

    public async Task RestoreProcessAsync(GovernedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var result = await Governance.RestoreAsync(process.ProcessId);

        CatalogStatusText = result.Message;

        await RefreshGovernanceAsync(force: true);
    }

    // ── Puntaje EVO y EVO AI ─────────────────────────────────────────────────

    private void UpdateEvoPerformanceScore()
    {
        var latest = _monitor.GetLatest();

        if (latest is null)
        {
            EvoPerformanceScore = null;
            EvoPerformanceScoreText = "SIN DATOS";
            EvoPerformanceBreakdownText =
                "Iniciá el monitor en vivo para calcular el puntaje con telemetría real.";
            return;
        }

        var notes = new List<string>();
        double score = 100;

        double cpuPenalty = Math.Min(15, Math.Max(0, latest.SystemCpuPercent - 70) * 0.5);
        double memPenalty = Math.Min(12, Math.Max(0, latest.MemoryUsagePercent - 80) * 0.6);
        double commitPercent = latest.CommitLimitMb > 0
            ? latest.CommitUsedMb / latest.CommitLimitMb * 100.0
            : 0;
        double commitPenalty = Math.Min(9, Math.Max(0, commitPercent - 85) * 0.6);
        double gpuPenalty = latest.GpuUsagePercent is double gpu
            ? Math.Min(8, Math.Max(0, gpu - 92))
            : 0;

        score -= cpuPenalty + memPenalty + commitPenalty + gpuPenalty;

        if (cpuPenalty > 0)
            notes.Add($"CPU {latest.SystemCpuPercent:F0}% (−{cpuPenalty:F1})");
        if (memPenalty > 0)
            notes.Add($"RAM {latest.MemoryUsagePercent:F0}% (−{memPenalty:F1})");
        if (commitPenalty > 0)
            notes.Add($"Commit {commitPercent:F0}% (−{commitPenalty:F1})");
        if (gpuPenalty > 0)
            notes.Add($"GPU {latest.GpuUsagePercent:F0}% (−{gpuPenalty:F1})");

        int unverified = CatalogAppliedCount - CatalogVerifiedCount;
        if (unverified > 0)
        {
            double penalty = Math.Min(10, unverified * 2);
            score -= penalty;
            notes.Add($"{unverified} optimización(es) sin evidencia FPS (−{penalty:F1})");
        }

        if (_hardwareSnapshot is null)
        {
            score -= 5;
            notes.Add("Diagnóstico de hardware pendiente (−5.0)");
        }

        score = Math.Clamp(score, 0, 100);
        int rounded = (int)Math.Round(score);
        EvoPerformanceScore = rounded;

        string label = rounded >= 85
            ? "EXCELENTE"
            : rounded >= 70
                ? "BUENO"
                : rounded >= 50
                    ? "MEJORABLE"
                    : "LIMITADO";

        EvoPerformanceScoreText = $"{rounded} / 100 · {label}";
        EvoPerformanceBreakdownText = notes.Count == 0
            ? "Sin penalizaciones activas. El puntaje usa CPU, RAM, commit, GPU y evidencia."
            : "Penalizaciones: " + string.Join(" · ", notes);
    }

    private void UpdateEvoAi(IReadOnlyList<OptimizationCatalogEntry> entries)
    {
        var report = EvoAiCoordinator.Analyze(new EvoAiInput(
            PrimaryBottleneck: BuildLiveContext().PrimaryBottleneck,
            CpuPercent: _monitor.GetLatest()?.SystemCpuPercent,
            GpuPercent: _monitor.GetLatest()?.GpuUsagePercent,
            MemoryUsedPercent: _monitor.GetLatest()?.MemoryUsagePercent,
            CommitUsedPercent: BuildLiveContext().CommitUsedPercent,
            Cs2Running: (Cs2ProcessCount > 0),
            HasFpsEvidence: entries.Any(x =>
                x.State == OptimizationCatalogState.ImprovementVerified),
            VerifiedOptimizations: entries.Count(x =>
                x.State == OptimizationCatalogState.ImprovementVerified),
            InconclusiveOptimizations: entries.Count(x =>
                x.State == OptimizationCatalogState.Inconclusive),
            AppliedOptimizations: entries.Count(x => x.IsApplied),
            Hardware: _hardwareSnapshot,
            Live: BuildLiveContext()));

        EvoAiSummaryText = report.Summary;

        EvoAiDecisions.Clear();
        foreach (var decision in report.Decisions)
            EvoAiDecisions.Add(decision);

        EvoAiConflicts.Clear();
        foreach (var conflict in report.Conflicts)
            EvoAiConflicts.Add(conflict);

        EvoAiWarnings.Clear();
        foreach (var warning in report.Warnings)
            EvoAiWarnings.Add(warning);
    }

    // ── Sincronización con telemetría en vivo ────────────────────────────────

    private void NotifyLiveSnapshotForHub(LivePerformanceSnapshot snapshot)
    {
        UpdateEvoPerformanceScore();
        _ = RefreshGovernanceAsync();
        UpdateMemoryPressureStatus(snapshot);

        bool cs2Running = snapshot.Cs2ProcessCount > 0;

        if (!IsAutoGameProfileEnabled)
        {
            if (!cs2Running && _autoProfileAppliedByHub)
            {
                RollbackPriority();
                RollbackAffinity();
                _autoProfileAppliedByHub = HasActiveOptimizations;
                AutoGameProfileStatusText = _autoProfileAppliedByHub
                    ? $"CS2 finalizó; rollback pendiente. {PriorityStatusText} {AffinityStatusText}"
                    : "CS2 finalizó; no quedan cambios temporales de EVO pendientes.";
            }

            return;
        }

        if (cs2Running && _activePriorityToken is null)
        {
            ApplyAboveNormalPriority();
        }

        if (cs2Running && Topology.IsHybrid && _activeAffinityToken is null)
        {
            ApplyPcoresAffinity();
        }

        if (cs2Running)
        {
            _autoProfileAppliedByHub = HasActiveOptimizations;
            AutoGameProfileStatusText = HasActiveOptimizations
                ? "CS2 detectado: cambio(s) temporal(es) aplicado(s) con la autorización del perfil."
                : "Perfil automático activo, pero no se aplicó ningún cambio de EVO. " +
                  $"{PriorityStatusText} {AffinityStatusText}";
        }

        if (!cs2Running && _autoProfileAppliedByHub)
        {
            RollbackPriority();
            RollbackAffinity();
            _autoProfileAppliedByHub = HasActiveOptimizations;
            AutoGameProfileStatusText = _autoProfileAppliedByHub
                ? $"CS2 finalizó; rollback pendiente. {PriorityStatusText} {AffinityStatusText}"
                : "CS2 finalizó; no quedan cambios temporales de EVO pendientes.";
        }
    }

    private void UpdateMemoryPressureStatus(LivePerformanceSnapshot snapshot)
    {
        MemoryPressureResult pressure = _memoryPressureEvaluator.Evaluate(
            new MemoryPressureEvidence(
                MegabytesToBytes(snapshot.TotalMemoryMb),
                MegabytesToBytes(snapshot.AvailableMemoryMb),
                MegabytesToBytes(snapshot.CommitUsedMb),
                MegabytesToBytes(snapshot.CommitLimitMb),
                SystemHardFaultsPerSecond: null));

        MemoryPressureStatusText = pressure.HasSufficientEvidence
            ? $"Presión {pressure.Level} · RAM {pressure.PhysicalMemoryUsedPercent:F1}% · " +
              $"commit {(pressure.CommitUsedPercent.HasValue ? $"{pressure.CommitUsedPercent:F1}%" : "N/D")}. " +
              "Hard faults no medidos."
            : "Presión de memoria: UNKNOWN (faltan datos válidos de RAM y commit).";
    }

    // ── Formato ──────────────────────────────────────────────────────────────

    private static string FormatInt(int? value) =>
        value?.ToString() ?? "UNKNOWN";

    private static string FormatInt(uint? value) =>
        value?.ToString() ?? "UNKNOWN";

    private static string FormatBytes(ulong? bytes) =>
        bytes is null
            ? "UNKNOWN"
            : $"{bytes.Value / 1024.0 / 1024.0 / 1024.0:F1} GB";
}