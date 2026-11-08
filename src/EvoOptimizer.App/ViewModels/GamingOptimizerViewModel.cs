using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using EvoOptimizer.Core.Gaming;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Gaming;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App.ViewModels;

/// <summary>
/// EVO GAMING OPTIMIZER (Módulo 1): detección real del entorno gaming,
/// biblioteca de juegos instalados (Steam/Epic/manual), perfiles inteligentes
/// con recomendaciones basadas en evidencia y detalle de qué cambiará cada
/// perfil. Es de solo lectura: la aplicación de cambios se delega al Game
/// Booster con autorización explícita por acción.
/// </summary>
public sealed class GamingOptimizerViewModel : INotifyPropertyChanged
{
    private readonly GameLibraryScanner _scanner;
    private readonly WindowsGamingConfigService _configService;
    private readonly IntelHybridTopologyProvider _topologyProvider;
    private readonly LivePerformanceMonitor _monitor;

    private bool _isBusy;
    private string _stateText = "PREPARADO";
    private string _cpuSummary = "Sin analizar";
    private string _memorySummary = "Sin analizar";
    private string _gpuSummary = "Sin analizar";
    private string _storageSummary = "Sin analizar";
    private string _powerSummary = "Sin analizar";
    private string _gamingConfigSummary = "Sin analizar";
    private string _backgroundSummary = "Sin analizar";
    private string _recommendationText = "Ejecutá el análisis para obtener una recomendación basada en tu hardware.";
    private string _plannedChangesText = "Seleccioná un perfil para ver exactamente qué cambiaría.";
    private string _selectedGameStatus = "Ningún juego seleccionado.";
    private InstalledGame? _selectedGame;
    private SmartGamingProfile? _selectedProfile;
    private bool _profileChosenByUser;
    private GamingEnvironmentSnapshot? _environment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GamingOptimizerViewModel(
        GameLibraryScanner? scanner = null,
        WindowsGamingConfigService? configService = null,
        IntelHybridTopologyProvider? topologyProvider = null,
        LivePerformanceMonitor? monitor = null)
    {
        _scanner = scanner ?? new GameLibraryScanner();
        _configService = configService ?? new WindowsGamingConfigService();
        _topologyProvider = topologyProvider ?? new IntelHybridTopologyProvider();
        _monitor = monitor ?? new LivePerformanceMonitor();
    }

    public ObservableCollection<InstalledGame> Games { get; } = new();

    public ObservableCollection<string> ScanNotes { get; } = new();

    public ObservableCollection<GamingRecommendation> Recommendations { get; } = new();

    public IReadOnlyList<SmartGamingProfile> Profiles => SmartGamingProfiles.All;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                OnPropertyChanged(nameof(CanAnalyze));
        }
    }

    public bool CanAnalyze => !IsBusy;

    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
    }

    public string CpuSummary
    {
        get => _cpuSummary;
        private set => SetField(ref _cpuSummary, value);
    }

    public string MemorySummary
    {
        get => _memorySummary;
        private set => SetField(ref _memorySummary, value);
    }

    public string GpuSummary
    {
        get => _gpuSummary;
        private set => SetField(ref _gpuSummary, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => SetField(ref _storageSummary, value);
    }

    public string PowerSummary
    {
        get => _powerSummary;
        private set => SetField(ref _powerSummary, value);
    }

    public string GamingConfigSummary
    {
        get => _gamingConfigSummary;
        private set => SetField(ref _gamingConfigSummary, value);
    }

    public string BackgroundSummary
    {
        get => _backgroundSummary;
        private set => SetField(ref _backgroundSummary, value);
    }

    public string RecommendationText
    {
        get => _recommendationText;
        private set => SetField(ref _recommendationText, value);
    }

    public string PlannedChangesText
    {
        get => _plannedChangesText;
        private set => SetField(ref _plannedChangesText, value);
    }

    public string SelectedGameStatus
    {
        get => _selectedGameStatus;
        private set => SetField(ref _selectedGameStatus, value);
    }

    public InstalledGame? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                UpdateSelectedGameStatus();
                UpdatePlannedChanges();
            }
        }
    }

    public SmartGamingProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
            {
                _profileChosenByUser = true;
                UpdateRecommendations();
                UpdatePlannedChanges();
            }
        }
    }

    // ── Análisis ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshEnvironmentAsync(cancellationToken);
        await ScanGamesAsync(cancellationToken);
    }

    public async Task RefreshEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StateText = "ANALIZANDO";

        try
        {
            CpuTopologyInfo topology = _topologyProvider.DiscoverTopology();
            WindowsGamingConfig config = _configService.ReadConfiguration();
            LivePerformanceSnapshot? snapshot = await WaitForSnapshotAsync(cancellationToken);

            CpuSummary = topology.IsHybrid
                ? $"{topology.ProcessorName} · Híbrida: {topology.PerformanceCores} P-Cores " +
                  $"({topology.PerformanceLogicalProcessors} hilos) + {topology.EfficientCores} E-Cores · " +
                  $"{topology.LogicalProcessors} procesadores lógicos · " +
                  $"Topología {(topology.IsTopologyReliable ? "confiable" : "NO confiable")}"
                : $"{topology.ProcessorName} · {topology.TotalPhysicalCores} núcleos · " +
                  $"{topology.LogicalProcessors} procesadores lógicos · No híbrida";

            if (snapshot is not null)
            {
                MemorySummary =
                    $"{snapshot.TotalMemoryMb / 1024:F1} GB totales · " +
                    $"{snapshot.AvailableMemoryMb / 1024:F1} GB disponibles · " +
                    $"Uso {snapshot.MemoryUsagePercent:F0}%";

                GpuSummary = snapshot.GpuUsagePercent is double gpu
                    ? $"Uso GPU: {gpu:F0}%" +
                      (snapshot.GpuSharedMemoryMb is double shared
                          ? $" · Memoria compartida: {shared:F0} MB"
                          : string.Empty)
                    : "Sin telemetría de GPU disponible en este equipo. EVO no infiere valores.";

                BackgroundSummary =
                    $"{snapshot.ActiveProcessCount} procesos activos · " +
                    $"CPU del sistema: {snapshot.SystemCpuPercent:F1}%";
            }
            else
            {
                MemorySummary = "Sin muestra de telemetría disponible. No se muestran valores asumidos.";
                GpuSummary = "Sin telemetría de GPU disponible.";
                BackgroundSummary = "Sin muestra de telemetría disponible.";
            }

            StorageSummary = BuildStorageSummary();

            PowerSummary =
                $"Plan activo: {config.PowerPlanName} · " +
                $"Alimentación: {(config.IsOnAcPower is bool ac ? (ac ? "AC (corriente)" : "Batería") : "no determinada")} · " +
                $"Plan Alto rendimiento: {(config.HasHighPerformancePlan ? "disponible" : "no expuesto por Windows")}";

            GamingConfigSummary =
                $"Modo Juego: {(config.GameModeEnabled ? "Activo" : "Desactivado")} · " +
                $"GameDVR: {(config.GameDvrEnabled ? "Activo (consume recursos)" : "Desactivado")} · " +
                $"HAGS: {(config.HagsEnabled ? "Activo" : "Desactivado")}";

            _environment = new GamingEnvironmentSnapshot(
                topology.IsHybrid,
                topology.IsTopologyReliable,
                topology.PerformanceCores,
                topology.LogicalProcessors,
                snapshot?.TotalMemoryMb,
                snapshot?.AvailableMemoryMb,
                config.GameDvrEnabled,
                config.GameModeEnabled,
                config.IsOnAcPower,
                config.HasHighPerformancePlan,
                WindowsGamingConfigService.IsBalancedPowerPlanName(config.PowerPlanName),
                config.PowerPlanName);

            var (recommended, rationale) = GamingRecommendationEngine.RecommendProfile(_environment);
            RecommendationText = $"PERFIL RECOMENDADO: {SmartGamingProfiles.Get(recommended).Name}. {rationale}";

            if (!_profileChosenByUser || SelectedProfile is null)
            {
                _profileChosenByUser = false;
                SetField(ref _selectedProfile, SmartGamingProfiles.Get(recommended), nameof(SelectedProfile));
            }

            UpdateRecommendations();
            UpdatePlannedChanges();
            StateText = "LISTO";
        }
        catch (Exception ex)
        {
            StateText = "ERROR";
            RecommendationText = $"Error analizando el entorno: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ScanGamesAsync(CancellationToken cancellationToken = default)
    {
        StateText = "ANALIZANDO";

        try
        {
            GameLibraryScanResult result = await _scanner.ScanAsync(cancellationToken);
            string? selectedId = SelectedGame?.Id;

            Games.Clear();
            foreach (InstalledGame game in result.Games)
                Games.Add(game);

            ScanNotes.Clear();
            foreach (string note in result.Notes)
                ScanNotes.Add(note);

            SelectedGame = Games.FirstOrDefault(game => game.Id == selectedId) ?? Games.FirstOrDefault();
            StateText = "LISTO";
        }
        catch (Exception ex)
        {
            StateText = "ERROR";
            ScanNotes.Add($"Error detectando juegos: {ex.Message}");
        }
    }

    public async Task<InstalledGame?> AddManualGameAsync(
        string executablePath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        InstalledGame? game = await _scanner.AddManualGameAsync(
            executablePath, displayName, cancellationToken);

        if (game is not null)
        {
            await ScanGamesAsync(cancellationToken);
            SelectedGame = Games.FirstOrDefault(existing => existing.Id == game.Id) ?? SelectedGame;
        }

        return game;
    }

    public async Task<bool> RemoveSelectedManualGameAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is not { Launcher: GameLauncher.Manual } game)
            return false;

        bool removed = await _scanner.RemoveManualGameAsync(game.Id, cancellationToken);

        if (removed)
            await ScanGamesAsync(cancellationToken);

        return removed;
    }

    // ── Estado derivado ──────────────────────────────────────────────────────

    private void UpdateSelectedGameStatus()
    {
        if (SelectedGame is null)
        {
            SelectedGameStatus = "Ningún juego seleccionado.";
            return;
        }

        bool running = IsGameRunning(SelectedGame);
        string executable = SelectedGame.ExecutablePath ?? "ejecutable no resuelto (elegilo manualmente)";

        SelectedGameStatus =
            $"{SelectedGame.Name} · {SelectedGame.Launcher} · " +
            $"{(running ? "EN EJECUCIÓN" : "detenido")} · {executable}";
    }

    public static bool IsGameRunning(InstalledGame game)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(game.ExecutableName);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateRecommendations()
    {
        Recommendations.Clear();

        if (_environment is null || SelectedProfile is null)
            return;

        foreach (GamingRecommendation recommendation in
            GamingRecommendationEngine.Evaluate(_environment, SelectedProfile))
        {
            Recommendations.Add(recommendation);
        }
    }

    private void UpdatePlannedChanges()
    {
        if (SelectedProfile is null)
        {
            PlannedChangesText = "Seleccioná un perfil para ver exactamente qué cambiaría.";
            return;
        }

        SmartGamingProfile profile = SelectedProfile;
        var lines = new List<string>
        {
            $"PERFIL {profile.Name} — cambios propuestos (nada se aplica sin autorización):",
            $"• Prioridad del juego → {profile.GamePriority} (nunca High ni Realtime)."
        };

        if (_environment is not null)
        {
            GamingAffinityMode? affinity = SmartGamingProfiles.ResolveAffinity(
                profile,
                _environment.IsHybridTopology,
                _environment.TopologyReliable,
                _environment.PerformanceCores);

            lines.Add(affinity is GamingAffinityMode mode
                ? $"• Afinidad del juego → {mode} (sólo topología híbrida confiable; reversible)."
                : "• Afinidad → sin cambios: no aplicable en esta CPU (no híbrida o topología no confiable).");

            if (profile.DisableGameDvr)
            {
                lines.Add(_environment.GameDvrEnabled
                    ? "• GameDVR (captura en segundo plano) → desactivar. Valor original guardado para restaurar."
                    : "• GameDVR → ya desactivado; no se toca.");
            }

            if (profile.EnableGameMode)
            {
                lines.Add(_environment.GameModeEnabled
                    ? "• Modo Juego → ya activo; no se toca."
                    : "• Modo Juego de Windows → activar. Valor original guardado para restaurar.");
            }

            if (profile.RequestHighPerformancePowerPlan)
            {
                if (_environment.IsOnAcPower == false)
                    lines.Add("• Plan de energía → sin cambios: el equipo está en batería.");
                else if (!_environment.HasHighPerformancePlan)
                    lines.Add("• Plan de energía → sin cambios: Windows no expone un plan de Alto rendimiento.");
                else if (_environment.IsBalancedPowerPlan)
                    lines.Add($"• Plan de energía → Alto rendimiento temporal (actual: {_environment.PowerPlanName}). Se restaura al terminar.");
                else
                    lines.Add($"• Plan de energía → sin cambios: el plan activo ({_environment.PowerPlanName}) no es Equilibrado.");
            }
            else
            {
                lines.Add("• Plan de energía → se conserva el actual.");
            }

            lines.Add(profile.ThrottleBackgroundApps
                ? "• Segundo plano → opcional: bajar prioridad (BelowNormal) o cerrar sólo las apps que autorices una por una."
                : "• Segundo plano → sin cambios automáticos.");
        }
        else
        {
            lines.Add("• Ejecutá el análisis del entorno para resolver afinidad, energía y configuración gaming.");
        }

        if (SelectedGame is null)
            lines.Add("• Seleccioná un juego: la prioridad/afinidad sólo se aplican a su ejecutable.");
        else if (!IsGameRunning(SelectedGame))
            lines.Add($"• {SelectedGame.Name} no está en ejecución: prioridad/afinidad se aplicarán cuando lo inicies (o se omitirán con aviso).");

        lines.Add("• Todo cambio queda en el diario write-ahead del Booster y se revierte con RESTORE SYSTEM.");

        PlannedChangesText = string.Join(Environment.NewLine, lines);
    }

    private static string BuildStorageSummary()
    {
        try
        {
            var parts = new List<string>();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                parts.Add($"{drive.Name.TrimEnd('\\')} {drive.AvailableFreeSpace / 1_073_741_824.0:F0} GB libres de {drive.TotalSize / 1_073_741_824.0:F0} GB");
            }

            return parts.Count > 0
                ? string.Join(" · ", parts)
                : "Sin unidades fijas listas para consultar.";
        }
        catch (Exception ex)
        {
            return $"No se pudo consultar el almacenamiento: {ex.Message}";
        }
    }

    private async Task<LivePerformanceSnapshot?> WaitForSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);

        while (DateTimeOffset.UtcNow < deadline)
        {
            LivePerformanceSnapshot? snapshot = _monitor.GetLatest();

            if (snapshot is not null &&
                snapshot.Timestamp > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3))
                return snapshot;

            await Task.Delay(250, cancellationToken);
        }

        return _monitor.GetLatest();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}