using System.Diagnostics;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Gaming;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.History;
using EvoOptimizer.Infrastructure.Native;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.Infrastructure.Gaming;

/// <summary>
/// Acciones autorizadas para una sesión de Boost. Ninguna se ejecuta sin que
/// la usuaria la haya marcado explícitamente en la interfaz.
/// </summary>
public sealed record BoostRequest(
    InstalledGame? Game,
    GamingProfileKind ProfileKind,
    bool ApplyPowerPlan,
    bool ApplyGamingRegistry,
    bool ApplyGamePriority,
    bool ApplyGameAffinity,
    IReadOnlyList<int> ThrottleProcessIds,
    IReadOnlyList<int> CloseProcessIds,
    bool RelaunchClosedApps,
    bool AutoRestoreOnGameExit);

public sealed record BoostOperationResult(
    bool Success,
    string Message,
    BoostSession? Session = null);

/// <summary>
/// EVO GAME BOOSTER: preparación temporal y reversible de Windows para una
/// sesión de juego. Orquesta los motores existentes (RestoreCenter,
/// ProcessGovernanceService, optimizadores de prioridad/afinidad, planes de
/// energía) y registra cada acción en un diario persistente write-ahead para
/// poder restaurar el estado original incluso tras un cierre inesperado.
/// Nunca usa Realtime, nunca termina procesos protegidos y no toca Defender,
/// firewall, Windows Update ni servicios críticos.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameBoosterService : IDisposable
{
    private readonly ProcessGovernanceService _governance;
    private readonly GamingOptimizationController _gamingController;
    private readonly WindowsGamingConfigService _configService;
    private readonly Cs2ProcessPriorityOptimizer _gamePriority;
    private readonly Cs2AffinityOptimizer _gameAffinity;
    private readonly CpuTopologyInfo _topology;
    private readonly EvoHistoryStore? _history;
    private readonly BoostSessionJournal _journal;
    private readonly LivePerformanceMonitor _monitor;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private BoostSession? _currentSession;
    private bool _disposed;

    public GameBoosterService(
        ProcessGovernanceService? governance = null,
        GamingOptimizationController? gamingController = null,
        WindowsGamingConfigService? configService = null,
        Cs2ProcessPriorityOptimizer? gamePriority = null,
        Cs2AffinityOptimizer? gameAffinity = null,
        CpuTopologyInfo? topology = null,
        EvoHistoryStore? history = null,
        BoostSessionJournal? journal = null,
        LivePerformanceMonitor? monitor = null)
    {
        _configService = configService ?? new WindowsGamingConfigService();
        _governance = governance ?? new ProcessGovernanceService(history);
        _gamingController = gamingController ?? new GamingOptimizationController(
            new Core.Recovery.RestoreCenter(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EvoFpsOptimizer", "gaming-restore.json"),
                new Recovery.CurrentUserRegistryStore()));
        _gamePriority = gamePriority ?? new Cs2ProcessPriorityOptimizer();
        _gameAffinity = gameAffinity ?? new Cs2AffinityOptimizer();
        _topology = topology ?? new IntelHybridTopologyProvider().DiscoverTopology();
        _history = history;
        _journal = journal ?? new BoostSessionJournal();
        _monitor = monitor ?? new LivePerformanceMonitor();
    }

    public BoostSession? CurrentSession => _currentSession;

    public LivePerformanceSnapshot? LatestSnapshot => _monitor.GetLatest();

    public string JournalPath => _journal.JournalPath;

    // ── Preparación / candidatos ─────────────────────────────────────────────

    /// <summary>
    /// Arranca el monitor de telemetría (si no corre) y espera una muestra real
    /// con delta de CPU. Devuelve null si no se pudo medir.
    /// </summary>
    public async Task<LivePerformanceSnapshot?> EnsureTelemetryAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_monitorTask is null)
        {
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = _monitor.StartAsync(_monitorCancellation.Token);
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = _monitor.GetLatest();

            if (snapshot is not null &&
                snapshot.Timestamp > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2))
                return snapshot;

            await Task.Delay(250, cancellationToken);
        }

        return _monitor.GetLatest();
    }

    /// <summary>
    /// Procesos que el Boost puede ofrecer para throttle/cierre: nunca
    /// protegidos, nunca el juego objetivo ni EVO.
    /// </summary>
    public IReadOnlyList<ProcessSummary> GetBoostCandidates(
        LivePerformanceSnapshot? snapshot,
        string? gameExecutableName)
    {
        if (snapshot is null)
            return Array.Empty<ProcessSummary>();

        return snapshot.TopProcesses
            .Where(process => BoostProcessPolicy.IsCloseCandidate(
                process.Name, gameExecutableName, userExclusions: null))
            .ToArray();
    }

    public bool IsGameRunning(BoostSession session)
    {
        if (string.IsNullOrWhiteSpace(session.GameExecutablePath))
            return false;

        string name = Path.GetFileNameWithoutExtension(session.GameExecutablePath);

        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ── BOOST NOW ────────────────────────────────────────────────────────────

    public async Task<BoostOperationResult> StartBoostAsync(
        BoostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            if (_currentSession is { Status: BoostSessionStatus.Active })
                return new(false, "Ya hay un Boost activo. Restaurá el sistema antes de iniciar otro.");

            var persisted = await _journal.LoadAsync(cancellationToken);

            if (persisted is not null && BoostSessionRules.HasPendingRestore(persisted))
            {
                return new(false,
                    "Existe una sesión de Boost interrumpida con cambios sin restaurar. " +
                    "EVO debe restaurar el sistema antes de iniciar un Boost nuevo.");
            }

            var profile = SmartGamingProfiles.Get(request.ProfileKind);
            var before = await EnsureTelemetryAsync(cancellationToken);

            var session = new BoostSession(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                EndedAt: null,
                request.Game?.Name,
                request.Game?.ExecutablePath,
                request.ProfileKind,
                BoostSessionStatus.Active,
                before?.SystemCpuPercent,
                before?.AvailableMemoryMb,
                AfterCpuPercent: null,
                AfterAvailableMemoryMb: null,
                request.AutoRestoreOnGameExit,
                Array.Empty<BoostActionRecord>(),
                new[]
                {
                    $"Perfil {profile.Name}. Toda acción fue autorizada explícitamente. " +
                    "Diario write-ahead activo: restauración disponible incluso tras un cierre inesperado."
                });

            // Write-ahead: la sesión existe en disco ANTES de tocar Windows.
            await _journal.SaveAsync(session, cancellationToken);
            _currentSession = session;

            if (request.ApplyPowerPlan && profile.RequestHighPerformancePowerPlan)
                session = await ApplyPowerPlanAsync(session, cancellationToken);

            if (request.ApplyGamingRegistry)
                session = await ApplyGamingRegistryAsync(session, profile, cancellationToken);

            foreach (int processId in request.ThrottleProcessIds)
                session = await ThrottleProcessAsync(session, processId, cancellationToken);

            foreach (int processId in request.CloseProcessIds)
                session = await CloseProcessAsync(session, processId, request.RelaunchClosedApps, cancellationToken);

            if (request.Game is not null)
            {
                if (request.ApplyGamePriority)
                    session = await ApplyGamePriorityAsync(session, request.Game, cancellationToken);

                if (request.ApplyGameAffinity)
                    session = await ApplyGameAffinityAsync(session, request.Game, profile, cancellationToken);
            }

            // Medición posterior real: se espera un delta de CPU nuevo.
            await Task.Delay(1200, cancellationToken);
            var after = _monitor.GetLatest();

            session = session with
            {
                AfterCpuPercent = after?.SystemCpuPercent,
                AfterAvailableMemoryMb = after?.AvailableMemoryMb
            };

            await _journal.SaveAsync(session, cancellationToken);
            _currentSession = session;

            await SaveHistoryAsync("BoostStarted", session, cancellationToken);

            int applied = session.Actions.Count(a => a.Status == BoostActionStatus.Applied);
            int failed = session.Actions.Count(a => a.Status is BoostActionStatus.Failed or BoostActionStatus.Conflict);

            return new(
                failed == 0,
                $"Boost activo: {applied} acciones aplicadas, {failed} con error. " +
                "RESTORE SYSTEM revierte todo al estado original.",
                session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_currentSession is not null)
            {
                _currentSession = BoostSessionRules.AddNote(
                    _currentSession, $"Error durante el Boost: {ex.Message}");
                await TrySaveSessionAsync(_currentSession, cancellationToken);
            }

            return new(false, $"Error durante el Boost: {ex.Message}", _currentSession);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<BoostSession> ApplyPowerPlanAsync(
        BoostSession session,
        CancellationToken cancellationToken)
    {
        var action = NewAction(BoostActionKind.PowerPlan, "powercfg:active", "Plan de energía");

        try
        {
            var config = _configService.ReadConfiguration();

            if (config.IsOnAcPower == false)
                return await SkipAsync(session, action with
                {
                    Error = "Equipo en batería: EVO no fuerza el plan de alto rendimiento."
                }, cancellationToken);

            if (!config.HasHighPerformancePlan)
                return await SkipAsync(session, action with
                {
                    Error = "Windows no expone un plan de Alto rendimiento (powercfg /list)."
                }, cancellationToken);

            if (!WindowsGamingConfigService.IsBalancedPowerPlanName(config.PowerPlanName))
                return await SkipAsync(session, action with
                {
                    Error = $"El plan activo ({config.PowerPlanName}) no es Equilibrado; no se modifica."
                }, cancellationToken);

            string? originalGuid = _configService.ReadActivePowerPlanGuid();
            string? targetGuid = _configService.FindPowerPlanGuid("Alto rendimiento", "High performance");

            if (originalGuid is null || targetGuid is null)
                return await FailAsync(session, action with
                {
                    Error = "No se pudieron leer los GUIDs de los planes de energía."
                }, cancellationToken);

            action = action with
            {
                OriginalValue = originalGuid,
                AppliedValue = targetGuid
            };

            // Write-ahead del valor original antes de cambiar el plan.
            session = BoostSessionRules.UpsertAction(session, action);
            await _journal.SaveAsync(session, cancellationToken);

            _configService.SetActivePowerPlan(targetGuid);

            return await AppliedAsync(session, action, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FailAsync(session, action with { Error = ex.Message }, cancellationToken);
        }
    }

    private async Task<BoostSession> ApplyGamingRegistryAsync(
        BoostSession session,
        SmartGamingProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.DisableGameDvr)
        {
            session = await ApplyRegistryEntryAsync(
                session, BoostActionKind.GameDvrCapture,
                "Captura en segundo plano (GameDVR)",
                () => _gamingController.DisableBackgroundCaptureAsync(cancellationToken),
                cancellationToken);

            session = await ApplyRegistryEntryAsync(
                session, BoostActionKind.GameDvrHistorical,
                "Captura histórica (GameDVR)",
                () => _gamingController.DisableHistoricalCaptureAsync(cancellationToken),
                cancellationToken);
        }

        if (profile.EnableGameMode)
        {
            session = await ApplyRegistryEntryAsync(
                session, BoostActionKind.GameMode,
                "Modo Juego de Windows",
                () => _gamingController.EnableGameModeAsync(cancellationToken),
                cancellationToken);
        }

        return session;
    }

    private async Task<BoostSession> ApplyRegistryEntryAsync(
        BoostSession session,
        BoostActionKind kind,
        string displayName,
        Func<Task<Core.Recovery.RestoreEntry>> apply,
        CancellationToken cancellationToken)
    {
        var action = NewAction(kind, displayName, displayName);

        try
        {
            var entry = await apply();

            action = action with
            {
                RestoreEntryId = entry.Id,
                OriginalValue = entry.OriginalValue.Exists ? entry.OriginalValue.Value : "(no existía)",
                AppliedValue = entry.RequestedValue.Value,
                Target = $@"{entry.SettingPath}\{entry.ValueName}"
            };

            return await AppliedAsync(session, action, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FailAsync(session, action with { Error = ex.Message }, cancellationToken);
        }
    }

    private async Task<BoostSession> ThrottleProcessAsync(
        BoostSession session,
        int processId,
        CancellationToken cancellationToken)
    {
        string displayName;
        string originalPriority;
        long startTicks;

        try
        {
            using var process = Process.GetProcessById(processId);
            displayName = $"{process.ProcessName} (PID {processId})";
            originalPriority = process.PriorityClass.ToString();
            startTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex)
        {
            return await FailAsync(session, NewAction(
                BoostActionKind.BackgroundPriority, $"PID {processId}", $"PID {processId}") with
            {
                Error = $"El proceso ya no existe: {ex.Message}"
            }, cancellationToken);
        }

        var action = NewAction(BoostActionKind.BackgroundPriority, processId.ToString(), displayName) with
        {
            ProcessId = processId,
            ProcessStartTimeUtcTicks = startTicks,
            OriginalValue = originalPriority,
            AppliedValue = nameof(ProcessPriorityClass.BelowNormal)
        };

        var result = await _governance.ThrottleAsync(
            processId, userAuthorized: true, allowUnclassified: true, cancellationToken);

        return result.Success
            ? await AppliedAsync(session, action, cancellationToken)
            : await FailAsync(session, action with { Error = result.Message }, cancellationToken);
    }

    private async Task<BoostSession> CloseProcessAsync(
        BoostSession session,
        int processId,
        bool relaunchAuthorized,
        CancellationToken cancellationToken)
    {
        var action = NewAction(BoostActionKind.ProcessClose, processId.ToString(), $"PID {processId}");

        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;

            if (BoostProcessPolicy.IsProtected(name))
            {
                return await SkipAsync(session, action with
                {
                    DisplayName = name,
                    Error = $"'{name}' está protegido por la política de EVO. Nunca se finaliza."
                }, cancellationToken);
            }

            string? executablePath = null;

            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
                // Proceso elevado o de otra arquitectura: sin ruta no hay relanzamiento.
            }

            action = action with
            {
                DisplayName = $"{name} (PID {processId})",
                ProcessId = processId,
                ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = executablePath,
                RelaunchAuthorized = relaunchAuthorized && executablePath is not null,
                OriginalValue = "En ejecución",
                AppliedValue = "Cerrada por EVO (autorizado)"
            };

            // Write-ahead antes de cerrar.
            session = BoostSessionRules.UpsertAction(session, action);
            await _journal.SaveAsync(session, cancellationToken);

            bool closed = process.CloseMainWindow();

            if (closed)
            {
                try
                {
                    closed = process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    closed = true;
                }
            }

            if (!closed && !process.HasExited)
            {
                return await FailAsync(session, action with
                {
                    Error = $"'{name}' no respondió al cierre voluntario. EVO no fuerza la terminación."
                }, cancellationToken);
            }

            return await AppliedAsync(session, action, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FailAsync(session, action with { Error = ex.Message }, cancellationToken);
        }
    }

    private async Task<BoostSession> ApplyGamePriorityAsync(
        BoostSession session,
        InstalledGame game,
        CancellationToken cancellationToken)
    {
        string processName = Path.GetFileNameWithoutExtension(game.ExecutableName);
        var action = NewAction(BoostActionKind.GamePriority, processName, $"Prioridad de {game.Name}");

        var result = _gamePriority.Apply(processName);

        if (result.Status == PriorityOptimizationStatus.Applied && result.Token is not null)
        {
            action = action with
            {
                OptimizerTokenId = result.Token.Id,
                ProcessId = result.Token.ProcessId,
                ProcessStartTimeUtcTicks = result.Token.ProcessStartTimeUtcTicks,
                OriginalValue = result.Token.OriginalPriority.ToString(),
                AppliedValue = result.Token.AppliedPriority.ToString()
            };

            return await AppliedAsync(session, action, cancellationToken);
        }

        return await SkipAsync(session, action with { Error = result.Message }, cancellationToken);
    }

    private async Task<BoostSession> ApplyGameAffinityAsync(
        BoostSession session,
        InstalledGame game,
        SmartGamingProfile profile,
        CancellationToken cancellationToken)
    {
        string processName = Path.GetFileNameWithoutExtension(game.ExecutableName);
        var action = NewAction(BoostActionKind.GameAffinity, processName, $"Afinidad de {game.Name}");

        var mode = SmartGamingProfiles.ResolveAffinity(
            profile, _topology.IsHybrid, _topology.IsTopologyReliable, _topology.PerformanceCores);

        if (mode is null)
        {
            return await SkipAsync(session, action with
            {
                Error = "Afinidad no aplicable: topología no híbrida o no confiable. No se restringen núcleos."
            }, cancellationToken);
        }

        var result = _gameAffinity.Apply(_topology, mode.Value, processName);

        if (result.Status == AffinityOptimizationStatus.Applied && result.Token is not null)
        {
            action = action with
            {
                OptimizerTokenId = result.Token.Id,
                ProcessId = result.Token.ProcessId,
                ProcessStartTimeUtcTicks = result.Token.ProcessStartTimeUtcTicks,
                OriginalValue = $"0x{result.Token.OriginalAffinityMask:X}",
                AppliedValue = $"0x{result.Token.AppliedAffinityMask:X}"
            };

            return await AppliedAsync(session, action, cancellationToken);
        }

        return await SkipAsync(session, action with { Error = result.Message }, cancellationToken);
    }

    // ── RESTORE SYSTEM ───────────────────────────────────────────────────────

    public async Task<BoostOperationResult> RestoreSystemAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            var session = _currentSession ?? await _journal.LoadAsync(cancellationToken);

            if (session is null)
                return new(true, "No hay ninguna sesión de Boost registrada. El sistema está en su estado original.");

            if (!BoostSessionRules.HasPendingRestore(session))
            {
                _currentSession = null;
                return new(true, "La sesión ya fue restaurada. No hay cambios pendientes.", session);
            }

            // Orden inverso: lo último aplicado se restaura primero.
            foreach (var action in session.Actions.Reverse().ToArray())
            {
                if (action.Status != BoostActionStatus.Applied)
                    continue;

                session = await RestoreActionAsync(session, action, cancellationToken);
            }

            bool allRestored = session.Actions.All(a =>
                a.Status is BoostActionStatus.Restored or BoostActionStatus.Skipped);

            session = session with
            {
                Status = BoostSessionStatus.Restored,
                EndedAt = DateTimeOffset.UtcNow
            };

            if (!allRestored)
            {
                session = BoostSessionRules.AddNote(session,
                    "Algunas acciones no pudieron revertirse; quedaron registradas con su error. " +
                    "Revisá el detalle antes de iniciar otro Boost.");
            }

            await _journal.SaveAsync(session, cancellationToken);
            _currentSession = null;

            StopTelemetry();

            await SaveHistoryAsync("BoostRestored", session, cancellationToken);

            int restored = session.Actions.Count(a => a.Status == BoostActionStatus.Restored);
            int failed = session.Actions.Count(a =>
                a.Status is BoostActionStatus.Failed or BoostActionStatus.Conflict);

            return new(
                allRestored,
                allRestored
                    ? $"Sistema restaurado: {restored} acciones revertidas y verificadas."
                    : $"Restauración parcial: {restored} revertidas, {failed} con error. Detalle en el registro.",
                session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, $"Error durante la restauración: {ex.Message}", _currentSession);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<BoostSession> RestoreActionAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (action.Kind)
            {
                case BoostActionKind.PowerPlan:
                    if (action.OriginalValue is null)
                        return await ConflictAsync(session, action, "Sin GUID original registrado.", cancellationToken);

                    _configService.SetActivePowerPlan(action.OriginalValue);
                    return await RestoredAsync(session, action, cancellationToken);

                case BoostActionKind.GameDvrCapture:
                case BoostActionKind.GameDvrHistorical:
                case BoostActionKind.GameMode:
                    if (action.RestoreEntryId is null)
                        return await ConflictAsync(session, action, "Sin entrada de RestoreCenter registrada.", cancellationToken);

                    bool restored = await _gamingController.RestoreAsync(action.RestoreEntryId.Value, cancellationToken);

                    return restored
                        ? await RestoredAsync(session, action, cancellationToken)
                        : await ConflictAsync(session, action,
                            "RestoreCenter no pudo restaurar (el valor cambió externamente o ya fue restaurado).", cancellationToken);

                case BoostActionKind.BackgroundPriority:
                case BoostActionKind.GamePriority:
                    return await RestorePriorityAsync(session, action, cancellationToken);

                case BoostActionKind.GameAffinity:
                    return await RestoreAffinityAsync(session, action, cancellationToken);

                case BoostActionKind.ProcessClose:
                    return await RelaunchProcessAsync(session, action, cancellationToken);

                default:
                    return await ConflictAsync(session, action, "Tipo de acción desconocido.", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            return await ConflictAsync(session, action, ex.Message, cancellationToken);
        }
    }

    private async Task<BoostSession> RestorePriorityAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        if (action.ProcessId is not int processId)
            return await ConflictAsync(session, action, "Sin PID registrado.", cancellationToken);

        // Camino normal: el gobernador conoce la prioridad original.
        if (_governance.GetThrottledProcessIds().Contains(processId))
        {
            var result = await _governance.RestoreAsync(processId, cancellationToken);

            return result.Success
                ? await RestoredAsync(session, action, cancellationToken)
                : await ConflictAsync(session, action, result.Message, cancellationToken);
        }

        // Token del optimizador de juego (si sigue vivo en esta instancia).
        if (action.Kind == BoostActionKind.GamePriority && action.OptimizerTokenId is Guid tokenId)
        {
            var rollback = _gamePriority.Rollback(tokenId);

            if (rollback.Status is PriorityOptimizationStatus.RolledBack or
                PriorityOptimizationStatus.IdentityMismatch)
            {
                return rollback.Status == PriorityOptimizationStatus.RolledBack
                    ? await RestoredAsync(session, action, cancellationToken)
                    : await RestoredAsync(session, action with
                    {
                        Error = "El juego ya no existe con esa identidad; no hay prioridad que restaurar."
                    }, cancellationToken);
            }
        }

        // Recuperación tras cierre inesperado: restauración directa desde el
        // diario con verificación de identidad (PID + ticks de inicio).
        return await RestorePriorityFromJournalAsync(session, action, processId, cancellationToken);
    }

    private async Task<BoostSession> RestorePriorityFromJournalAsync(
        BoostSession session,
        BoostActionRecord action,
        int processId,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ProcessPriorityClass>(action.OriginalValue, out var original))
            return await ConflictAsync(session, action, "Prioridad original no registrada o inválida.", cancellationToken);

        try
        {
            using var process = Process.GetProcessById(processId);

            if (action.ProcessStartTimeUtcTicks is long ticks &&
                process.StartTime.ToUniversalTime().Ticks != ticks)
            {
                return await RestoredAsync(session, action with
                {
                    Error = "El PID pertenece a otro proceso; no se modificó nada."
                }, cancellationToken);
            }

            if (process.PriorityClass == original)
                return await RestoredAsync(session, action, cancellationToken);

            if (action.AppliedValue is not null &&
                process.PriorityClass.ToString() != action.AppliedValue)
            {
                return await ConflictAsync(session, action,
                    "La prioridad fue modificada externamente; no se sobrescribió.", cancellationToken);
            }

            process.PriorityClass = original;

            return process.PriorityClass == original
                ? await RestoredAsync(session, action, cancellationToken)
                : await ConflictAsync(session, action, "Windows no confirmó la restauración.", cancellationToken);
        }
        catch (ArgumentException)
        {
            return await RestoredAsync(session, action with
            {
                Error = "El proceso ya no existe; no hay prioridad que restaurar."
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return await ConflictAsync(session, action, ex.Message, cancellationToken);
        }
    }

    private async Task<BoostSession> RestoreAffinityAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        if (action.OptimizerTokenId is Guid tokenId)
        {
            var rollback = _gameAffinity.Rollback(tokenId);

            if (rollback.Status == AffinityOptimizationStatus.RolledBack)
                return await RestoredAsync(session, action, cancellationToken);

            if (rollback.Status == AffinityOptimizationStatus.IdentityMismatch)
            {
                return await RestoredAsync(session, action with
                {
                    Error = "El juego ya no existe con esa identidad; no hay afinidad que restaurar."
                }, cancellationToken);
            }
        }

        // Recuperación desde el diario (crash o token perdido).
        if (action.ProcessId is not int processId ||
            action.OriginalValue is null ||
            !ulong.TryParse(
                action.OriginalValue.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out ulong originalMask))
        {
            return await ConflictAsync(session, action, "Sin máscara original registrada.", cancellationToken);
        }

        try
        {
            using var process = Process.GetProcessById(processId);

            if (action.ProcessStartTimeUtcTicks is long ticks &&
                process.StartTime.ToUniversalTime().Ticks != ticks)
            {
                return await RestoredAsync(session, action with
                {
                    Error = "El PID pertenece a otro proceso; no se modificó nada."
                }, cancellationToken);
            }

            if (!NativeMethods.SetProcessAffinityMask(process.Handle, new UIntPtr(originalMask)))
            {
                return await ConflictAsync(session, action,
                    "Windows rechazó restaurar la afinidad original.", cancellationToken);
            }

            return await RestoredAsync(session, action, cancellationToken);
        }
        catch (ArgumentException)
        {
            return await RestoredAsync(session, action with
            {
                Error = "El proceso ya no existe; no hay afinidad que restaurar."
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return await ConflictAsync(session, action, ex.Message, cancellationToken);
        }
    }

    private async Task<BoostSession> RelaunchProcessAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        if (!action.RelaunchAuthorized)
        {
            return await RestoredAsync(session, action with
            {
                Error = "La aplicación no se relanza: el relanzamiento no fue autorizado."
            }, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(action.ExecutablePath) || !File.Exists(action.ExecutablePath))
        {
            return await ConflictAsync(session, action,
                "No se pudo relanzar: sin ruta de ejecutable válida registrada.", cancellationToken);
        }

        try
        {
            var startInfo = new ProcessStartInfo(action.ExecutablePath)
            {
                UseShellExecute = true,
                Arguments = action.Arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(action.ExecutablePath) ?? string.Empty
            };

            using var relaunched = Process.Start(startInfo);

            return relaunched is not null
                ? await RestoredAsync(session, action, cancellationToken)
                : await ConflictAsync(session, action, "Windows no devolvió un proceso al relanzar.", cancellationToken);
        }
        catch (Exception ex)
        {
            return await ConflictAsync(session, action, $"No se pudo relanzar: {ex.Message}", cancellationToken);
        }
    }

    // ── Recuperación ante cierre inesperado ──────────────────────────────────

    /// <summary>
    /// Comprueba al iniciar EVO si quedó una sesión interrumpida con cambios
    /// sin restaurar y la revierte automáticamente desde el diario persistente.
    /// </summary>
    public async Task<BoostOperationResult> RecoverInterruptedSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var persisted = await _journal.LoadAsync(cancellationToken);

        if (persisted is null || !BoostSessionRules.HasPendingRestore(persisted))
            return new(true, "Sin sesiones interrumpidas pendientes de restauración.");

        _currentSession = BoostSessionRules.AddNote(
            persisted with { Status = BoostSessionStatus.Interrupted },
            $"EVO detectó una sesión interrumpida (inicio {persisted.StartedAt:yyyy-MM-dd HH:mm}). " +
            "Restauración automática desde el diario persistente.");

        await _journal.SaveAsync(_currentSession, cancellationToken);

        return await RestoreSystemAsync(cancellationToken);
    }

    // ── Helpers de estado ────────────────────────────────────────────────────

    private static BoostActionRecord NewAction(
        BoostActionKind kind,
        string target,
        string displayName) =>
        new(Guid.NewGuid(), kind, target, displayName,
            OriginalValue: null, AppliedValue: null,
            BoostActionStatus.Applied, Error: null,
            DateTimeOffset.UtcNow, RestoredAt: null);

    private async Task<BoostSession> AppliedAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        session = BoostSessionRules.UpsertAction(
            session, action with { Status = BoostActionStatus.Applied });
        await _journal.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task<BoostSession> SkipAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        session = BoostSessionRules.UpsertAction(
            session, action with { Status = BoostActionStatus.Skipped });
        await _journal.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task<BoostSession> FailAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        session = BoostSessionRules.UpsertAction(
            session, action with { Status = BoostActionStatus.Failed });
        await _journal.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task<BoostSession> RestoredAsync(
        BoostSession session,
        BoostActionRecord action,
        CancellationToken cancellationToken)
    {
        session = BoostSessionRules.UpsertAction(session, action with
        {
            Status = BoostActionStatus.Restored,
            RestoredAt = DateTimeOffset.UtcNow
        });
        await _journal.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task<BoostSession> ConflictAsync(
        BoostSession session,
        BoostActionRecord action,
        string error,
        CancellationToken cancellationToken)
    {
        session = BoostSessionRules.UpsertAction(session, action with
        {
            Status = BoostActionStatus.Conflict,
            Error = error,
            RestoredAt = DateTimeOffset.UtcNow
        });
        await _journal.SaveAsync(session, cancellationToken);
        return session;
    }

    private async Task TrySaveSessionAsync(
        BoostSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.SaveAsync(session, cancellationToken);
        }
        catch
        {
            // El diario no debe enmascarar el error original.
        }
    }

    private async Task SaveHistoryAsync(
        string action,
        BoostSession session,
        CancellationToken cancellationToken)
    {
        if (_history is null)
            return;

        try
        {
            await _history.SaveAsync(
                "GameBoost",
                new
                {
                    Action = action,
                    SessionId = session.Id,
                    session.GameName,
                    Profile = session.ProfileKind.ToString(),
                    session.Status,
                    session.BeforeCpuPercent,
                    session.BeforeAvailableMemoryMb,
                    session.AfterCpuPercent,
                    session.AfterAvailableMemoryMb,
                    Actions = session.Actions.Select(a => new
                    {
                        a.Kind,
                        a.DisplayName,
                        a.Status,
                        a.Error
                    }),
                    Timestamp = DateTimeOffset.UtcNow
                },
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch
        {
            // El historial SQLite es complementario: el diario JSON manda.
        }
    }

    private void StopTelemetry()
    {
        try
        {
            _monitorCancellation?.Cancel();
        }
        catch
        {
            // Cancelación best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        StopTelemetry();

        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // El monitor se cancela; no bloqueamos el cierre.
        }

        _monitorCancellation?.Dispose();
        _monitor.Dispose();
        _operationLock.Dispose();
    }
}