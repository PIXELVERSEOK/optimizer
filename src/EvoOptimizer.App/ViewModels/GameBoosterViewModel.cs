using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EvoOptimizer.Core.Gaming;
using EvoOptimizer.Infrastructure.Gaming;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App.ViewModels;

public enum BoosterVisualState
{
    Ready,
    Analyzing,
    Applying,
    BoostActive,
    Restoring,
    Restored,
    Error
}

/// <summary>Proceso candidato a throttle/cierre, con selección explícita por acción.</summary>
public sealed class BoostCandidateItem : INotifyPropertyChanged
{
    private bool _throttleSelected;
    private bool _closeSelected;

    public BoostCandidateItem(ProcessSummary summary)
    {
        Summary = summary;
    }

    public ProcessSummary Summary { get; }

    public int ProcessId => Summary.ProcessId;
    public string Name => Summary.Name;
    public double CpuPercent => Summary.CpuPercent;
    public double MemoryMb => Summary.MemoryMb;

    public bool ThrottleSelected
    {
        get => _throttleSelected;
        set
        {
            if (_throttleSelected == value)
                return;

            _throttleSelected = value;
            // Cerrar y bajar prioridad son excluyentes para el mismo proceso.
            if (value && _closeSelected)
            {
                _closeSelected = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseSelected)));
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThrottleSelected)));
        }
    }

    public bool CloseSelected
    {
        get => _closeSelected;
        set
        {
            if (_closeSelected == value)
                return;

            _closeSelected = value;
            if (value && _throttleSelected)
            {
                _throttleSelected = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThrottleSelected)));
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// EVO GAME BOOSTER (Módulo 2): BOOST NOW con autorización explícita por
/// acción, diario write-ahead, RESTORE SYSTEM y restauración automática al
/// salir el juego o al cerrar EVO. Todos los valores mostrados provienen de
/// mediciones reales; lo que no se puede medir se declara como no disponible.
/// </summary>
public sealed class GameBoosterViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly GameBoosterService _service;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _gameExitTimer;
    private bool _gameSeenRunning;
    private bool _autoRestoreInProgress;
    private bool _disposed;

    private BoosterVisualState _state = BoosterVisualState.Ready;
    private string _statusText = "Preparado. Analizá los procesos en segundo plano y autorizá las acciones antes de BOOST NOW.";
    private string _resultText = "Sin sesión todavía. EVO no declara mejoras de rendimiento sin medición.";
    private string _sessionSummary = "Sin sesión activa.";
    private InstalledGame? _selectedGame;
    private SmartGamingProfile _selectedProfile = SmartGamingProfiles.Get(GamingProfileKind.Balanced);
    private bool _applyPowerPlan = true;
    private bool _applyGamingRegistry = true;
    private bool _applyGamePriority = true;
    private bool _applyGameAffinity = true;
    private bool _relaunchClosedApps;
    private bool _autoRestoreOnGameExit = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameBoosterViewModel(
        GameBoosterService? service = null,
        Dispatcher? dispatcher = null)
    {
        _service = service ?? new GameBoosterService();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public ObservableCollection<BoostCandidateItem> Candidates { get; } = new();

    public ObservableCollection<BoostActionRecord> Actions { get; } = new();

    public ObservableCollection<string> SessionNotes { get; } = new();

    public IReadOnlyList<SmartGamingProfile> Profiles => SmartGamingProfiles.All;

    public BoosterVisualState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanBoost));
                OnPropertyChanged(nameof(CanRestore));
                OnPropertyChanged(nameof(CanRefreshCandidates));
            }
        }
    }

    public string StateText => State switch
    {
        BoosterVisualState.Ready => "PREPARADO",
        BoosterVisualState.Analyzing => "ANALIZANDO",
        BoosterVisualState.Applying => "APLICANDO",
        BoosterVisualState.BoostActive => "BOOST ACTIVO",
        BoosterVisualState.Restoring => "RESTAURANDO",
        BoosterVisualState.Restored => "RESTAURADO",
        BoosterVisualState.Error => "ERROR",
        _ => "PREPARADO"
    };

    public bool IsBusy => State is BoosterVisualState.Analyzing
        or BoosterVisualState.Applying or BoosterVisualState.Restoring;

    public bool CanBoost => !IsBusy && State != BoosterVisualState.BoostActive && !_disposed;

    public bool CanRestore => !IsBusy && !_disposed &&
        (State == BoosterVisualState.BoostActive ||
         _service.CurrentSession is { } session && BoostSessionRules.HasPendingRestore(session));

    public bool CanRefreshCandidates => !IsBusy && !_disposed;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetField(ref _resultText, value);
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetField(ref _sessionSummary, value);
    }

    public InstalledGame? SelectedGame
    {
        get => _selectedGame;
        private set
        {
            if (SetField(ref _selectedGame, value))
                OnPropertyChanged(nameof(GameDisplayText));
        }
    }

    public string GameDisplayText => SelectedGame is { } game
        ? $"{game.Name} ({game.Launcher}) · {game.ExecutableName}"
        : "Sin juego seleccionado (Boost sólo de sistema). Elegí un juego en el Game Optimizer.";

    public SmartGamingProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
                OnPropertyChanged(nameof(SelectedProfileDescription));
        }
    }

    public string SelectedProfileDescription => SelectedProfile.Description;

    public bool ApplyPowerPlan
    {
        get => _applyPowerPlan;
        set => SetField(ref _applyPowerPlan, value);
    }

    public bool ApplyGamingRegistry
    {
        get => _applyGamingRegistry;
        set => SetField(ref _applyGamingRegistry, value);
    }

    public bool ApplyGamePriority
    {
        get => _applyGamePriority;
        set => SetField(ref _applyGamePriority, value);
    }

    public bool ApplyGameAffinity
    {
        get => _applyGameAffinity;
        set => SetField(ref _applyGameAffinity, value);
    }

    public bool RelaunchClosedApps
    {
        get => _relaunchClosedApps;
        set => SetField(ref _relaunchClosedApps, value);
    }

    public bool AutoRestoreOnGameExit
    {
        get => _autoRestoreOnGameExit;
        set => SetField(ref _autoRestoreOnGameExit, value);
    }

    // ── Integración con el Game Optimizer (Módulo 3) ─────────────────────────

    /// <summary>
    /// Recibe juego + perfil recomendados desde el Optimizer y preselecciona
    /// las acciones coherentes con el perfil. No aplica nada todavía.
    /// </summary>
    public void PrepareBoost(InstalledGame? game, GamingProfileKind profileKind)
    {
        SelectedGame = game;
        SelectedProfile = SmartGamingProfiles.Get(profileKind);
        ApplyPowerPlan = SelectedProfile.RequestHighPerformancePowerPlan;
        ApplyGamingRegistry = SelectedProfile.DisableGameDvr || SelectedProfile.EnableGameMode;
        ApplyGamePriority = game is not null;
        ApplyGameAffinity = game is not null && SelectedProfile.PreferredAffinity is not null;
        StatusText = game is null
            ? $"Perfil {SelectedProfile.Name} cargado sin juego. Autorizá las acciones y pulsá BOOST NOW."
            : $"{game.Name} + perfil {SelectedProfile.Name} preparados. Revisá los candidatos y autorizá cada acción.";
    }

    // ── Inicio / recuperación ante cierre inesperado ─────────────────────────

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            BoostOperationResult recovery =
                await _service.RecoverInterruptedSessionAsync(cancellationToken);

            if (recovery.Session is not null)
            {
                UpdateSessionView(recovery.Session);
                State = recovery.Success ? BoosterVisualState.Restored : BoosterVisualState.Error;
                StatusText = "Sesión interrumpida detectada al iniciar EVO. " + recovery.Message;
            }
            else
            {
                StatusText = recovery.Message + " " + StatusText;
            }
        }
        catch (Exception ex)
        {
            State = BoosterVisualState.Error;
            StatusText = $"Error comprobando sesiones interrumpidas: {ex.Message}";
        }
    }

    // ── Candidatos ───────────────────────────────────────────────────────────

    public async Task RefreshCandidatesAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;

        BoosterVisualState previous = State;
        State = BoosterVisualState.Analyzing;
        StatusText = "Midiendo procesos en segundo plano (telemetría real)…";

        try
        {
            LivePerformanceSnapshot? snapshot =
                await _service.EnsureTelemetryAsync(cancellationToken);

            var throttledIds = Candidates
                .Where(candidate => candidate.ThrottleSelected)
                .Select(candidate => candidate.ProcessId)
                .ToHashSet();
            var closedIds = Candidates
                .Where(candidate => candidate.CloseSelected)
                .Select(candidate => candidate.ProcessId)
                .ToHashSet();

            Candidates.Clear();

            if (snapshot is null)
            {
                StatusText = "No se pudo obtener una muestra de telemetría. No se muestran procesos inventados.";
            }
            else
            {
                foreach (ProcessSummary process in _service.GetBoostCandidates(
                    snapshot, SelectedGame?.ExecutableName))
                {
                    Candidates.Add(new BoostCandidateItem(process)
                    {
                        ThrottleSelected = throttledIds.Contains(process.ProcessId),
                        CloseSelected = closedIds.Contains(process.ProcessId)
                    });
                }

                StatusText = $"{Candidates.Count} aplicaciones no esenciales detectadas " +
                             $"(CPU sistema {snapshot.SystemCpuPercent:F1}%, RAM libre {snapshot.AvailableMemoryMb / 1024:F1} GB). " +
                             "Los procesos críticos, de seguridad, anti-cheat y periféricos están excluidos por política.";
            }

            State = previous == BoosterVisualState.BoostActive
                ? BoosterVisualState.BoostActive
                : BoosterVisualState.Ready;
        }
        catch (Exception ex)
        {
            State = BoosterVisualState.Error;
            StatusText = $"Error analizando candidatos: {ex.Message}";
        }
    }

    // ── BOOST NOW ────────────────────────────────────────────────────────────

    public BoostRequest BuildRequest() => new(
        SelectedGame,
        SelectedProfile.Kind,
        ApplyPowerPlan,
        ApplyGamingRegistry,
        ApplyGamePriority,
        ApplyGameAffinity,
        Candidates.Where(candidate => candidate.ThrottleSelected)
            .Select(candidate => candidate.ProcessId).ToArray(),
        Candidates.Where(candidate => candidate.CloseSelected)
            .Select(candidate => candidate.ProcessId).ToArray(),
        RelaunchClosedApps,
        AutoRestoreOnGameExit);

    public string BuildConfirmationSummary()
    {
        BoostRequest request = BuildRequest();
        var lines = new List<string>
        {
            $"Juego: {SelectedGame?.Name ?? "sin juego (sólo sistema)"}",
            $"Perfil: {SelectedProfile.Name}"
        };

        if (request.ApplyPowerPlan && SelectedProfile.RequestHighPerformancePowerPlan)
            lines.Add("• Plan de energía → Alto rendimiento temporal (sólo con AC y plan Equilibrado activo).");

        if (request.ApplyGamingRegistry)
            lines.Add("• GameDVR/Modo Juego → ajustes de registro HKCU con rollback del RestoreCenter.");

        if (request.ApplyGamePriority && SelectedGame is not null)
            lines.Add($"• Prioridad de {SelectedGame.ExecutableName} → AboveNormal (si está en ejecución).");

        if (request.ApplyGameAffinity && SelectedGame is not null)
            lines.Add($"• Afinidad de {SelectedGame.ExecutableName} → según topología (se omite si no es aplicable).");

        foreach (int pid in request.ThrottleProcessIds)
        {
            string name = Candidates.FirstOrDefault(c => c.ProcessId == pid)?.Name ?? $"PID {pid}";
            lines.Add($"• Bajar prioridad a BelowNormal: {name} (PID {pid}).");
        }

        foreach (int pid in request.CloseProcessIds)
        {
            string name = Candidates.FirstOrDefault(c => c.ProcessId == pid)?.Name ?? $"PID {pid}";
            lines.Add($"• Cerrar (cierre voluntario, nunca forzado): {name} (PID {pid}).");
        }

        lines.Add(request.RelaunchClosedApps
            ? "• Las apps cerradas se relanzarán al restaurar (autorizado)."
            : "• Las apps cerradas NO se relanzarán automáticamente.");

        lines.Add(request.AutoRestoreOnGameExit
            ? "• Restauración automática al salir el juego."
            : "• Sin restauración automática al salir el juego (RESTORE SYSTEM manual).");

        lines.Add("Todo queda registrado en el diario write-ahead; RESTORE SYSTEM revierte cada cambio.");
        return string.Join(Environment.NewLine, lines);
    }

    public async Task BoostNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanBoost)
            return;

        State = BoosterVisualState.Applying;
        StatusText = "Aplicando acciones autorizadas con diario write-ahead…";
        _gameSeenRunning = false;

        try
        {
            BoostOperationResult result =
                await _service.StartBoostAsync(BuildRequest(), cancellationToken);

            if (result.Session is not null)
                UpdateSessionView(result.Session);

            StatusText = result.Message;

            if (result.Session is { Status: BoostSessionStatus.Active })
            {
                State = result.Success ? BoosterVisualState.BoostActive : BoosterVisualState.Error;
                StartGameExitWatch();
            }
            else
            {
                State = BoosterVisualState.Error;
            }
        }
        catch (Exception ex)
        {
            State = BoosterVisualState.Error;
            StatusText = $"Error durante el Boost: {ex.Message}";
        }
    }

    // ── RESTORE SYSTEM ───────────────────────────────────────────────────────

    public async Task RestoreSystemAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;

        State = BoosterVisualState.Restoring;
        StatusText = "Restaurando el estado original en orden inverso…";
        StopGameExitWatch();

        try
        {
            BoostOperationResult result =
                await _service.RestoreSystemAsync(cancellationToken);

            if (result.Session is not null)
                UpdateSessionView(result.Session);

            StatusText = result.Message;
            State = result.Success ? BoosterVisualState.Restored : BoosterVisualState.Error;
        }
        catch (Exception ex)
        {
            State = BoosterVisualState.Error;
            StatusText = $"Error durante la restauración: {ex.Message}";
        }
    }

    // ── Vigilancia de salida del juego ───────────────────────────────────────

    private void StartGameExitWatch()
    {
        StopGameExitWatch();

        if (!AutoRestoreOnGameExit || SelectedGame is null)
            return;

        _gameExitTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _gameExitTimer.Tick += GameExitTimer_Tick;
        _gameExitTimer.Start();
    }

    private void StopGameExitWatch()
    {
        if (_gameExitTimer is null)
            return;

        _gameExitTimer.Stop();
        _gameExitTimer.Tick -= GameExitTimer_Tick;
        _gameExitTimer = null;
    }

    private async void GameExitTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoRestoreInProgress || _disposed)
            return;

        if (_service.CurrentSession is not { Status: BoostSessionStatus.Active } session)
        {
            StopGameExitWatch();
            return;
        }

        try
        {
            if (_service.IsGameRunning(session))
            {
                if (!_gameSeenRunning)
                {
                    _gameSeenRunning = true;
                    StatusText = $"{session.GameName} detectado en ejecución. Restauración automática armada para cuando salga.";
                }

                return;
            }

            // Sólo se restaura automáticamente si el juego llegó a verse en
            // ejecución: si nunca se inició, la sesión queda para restauración
            // manual (evita revertir el Boost antes de lanzar el juego).
            if (!_gameSeenRunning)
                return;

            _autoRestoreInProgress = true;
            StatusText = $"{session.GameName} salió. Restaurando el sistema automáticamente…";
            await RestoreSystemAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error en la vigilancia de salida del juego: {ex.Message}";
        }
        finally
        {
            _autoRestoreInProgress = false;
        }
    }

    // ── Vista de sesión ──────────────────────────────────────────────────────

    private void UpdateSessionView(BoostSession session)
    {
        Actions.Clear();
        foreach (BoostActionRecord action in session.Actions)
            Actions.Add(action);

        SessionNotes.Clear();
        foreach (string note in session.Notes)
            SessionNotes.Add(note);

        SessionSummary =
            $"Sesión {session.Id.ToString()[..8]} · {session.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · " +
            $"{session.GameName ?? "sin juego"} · Perfil {session.ProfileKind} · Estado {session.Status}";

        ResultText = BuildResultText(session);
        OnPropertyChanged(nameof(CanRestore));
    }

    private static string BuildResultText(BoostSession session)
    {
        static string Cpu(double? value) => value is double cpu ? $"{cpu:F1}%" : "sin datos";
        static string Ram(double? value) => value is double ram ? $"{ram / 1024:F2} GB" : "sin datos";

        int applied = session.Actions.Count(a => a.Status == BoostActionStatus.Applied);
        int restored = session.Actions.Count(a => a.Status == BoostActionStatus.Restored);
        int skipped = session.Actions.Count(a => a.Status == BoostActionStatus.Skipped);
        int failed = session.Actions.Count(a =>
            a.Status is BoostActionStatus.Failed or BoostActionStatus.Conflict);

        return
            $"CPU sistema: {Cpu(session.BeforeCpuPercent)} → {Cpu(session.AfterCpuPercent)} · " +
            $"RAM libre: {Ram(session.BeforeAvailableMemoryMb)} → {Ram(session.AfterAvailableMemoryMb)}{Environment.NewLine}" +
            $"Acciones: {applied} aplicadas · {restored} restauradas · {skipped} omitidas · {failed} con error.{Environment.NewLine}" +
            "Medición de carga del sistema, no de FPS. EVO no declara mejoras de frametime sin datos de PresentMon compatibles.";
    }

    // ── Cierre ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopGameExitWatch();

        try
        {
            // Al cerrar EVO con un Boost activo, el sistema vuelve a su estado
            // original. Si algo falla, el diario persistente permite recuperar
            // la sesión en el próximo inicio.
            if (_service.CurrentSession is { } session &&
                BoostSessionRules.HasPendingRestore(session))
            {
                await _service.RestoreSystemAsync();
            }
        }
        catch
        {
            // El diario write-ahead garantiza la recuperación posterior.
        }
        finally
        {
            _service.Dispose();
        }
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