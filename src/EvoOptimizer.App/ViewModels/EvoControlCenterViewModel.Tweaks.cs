using System.Collections.ObjectModel;
using EvoOptimizer.Core.Audit;
using EvoOptimizer.Core.Tweaks;
using EvoOptimizer.Infrastructure.Audit;
using EvoOptimizer.Infrastructure.Tweaks;

namespace EvoOptimizer.App.ViewModels;

/// <summary>Fila de tweaks para el DataGrid: texto ya formateado y banderas de acción.</summary>
public sealed record TweakRow(
    string Id,
    string Name,
    string Description,
    string CategoryLabel,
    string StateLabel,
    string StateColor,
    string RiskLabel,
    string Benefit,
    string RegistryLocation,
    string CurrentValueLabel,
    string DesiredLabel,
    string ElevationLabel,
    string RebootLabel,
    string EvidenceSource,
    string MeasuredAtText,
    bool CanApply,
    bool CanRollback)
{
    public bool IsApplied => StateLabel == "APLICADO";

    public bool IsBlocked => StateLabel == "BLOQUEADO POR POLÍTICA EVO";
}

/// <summary>Fila de debloat para el DataGrid.</summary>
public sealed record DebloatRow(
    string Id,
    string Name,
    string PackageLabel,
    string StateLabel,
    string StateColor,
    string RiskLabel,
    string Reason,
    string Benefit,
    string ReinstallHint,
    string VersionLabel,
    string ScannedAtLabel,
    bool CanRemove,
    bool CanRestore);

/// <summary>
/// Escáner de especificaciones + motor de tweaks (FPS/latencia/registro) y
/// debloat UWP. Reutiliza WindowsSystemAuditor para el escaneo y los servicios
/// nuevos de Infrastructure para escribir; toda escritura pasa por su diario
/// y exige autorización explícita del usuario.
/// </summary>
public sealed partial class EvoControlCenterViewModel
{
    private RegistryTweakService? _tweakService;
    private DebloatService? _debloatService;
    private SystemAuditReport? _lastScannerReport;

    private readonly HashSet<string> _activeTweakIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedDebloatIds = new(StringComparer.OrdinalIgnoreCase);

    private string _tweakSearchText = string.Empty;
    private string _selectedTweakCategory = "TODAS";
    private string _tweakStatusText =
        "TWEAKS SIN ESCANEO: EVO no muestra estados inventados. Ejecutá ESCANEAR TWEAKS.";
    private bool _isScanningTweaks;

    private string _debloatStatusText =
        "DEBLOAT SIN ESCANEO: no se asume qué apps están instaladas hasta leer Get-AppxPackage.";
    private bool _isScanningDebloat;
    private DateTimeOffset? _debloatScannedAt;

    private string _scannerStatusText =
        "ESCÁNER SIN EJECUTAR: las especificaciones reales del equipo aparecen tras el escaneo.";
    private bool _isRunningScanner;
    private DateTimeOffset? _scannerAt;
    private string _scannerCpuText = "CPU: UNKNOWN";
    private string _scannerGpuText = "GPU: UNKNOWN";
    private string _scannerMemoryText = "MEMORIA: UNKNOWN";
    private string _scannerStorageText = "ALMACENAMIENTO: UNKNOWN";
    private string _scannerDisplayText = "PANTALLA: UNKNOWN";
    private string _scannerOsText = "WINDOWS: UNKNOWN";
    private string _scannerTopologyText = "TOPOLOGÍA: UNKNOWN";
    private string _scannerBriefText =
        "Brief original: i3-1220P · Intel UHD · 16 GB DDR5-5600 · 500 GB · 1080p 200 Hz. " +
        "El escáner compara lo declarado con lo realmente detectado.";

    public ObservableCollection<TweakRow> TweakView { get; } = new();

    public ObservableCollection<string> TweakCategories { get; } = new();

    public ObservableCollection<DebloatRow> DebloatView { get; } = new();

    public ObservableCollection<HardwareFactRow> ScannerRows { get; } = new();

    private RegistryTweakService TweakService =>
        _tweakService ??= new RegistryTweakService();

    private DebloatService DebloatServiceInstance =>
        _debloatService ??= new DebloatService();

    public string TweakSearchText
    {
        get => _tweakSearchText;
        set
        {
            if (SetField(ref _tweakSearchText, value))
            {
                RebuildTweakView();
            }
        }
    }

    public string SelectedTweakCategory
    {
        get => _selectedTweakCategory;
        set
        {
            if (SetField(ref _selectedTweakCategory, value))
            {
                RebuildTweakView();
            }
        }
    }

    public string TweakStatusText
    {
        get => _tweakStatusText;
        private set => SetField(ref _tweakStatusText, value);
    }

    public bool IsScanningTweaks
    {
        get => _isScanningTweaks;
        private set
        {
            if (SetField(ref _isScanningTweaks, value))
            {
                OnPropertyChanged(nameof(CanScanTweaks));
            }
        }
    }

    public bool CanScanTweaks => !IsScanningTweaks && !_disposed;

    public int TweakTotalCount => TweakView.Count;

    public int TweakAppliedCount => TweakView.Count(row => row.IsApplied);

    public int TweakAvailableCount => TweakView.Count(row => row.CanApply);

    public int TweakBlockedCount => TweakView.Count(row => row.IsBlocked);

    public string TweakCountsText =>
        $"Catálogo: {RegistryTweakDefinitions.All.Count} tweaks de registro · " +
        $"visibles: {TweakView.Count} · aplicados: {TweakAppliedCount} · " +
        $"disponibles: {TweakAvailableCount} · bloqueados por política: {TweakBlockedCount}";

    public string DebloatStatusText
    {
        get => _debloatStatusText;
        private set => SetField(ref _debloatStatusText, value);
    }

    public bool IsScanningDebloat
    {
        get => _isScanningDebloat;
        private set
        {
            if (SetField(ref _isScanningDebloat, value))
            {
                OnPropertyChanged(nameof(CanScanDebloat));
            }
        }
    }

    public bool CanScanDebloat => !IsScanningDebloat && !_disposed;

    public int DebloatInstalledCount => DebloatView.Count(row => row.CanRemove);

    public int DebloatRemovedCount => DebloatView.Count(row => row.CanRestore);

    public string DebloatCountsText =>
        $"Catálogo: {DebloatCatalog.RemovableApps.Count} apps evaluables · " +
        $"visibles: {DebloatView.Count} · quitables: {DebloatInstalledCount} · " +
        $"quitadas por EVO: {DebloatRemovedCount}";

    public string ScannerStatusText
    {
        get => _scannerStatusText;
        private set => SetField(ref _scannerStatusText, value);
    }

    public bool IsRunningScanner
    {
        get => _isRunningScanner;
        private set
        {
            if (SetField(ref _isRunningScanner, value))
            {
                OnPropertyChanged(nameof(CanRunScanner));
            }
        }
    }

    public bool CanRunScanner => !IsRunningScanner && !_disposed;

    public string ScannerAtText => _scannerAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        ?? "Sin escaneo";

    public string ScannerCpuText
    {
        get => _scannerCpuText;
        private set => SetField(ref _scannerCpuText, value);
    }

    public string ScannerGpuText
    {
        get => _scannerGpuText;
        private set => SetField(ref _scannerGpuText, value);
    }

    public string ScannerMemoryText
    {
        get => _scannerMemoryText;
        private set => SetField(ref _scannerMemoryText, value);
    }

    public string ScannerStorageText
    {
        get => _scannerStorageText;
        private set => SetField(ref _scannerStorageText, value);
    }

    public string ScannerDisplayText
    {
        get => _scannerDisplayText;
        private set => SetField(ref _scannerDisplayText, value);
    }

    public string ScannerOsText
    {
        get => _scannerOsText;
        private set => SetField(ref _scannerOsText, value);
    }

    public string ScannerTopologyText
    {
        get => _scannerTopologyText;
        private set => SetField(ref _scannerTopologyText, value);
    }

    public string ScannerBriefText
    {
        get => _scannerBriefText;
        private set => SetField(ref _scannerBriefText, value);
    }

    // ── Escáner de especificaciones ──────────────────────────────────────────

    public async Task RunScannerAsync()
    {
        if (IsRunningScanner || _disposed)
        {
            return;
        }

        IsRunningScanner = true;
        ScannerStatusText = "ESCANEANDO EQUIPO · consultas CIM de solo lectura (PowerShell 5.1)…";

        try
        {
            SystemAuditReport report = await Auditor.CaptureAsync();

            _lastScannerReport = report;

            FillScannerRows(report);
            FillScannerSummary(report);

            _scannerAt = report.CapturedAt;

            ScannerStatusText =
                $"Escaneo completo: {report.Facts.Count} hechos leídos de CIM · " +
                $"{ScannerAtText}. Nada del escáner modifica el sistema.";

            if (report.Warnings.Count > 0)
            {
                ScannerStatusText += $" Avisos: {report.Warnings.Count} " +
                    "(los datos no disponibles figuran como UNKNOWN).";
            }
        }
        catch (OperationCanceledException)
        {
            ScannerStatusText = "Escáner cancelado. No se modificó nada.";
        }
        catch (Exception exception)
        {
            ScannerStatusText = $"Escáner falló: {exception.Message} " +
                "El estado del hardware queda UNKNOWN hasta reintentar.";
        }
        finally
        {
            IsRunningScanner = false;
            OnPropertyChanged(nameof(ScannerAtText));
        }
    }

    private void FillScannerRows(SystemAuditReport report)
    {
        ScannerRows.Clear();

        string stamp = report.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        string[] hardwareCategories =
        {
            "Equipo", "Placa madre", "BIOS", "CPU", "GPU", "RAM", "Arreglo de memoria",
            "Windows", "Discos", "Disco físico", "Particiones", "Volúmenes",
            "SMART predictivo", "Almacenamiento", "Contadores de confiabilidad",
            "TRIM", "Plan de energía", "Red", "Servicios"
        };

        foreach (string category in hardwareCategories)
        {
            foreach (AuditFact fact in report.Facts.Where(fact => fact.Category == category))
            {
                ScannerRows.Add(new HardwareFactRow(
                    fact.Category,
                    fact.Name,
                    string.IsNullOrWhiteSpace(fact.Value) ? "UNKNOWN" : fact.Value!,
                    fact.EvidenceSource,
                    fact.Confidence,
                    stamp));
            }
        }

        AuditFact[] serviceStartModes = report.Facts
            .Where(fact =>
                fact.Category == "Servicios" &&
                fact.Name.EndsWith("StartMode", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        int services = serviceStartModes.Count(fact =>
            string.Equals(fact.Value, "Auto", StringComparison.OrdinalIgnoreCase));
        int startup = report.Facts.Count(fact => fact.Category == "Inicio");

        ScannerRows.Add(new HardwareFactRow(
            "Resumen",
            "Servicios inventariados",
            serviceStartModes.Length > 0 ? serviceStartModes.Length.ToString() : "UNKNOWN",
            "Win32_Service",
            serviceStartModes.Length > 0 ? "SYSTEM_REPORTED" : "UNKNOWN",
            stamp));

        ScannerRows.Add(new HardwareFactRow(
            "Resumen",
            "Servicios automáticos detectados",
            services > 0 ? services.ToString() : "UNKNOWN",
            "Win32_Service (StartMode=Auto)",
            services > 0 ? "SYSTEM_REPORTED" : "UNKNOWN",
            stamp));

        ScannerRows.Add(new HardwareFactRow(
            "Resumen",
            "Entradas de inicio detectadas",
            startup > 0 ? startup.ToString() : "UNKNOWN",
            "Win32_StartupCommand",
            startup > 0 ? "SYSTEM_REPORTED" : "UNKNOWN",
            stamp));
    }

    private void FillScannerSummary(SystemAuditReport report)
    {
        string? cpuName = Fact(report, "CPU", "Name");
        string? cores = Fact(report, "CPU", "NumberOfCores");
        string? threads = Fact(report, "CPU", "NumberOfLogicalProcessors");
        string? maxClock = Fact(report, "CPU", "MaxClockSpeed");

        ScannerCpuText = cpuName is null
            ? "CPU: UNKNOWN"
            : $"CPU: {cpuName} · {cores ?? "?"} núcleos / {threads ?? "?"} hilos · máx {maxClock ?? "?"} MHz";

        string? gpuName = Fact(report, "GPU", "Name");
        string? driver = Fact(report, "GPU", "DriverVersion");
        string? width = Fact(report, "GPU", "CurrentHorizontalResolution");
        string? height = Fact(report, "GPU", "CurrentVerticalResolution");
        string? refresh = Fact(report, "GPU", "CurrentRefreshRate");

        ScannerGpuText = gpuName is null
            ? "GPU: UNKNOWN"
            : $"GPU: {gpuName} · driver {driver ?? "UNKNOWN"}";

        ScannerDisplayText = width is null || height is null
            ? "PANTALLA: UNKNOWN (resolución no informada por CIM)"
            : $"PANTALLA: {width}×{height} @ {refresh ?? "?"} Hz";

        string? totalRam = Fact(report, "Equipo", "TotalPhysicalMemory");
        string ramGb = TryParseUnsigned(totalRam, out ulong bytes)
            ? $"{bytes / 1024d / 1024d / 1024d:F1} GB"
            : "UNKNOWN";

        int modules = report.Facts
            .Where(fact => fact.Category == "RAM" && fact.Name.EndsWith("] Capacity", StringComparison.Ordinal))
            .Count(fact => !string.IsNullOrWhiteSpace(fact.Value));

        string? speed = Fact(report, "RAM", "ConfiguredClockSpeed");
        string? memoryType = Fact(report, "RAM", "SMBIOSMemoryType");

        ScannerMemoryText = $"MEMORIA: {ramGb} · {modules} módulo(s) declarado(s) · " +
            $"{memoryType ?? "tipo UNKNOWN"} · {speed ?? "velocidad UNKNOWN"} MHz";

        string? diskSize = Fact(report, "Disco físico", "Size");
        string? freeSpace = Fact(report, "Discos", "FreeSpace");
        string? diskModel = Fact(report, "Disco físico", "Model");
        string? diskInterface = Fact(report, "Disco físico", "InterfaceType");

        ScannerStorageText = TryParseUnsigned(diskSize, out ulong diskBytes)
            ? $"ALMACENAMIENTO: {diskBytes / 1000d / 1000d / 1000d:F0} GB · {diskModel ?? "modelo UNKNOWN"} · " +
              $"{diskInterface ?? "interfaz UNKNOWN"} · libre {FormatMaybeGb(freeSpace)}"
            : $"ALMACENAMIENTO: modelo {diskModel ?? "UNKNOWN"} · capacidad UNKNOWN";

        string? caption = Fact(report, "Windows", "Caption");
        string? build = Fact(report, "Windows", "BuildNumber");
        string? osVersion = Fact(report, "Windows", "Version");

        ScannerOsText = caption is null
            ? "WINDOWS: UNKNOWN"
            : $"WINDOWS: {caption} · {osVersion ?? "versión UNKNOWN"} (build {build ?? "UNKNOWN"})";

        string? topology = Fact(report, "Equipo", "SystemType");
        string? board = Fact(report, "Placa madre", "Manufacturer");
        string? bios = Fact(report, "BIOS", "SMBIOSBIOSVersion");

        ScannerTopologyText = $"EQUIPO: {topology ?? "UNKNOWN"} · placa {board ?? "UNKNOWN"} · BIOS {bios ?? "UNKNOWN"}";

        ScannerBriefText =
            "Brief original: i3-1220P (12ª gen) · Intel UHD · 16 GB DDR5-5600 · 500 GB · 1080p 200 Hz. " +
            $"Detectado ahora: CPU {cores ?? "?"}c/{threads ?? "?"}t · RAM {ramGb} · pantalla " +
            $"{width ?? "?"}×{height ?? "?"} @ {refresh ?? "?"} Hz. " +
            "EVO muestra lo medido, no lo declarado: si algo no aparece, figura como UNKNOWN.";
    }

    private static string? Fact(SystemAuditReport report, string category, string suffix)
    {
        return report.Facts
            .Where(fact => fact.Category == category &&
                (fact.Name == suffix ||
                 fact.Name.EndsWith("] " + suffix, StringComparison.Ordinal)) &&
                !string.IsNullOrWhiteSpace(fact.Value))
            .Select(fact => fact.Value)
            .FirstOrDefault();
    }

    private static bool TryParseUnsigned(string? text, out ulong value)
    {
        value = 0;

        return !string.IsNullOrWhiteSpace(text) &&
            ulong.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string FormatMaybeGb(string? bytes) =>
        TryParseUnsigned(bytes, out ulong value)
            ? $"{value / 1024d / 1024d / 1024d:F1} GB"
            : "UNKNOWN";

    /// <summary>
    /// Exporta el último escaneo como informe JSON + texto legible. Es escritura
    /// en la carpeta de EVO (no en el sistema) y no cambia ninguna configuración.
    /// </summary>
    public async Task<string> ExportScannerReportAsync()
    {
        if (_lastScannerReport is null)
        {
            return "No hay escaneo exportable todavía: ejecutá ESCANEAR EQUIPO primero.";
        }

        try
        {
            (string jsonPath, string textPath) = await WindowsSystemAuditor.ExportAsync(_lastScannerReport);

            return $"Informe exportado (solo lectura, sin cambios en el sistema):\n\n" +
                $"JSON: {jsonPath}\nTEXTO: {textPath}";
        }
        catch (Exception exception)
        {
            return $"No se pudo exportar el informe: {exception.Message}";
        }
    }

    // ── Tweaks de registro ───────────────────────────────────────────────────

    public void InitializeTweaksState()
    {
        TweakCategories.Clear();
        TweakCategories.Add("TODAS");

        foreach (string label in RegistryTweakDefinitions.All
            .Select(definition => definition.CategoryLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            TweakCategories.Add(label);
        }

        RebuildTweakView();
    }

    public async Task ScanTweaksAsync()
    {
        if (IsScanningTweaks || _disposed)
        {
            return;
        }

        IsScanningTweaks = true;
        TweakStatusText = "LEYENDO REGISTRO · HKCU directo, HKLM en vista 64 bits (solo lectura)…";

        try
        {
            IReadOnlyList<RegistryTweakStatus> statuses = await TweakService.ScanAsync();

            _activeTweakIds.Clear();

            foreach (TweakChange change in await TweakService.Journal.GetActiveAsync())
            {
                _activeTweakIds.Add(change.DefinitionId);
            }

            _tweakStatuses = statuses;

            RebuildTweakView();

            int unknown = statuses.Count(status => status.State == TweakState.Unknown);
            int blocked = statuses.Count(status => status.State == TweakState.Blocked);

            TweakStatusText =
                $"Escaneo de tweaks: {statuses.Count} leídos · {TweakAppliedCount} aplicados · " +
                $"{TweakAvailableCount} disponibles · {blocked} bloqueados por política · " +
                $"{unknown} UNKNOWN.";

            if (unknown > 0)
            {
                TweakStatusText += " Los UNKNOWN no se aplican: EVO no escribe sobre estado desconocido.";
            }

            if (RegistryTweakService.IsCurrentProcessElevated())
            {
                TweakStatusText += " Proceso elevado: los tweaks HKLM pueden aplicarse sin nuevo UAC.";
            }
        }
        catch (OperationCanceledException)
        {
            TweakStatusText = "Escaneo de tweaks cancelado.";
        }
        catch (Exception exception)
        {
            TweakStatusText = $"No se pudo escanear el registro: {exception.Message}";
        }
        finally
        {
            IsScanningTweaks = false;
        }
    }

    private IReadOnlyList<RegistryTweakStatus> _tweakStatuses = Array.Empty<RegistryTweakStatus>();

    private void RebuildTweakView()
    {
        string search = _tweakSearchText?.Trim() ?? string.Empty;

        IEnumerable<TweakRow> rows = _tweakStatuses.Select(status => new TweakRow(
            status.Definition.Id,
            status.Definition.Name,
            status.Definition.Description,
            status.CategoryLabel,
            status.StateLabel,
            status.StateColor,
            status.Definition.RiskLabel,
            status.Definition.ExpectedBenefit,
            status.Definition.RegistryLocation,
            status.CurrentValueLabel,
            $"Deseado: {status.Definition.DesiredValue} " +
                $"({(status.Definition.ValueKind == RegistryValueChoice.Dword ? "DWORD" : "SZ")})",
            status.ElevationLabel,
            status.RebootLabel,
            status.EvidenceSource,
            status.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            status.CanApply,
            _activeTweakIds.Contains(status.Definition.Id)));

        if (!string.Equals(_selectedTweakCategory, "TODAS", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.Where(row =>
                string.Equals(row.CategoryLabel, _selectedTweakCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (search.Length > 0)
        {
            rows = rows.Where(row =>
                row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.RegistryLocation.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.StateLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        TweakView.Clear();

        foreach (TweakRow row in rows.OrderBy(row => row.CategoryLabel)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
        {
            TweakView.Add(row);
        }

        RaiseTweakCounts();
    }

    private void RaiseTweakCounts()
    {
        OnPropertyChanged(nameof(TweakTotalCount));
        OnPropertyChanged(nameof(TweakAppliedCount));
        OnPropertyChanged(nameof(TweakAvailableCount));
        OnPropertyChanged(nameof(TweakBlockedCount));
        OnPropertyChanged(nameof(TweakCountsText));
    }

    public async Task<string> ApplyTweakAsync(string definitionId)
    {
        RegistryTweakService.TweakOperationResult result =
            await TweakService.ApplyAsync(definitionId, userAuthorized: true);

        await RescanTweaksQuietlyAsync();

        return result.Message;
    }

    public async Task<string> RollbackTweakAsync(string definitionId)
    {
        RegistryTweakService.TweakOperationResult result =
            await TweakService.RollbackAsync(definitionId);

        await RescanTweaksQuietlyAsync();

        return result.Message;
    }

    public async Task<string> ApplyAllLowRiskTweaksAsync()
    {
        IReadOnlyList<RegistryTweakService.TweakOperationResult> results =
            await TweakService.ApplyLowRiskAsync(userAuthorized: true);

        await RescanTweaksQuietlyAsync();

        int applied = results.Count(result => result.Success);

        return applied == 0
            ? "No hubo tweaks de riesgo bajo disponibles para aplicar (o todos requirieron verificación previa)."
            : $"Se aplicaron {applied} de {results.Count} tweaks de riesgo bajo. " +
              "Cada uno quedó registrado con su valor original en el diario.";
    }

    public async Task<string> RollbackAllTweaksAsync()
    {
        IReadOnlyList<RegistryTweakService.TweakOperationResult> results =
            await TweakService.RollbackAllAsync();

        await RescanTweaksQuietlyAsync();

        int reverted = results.Count(result => result.Success);

        return reverted == 0
            ? "No había cambios de EVO que revertir en el diario de tweaks."
            : $"Se revirtieron {reverted} de {results.Count} cambios registrados por EVO.";
    }

    private async Task RescanTweaksQuietlyAsync()
    {
        try
        {
            _tweakStatuses = await TweakService.ScanAsync();

            _activeTweakIds.Clear();

            foreach (TweakChange change in await TweakService.Journal.GetActiveAsync())
            {
                _activeTweakIds.Add(change.DefinitionId);
            }

            RebuildTweakView();
        }
        catch (Exception exception)
        {
            TweakStatusText = $"La relectura posterior falló: {exception.Message}";
        }
    }

    // ── Debloat UWP ──────────────────────────────────────────────────────────

    public async Task ScanDebloatAsync()
    {
        if (IsScanningDebloat || _disposed)
        {
            return;
        }

        IsScanningDebloat = true;
        DebloatStatusText = "ESCANEANDO PAQUETES · Get-AppxPackage del usuario actual…";

        try
        {
            DebloatService.DebloatScanResult scan = await DebloatServiceInstance.ScanAsync();

            _debloatStatuses = scan.Statuses;
            _debloatScannedAt = scan.ScannedAt;

            _removedDebloatIds.Clear();

            foreach (DebloatChange change in await DebloatServiceInstance.Journal.GetActiveAsync())
            {
                _removedDebloatIds.Add(change.DefinitionId);
            }

            RebuildDebloatView();

            DebloatStatusText = DebloatService.DescribeScan(scan);
        }
        catch (OperationCanceledException)
        {
            DebloatStatusText = "Escaneo de debloat cancelado.";
        }
        catch (Exception exception)
        {
            DebloatStatusText = $"No se pudo escanear Get-AppxPackage: {exception.Message}. " +
                "El debloat queda UNKNOWN.";
        }
        finally
        {
            IsScanningDebloat = false;
        }
    }

    private IReadOnlyList<DebloatAppStatus> _debloatStatuses = Array.Empty<DebloatAppStatus>();

    private void RebuildDebloatView()
    {
        DebloatView.Clear();

        foreach (DebloatAppStatus status in _debloatStatuses
            .Where(status => status.State != DebloatState.NotInstalled)
            .OrderBy(status => status.StateLabel)
            .ThenBy(status => status.Name, StringComparer.OrdinalIgnoreCase))
        {
            DebloatView.Add(new DebloatRow(
                status.Id,
                status.Name,
                status.PackageLabel,
                status.StateLabel,
                status.StateColor,
                status.RiskLabel,
                status.Definition.Reason,
                status.Definition.ExpectedBenefit,
                status.Definition.ReinstallHint,
                status.VersionLabel,
                status.ScannedAtLabel,
                status.CanRemove,
                _removedDebloatIds.Contains(status.Id)));
        }

        RaiseDebloatCounts();
    }

    private void RaiseDebloatCounts()
    {
        OnPropertyChanged(nameof(DebloatInstalledCount));
        OnPropertyChanged(nameof(DebloatRemovedCount));
        OnPropertyChanged(nameof(DebloatCountsText));
    }

    public async Task<string> RemoveDebloatAppAsync(string definitionId)
    {
        DebloatService.DebloatOperationResult result =
            await DebloatServiceInstance.RemoveAsync(definitionId, userAuthorized: true);

        await ScanDebloatQuietlyAsync();

        return result.Message;
    }

    public async Task<string> RestoreDebloatAppAsync(string definitionId)
    {
        DebloatService.DebloatOperationResult result =
            await DebloatServiceInstance.RestoreAsync(definitionId);

        await ScanDebloatQuietlyAsync();

        return result.Message;
    }

    private async Task ScanDebloatQuietlyAsync()
    {
        try
        {
            DebloatService.DebloatScanResult scan = await DebloatServiceInstance.ScanAsync();

            _debloatStatuses = scan.Statuses;
            _debloatScannedAt = scan.ScannedAt;

            _removedDebloatIds.Clear();

            foreach (DebloatChange change in await DebloatServiceInstance.Journal.GetActiveAsync())
            {
                _removedDebloatIds.Add(change.DefinitionId);
            }

            RebuildDebloatView();

            DebloatStatusText = DebloatService.DescribeScan(scan);
        }
        catch (Exception exception)
        {
            DebloatStatusText = $"Relectura del escaneo falló: {exception.Message}";
        }
    }

    public string DebloatScannedAtText => _debloatScannedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        ?? "Sin escaneo";

    public string TweakJournalHint =>
        "Diario de tweaks: %LOCALAPPDATA%\\EvoFpsOptimizer\\tweaks-changes.json · " +
        "Diario de debloat: %LOCALAPPDATA%\\EvoFpsOptimizer\\debloat-changes.json";

    public int TotalTweakCatalogSize =>
        RegistryTweakDefinitions.All.Count + DebloatCatalog.RemovableApps.Count;
}
