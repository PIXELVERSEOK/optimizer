using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Analysis;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.History;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Optimization;

/// <summary>
/// Primer bloque funcional de optimización de Windows.
/// Aplica un cambio por vez, guarda el estado original en un diario persistente,
/// verifica el resultado y permite revertirlo. Nunca modifica Defender, firewall,
/// servicios críticos ni valores fuera de HKCU.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOptimizationService
{
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameModeValue = "AllowAutoGameMode";
    private const string GameDvrPath =
        @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string AppCaptureValue = "AppCaptureEnabled";
    private const string HistoricalCaptureValue = "HistoricalCaptureEnabled";
    private const string RunPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string BackgroundAccessPath =
        @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    private const string BackgroundAccessValue = "GlobalUserDisabled";
    private const string EdgePolicyPath = @"Software\Policies\Microsoft\Edge";
    private const string EdgeBackgroundValue = "BackgroundModeEnabled";
    private const string ContentDeliveryPath =
        @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private static readonly string[] SuggestionValues =
    {
        "SubscribedContent-338388Enabled",
        "SubscribedContent-338389Enabled",
        "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled",
        "SoftLandingEnabled",
        "SystemPaneSuggestionsEnabled"
    };

    private static readonly string[] HighPerformancePlanHints =
    {
        "Alto rendimiento",
        "High performance"
    };

    private readonly WindowsGamingConfigService _configService;
    private readonly StartupBackgroundAnalyzer _startupAnalyzer;
    private readonly WindowsChangeJournal _journal;
    private readonly EvoHistoryStore? _history;
    private readonly OptimizationEvidenceEvaluator _evidenceEvaluator;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public WindowsOptimizationService(
        WindowsGamingConfigService? configService = null,
        StartupBackgroundAnalyzer? startupAnalyzer = null,
        WindowsChangeJournal? journal = null,
        EvoHistoryStore? history = null,
        OptimizationEvidenceEvaluator? evidenceEvaluator = null)
    {
        _configService = configService ?? new WindowsGamingConfigService();
        _startupAnalyzer = startupAnalyzer ?? new StartupBackgroundAnalyzer();
        _journal = journal ?? new WindowsChangeJournal(DefaultJournalPath());
        _history = history;
        _evidenceEvaluator = evidenceEvaluator ?? new OptimizationEvidenceEvaluator();
    }

    public static string DefaultJournalPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EvoFpsOptimizer",
            "windows-changes.json");

    // ── Plan ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WindowsOptimizationItem>> GetPlanAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureSnapshotAsync(cancellationToken);

        return WindowsOptimizationPlanner.Build(snapshot);
    }

    public async Task<IReadOnlyList<WindowsStartupCandidate>> GetStartupCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var report = await _startupAnalyzer.AnalyzeAsync(cancellationToken);

        return report.StartupItems
            .Where(x =>
                x.Source.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) &&
                x.Source.Contains(@"\Run", StringComparison.OrdinalIgnoreCase) &&
                !x.Source.Contains("RunOnce", StringComparison.OrdinalIgnoreCase))
            .Select(x => new WindowsStartupCandidate(
                Name: x.Name,
                Command: x.Command,
                Source: x.Source,
                IsRecommended: x.Impact == StartupImpact.Medium,
                Recommendation: x.Recommendation))
            .ToArray();
    }

    /// <summary>
    /// Estado actual sin efectos secundarios. Público para que el catálogo y el
    /// Control Center puedan explicar cada propuesta con datos medidos.
    /// </summary>
    public async Task<WindowsOptimizationSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var config = _configService.ReadConfiguration();
        var startup = await GetStartupCandidatesAsync(cancellationToken);

        return new WindowsOptimizationSnapshot(
            GameModeEnabled: config.GameModeEnabled,
            GameDvrEnabled: config.GameDvrEnabled,
            HistoricalCaptureEnabled:
                ReadDword(Registry.CurrentUser, GameDvrPath, HistoricalCaptureValue) == 1,
            PowerPlanName: config.PowerPlanName,
            IsBalancedPowerPlan:
                WindowsGamingConfigService.IsBalancedPowerPlanName(config.PowerPlanName),
            StartupCandidateCount: startup.Count,
            UwpBackgroundDisabled:
                ReadDword(Registry.CurrentUser, BackgroundAccessPath, BackgroundAccessValue) == 1,
            EdgeBackgroundDisabled:
                ReadDword(Registry.CurrentUser, EdgePolicyPath, EdgeBackgroundValue) == 0 &&
                ReadDword(Registry.CurrentUser, EdgePolicyPath, EdgeBackgroundValue) is not null,
            WindowsSuggestionsDisabled: AreSuggestionsDisabled(),
            IsOnAcPower: config.IsOnAcPower,
            HasHighPerformancePlan: config.HasHighPerformancePlan);
    }

    // ── Aplicación ───────────────────────────────────────────────────────────

    public async Task<WindowsOptimizationResult> ApplyAsync(
        WindowsOptimizationTarget target,
        bool userAuthorized,
        bool hasMeasuredBaseline = false,
        CancellationToken cancellationToken = default)
    {
        if (!userAuthorized)
        {
            return new(
                WindowsOptimizationStatus.NotAuthorized,
                "El ajuste requiere autorización explícita del usuario.");
        }

        if (!hasMeasuredBaseline)
        {
            return new(
                WindowsOptimizationStatus.MeasurementRequired,
                "Se requiere una línea base medida antes de aplicar este ajuste. " +
                "EVO no conserva cambios sin evidencia antes/después.");
        }

        if (target == WindowsOptimizationTarget.StartupApp)
        {
            return new(
                WindowsOptimizationStatus.Unavailable,
                "Las aplicaciones de inicio se desactivan una por una desde la lista.");
        }

        if (target == WindowsOptimizationTarget.PowerPlan)
        {
            // Defensa en profundidad: el planificador ya exige AC verificada,
            // pero se relee aquí porque la fuente puede haber cambiado desde la
            // captura. Nunca se propone un plan de mayor consumo con batería o
            // estado de alimentación desconocido.
            bool? onAc = new WindowsPowerTelemetryService().ReadCurrent().IsOnAcPower;
            if (onAc != true)
            {
                return new(
                    WindowsOptimizationStatus.Unavailable,
                    "Se exige alimentación AC verificada en el momento de aplicar; " +
                    "con batería o estado desconocido no se cambia el plan de energía.");
            }
        }

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            IReadOnlyList<WindowsChangeOperation>? original =
                CaptureOperations(target, null);

            IReadOnlyList<WindowsChangeOperation>? applied =
                BuildDesiredOperations(target);

            if (original is null || applied is null)
            {
                return new(
                    WindowsOptimizationStatus.Unavailable,
                    "No se pudo leer el estado actual o no hay un valor objetivo disponible para este ajuste.");
            }

            if (SameState(original, applied))
            {
                return new(
                    WindowsOptimizationStatus.AlreadyOptimal,
                    $"{DescribeTarget(target)} ya se encuentra en el estado propuesto.");
            }

            return await CommitAsync(
                target,
                SettingKeyFor(target, null),
                original,
                applied,
                capture: () => CaptureOperations(target, null),
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<WindowsOptimizationResult> ApplyStartupAsync(
        string valueName,
        bool userAuthorized,
        bool hasMeasuredBaseline = false,
        CancellationToken cancellationToken = default)
    {
        if (!userAuthorized)
        {
            return new(
                WindowsOptimizationStatus.NotAuthorized,
                "Desactivar una aplicación de inicio requiere autorización explícita.");
        }

        if (!hasMeasuredBaseline)
        {
            return new(
                WindowsOptimizationStatus.MeasurementRequired,
                "Se requiere una línea base medida antes de desactivar una aplicación de inicio.");
        }

        if (string.IsNullOrWhiteSpace(valueName))
        {
            return new(
                WindowsOptimizationStatus.Unavailable,
                "La aplicación de inicio indicada no es válida.");
        }

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var candidates = await GetStartupCandidatesAsync(cancellationToken);
            var candidate = candidates.FirstOrDefault(x =>
                string.Equals(x.Name, valueName, StringComparison.OrdinalIgnoreCase));

            if (candidate is null)
            {
                return new(
                    WindowsOptimizationStatus.Unavailable,
                    $"'{valueName}' ya no está en el inicio del usuario.");
            }

            var original = new[]
            {
                new WindowsChangeOperation(
                    WindowsChangeKind.StartupValue,
                    candidate.Name,
                    candidate.Command)
            };

            var applied = new[]
            {
                new WindowsChangeOperation(
                    WindowsChangeKind.StartupValue,
                    candidate.Name,
                    null)
            };

            return await CommitAsync(
                WindowsOptimizationTarget.StartupApp,
                $"startup:{candidate.Name}",
                original,
                applied,
                capture: () => CaptureOperations(
                    WindowsOptimizationTarget.StartupApp,
                    candidate.Name),
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<WindowsOptimizationResult> CommitAsync(
        WindowsOptimizationTarget target,
        string settingKey,
        IReadOnlyList<WindowsChangeOperation> original,
        IReadOnlyList<WindowsChangeOperation> applied,
        Func<IReadOnlyList<WindowsChangeOperation>?> capture,
        CancellationToken cancellationToken)
    {
        var change = new WindowsOptimizationChange(
            Guid.NewGuid(),
            target,
            settingKey,
            original,
            applied,
            WindowsOptimizationStatus.Pending,
            DateTimeOffset.UtcNow,
            null,
            null);

        // Write-ahead: el estado original queda persistido antes de tocar Windows.
        await _journal.AppendAsync(change, cancellationToken);

        try
        {
            foreach (var operation in applied)
                Execute(operation);

            var current = capture();

            if (current is null || !SameState(current, applied))
            {
                throw new InvalidOperationException(
                    "La verificación posterior no coincide con el estado aplicado.");
            }

            change = change with
            {
                Status = WindowsOptimizationStatus.Verified,
                CompletedAt = DateTimeOffset.UtcNow
            };

            await _journal.UpdateAsync(change, cancellationToken);
            await SaveHistoryAsync(change, cancellationToken);

            return new(
                WindowsOptimizationStatus.Verified,
                $"{DescribeTarget(target)} aplicado y verificado.",
                change);
        }
        catch (Exception ex)
        {
            var restoreErrors = TryRestore(original).ToList();
            bool restoreVerified = false;

            try
            {
                var restored = capture();
                restoreVerified = restored is not null &&
                    SameState(restored, original);
            }
            catch (Exception verificationError)
            {
                restoreErrors.Add(
                    $"No se pudo verificar la restauración: {verificationError.Message}");
            }

            if (!restoreVerified)
            {
                restoreErrors.Add(
                    "El estado original no pudo verificarse después del rollback automático.");
            }

            change = change with
            {
                Status = restoreVerified
                    ? WindowsOptimizationStatus.Failed
                    : WindowsOptimizationStatus.RollbackFailed,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = restoreErrors.Count == 0
                    ? ex.Message
                    : $"{ex.Message} Rollback: {string.Join(" ", restoreErrors)}"
            };

            await _journal.UpdateAsync(change, cancellationToken);
            await SaveHistoryAsync(change, cancellationToken);

            return new(
                change.Status,
                restoreVerified
                    ? $"No se pudo aplicar {DescribeTarget(target)}: {ex.Message}. " +
                      "La restauración del estado original fue verificada."
                    : $"No se pudo aplicar {DescribeTarget(target)} y la restauración " +
                      "automática no pudo verificarse. Revisa el estado del sistema e " +
                      "intenta la restauración manual desde el diario.",
                change);
        }
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    public async Task<WindowsOptimizationResult> RollbackAsync(
        Guid changeId,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var change = (await _journal.GetAllAsync(cancellationToken))
                .FirstOrDefault(x => x.Id == changeId);

            if (change is null)
            {
                return new(
                    WindowsOptimizationStatus.Unavailable,
                    "No existe un cambio registrado con ese identificador.");
            }

            if (change.Status is not (
                WindowsOptimizationStatus.Pending or
                WindowsOptimizationStatus.Applied or
                WindowsOptimizationStatus.Verified or
                WindowsOptimizationStatus.PerformanceVerified or
                WindowsOptimizationStatus.PerformanceInconclusive or
                WindowsOptimizationStatus.Failed or
                WindowsOptimizationStatus.RollbackFailed))
            {
                return new(
                    WindowsOptimizationStatus.Unavailable,
                    "El cambio ya fue revertido o nunca llegó a aplicarse.");
            }

            var current = CaptureOperations(change.Target, StartupNameOf(change));

            if (change.Status is not (
                    WindowsOptimizationStatus.Failed or
                    WindowsOptimizationStatus.Pending or
                    WindowsOptimizationStatus.RollbackFailed) &&
                current is not null &&
                !SameState(current, change.AppliedOperations))
            {
                return new(
                    WindowsOptimizationStatus.Failed,
                    "El estado del sistema cambió desde que se aplicó el ajuste. No se sobrescribió.");
            }

            try
            {
                foreach (var operation in change.OriginalOperations.Reverse())
                    Execute(operation);

                var restored = CaptureOperations(change.Target, StartupNameOf(change));

                if (restored is null ||
                    !SameState(restored, change.OriginalOperations))
                {
                    throw new InvalidOperationException(
                        "La restauración no pudo verificarse.");
                }

                change = change with
                {
                    Status = WindowsOptimizationStatus.RolledBack,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = null
                };

                await _journal.UpdateAsync(change, cancellationToken);
                await SaveHistoryAsync(change, cancellationToken);

                return new(
                    WindowsOptimizationStatus.RolledBack,
                    $"{DescribeTarget(change.Target)} revertido y verificado.",
                    change);
            }
            catch (Exception ex)
            {
                change = change with
                {
                    Status = WindowsOptimizationStatus.RollbackFailed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message
                };
                await _journal.UpdateAsync(change, cancellationToken);
                await SaveHistoryAsync(change, cancellationToken);

                return new(
                    WindowsOptimizationStatus.RollbackFailed,
                    $"No se pudo verificar la reversión: {ex.Message}. " +
                    "El cambio permanece en el diario para reintentar la restauración.",
                    change);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<WindowsOptimizationResult>> RollbackAllAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await _journal.GetActiveAsync(cancellationToken);
        var results = new List<WindowsOptimizationResult>();

        foreach (var change in active.OrderByDescending(x => x.CreatedAt))
            results.Add(await RollbackAsync(change.Id, cancellationToken));

        return results;
    }

    /// <summary>
    /// Evalúa la evidencia antes/después de un cambio aplicado. Sólo conserva el
    /// cambio como PERFORMANCE_VERIFIED si demuestra beneficio consistente sin
    /// perjuicios. Si hay regresión o intercambio perjudicial, ejecuta rollback.
    /// Si falta evidencia, lo marca INCONCLUSO y no lo declara beneficioso.
    /// </summary>
    public async Task<WindowsOptimizationResult> EvaluateMeasurementAsync(
        Guid changeId,
        OptimizationMeasurement baseline,
        OptimizationMeasurement candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var change = (await _journal.GetAllAsync(cancellationToken))
            .FirstOrDefault(x => x.Id == changeId);

        if (change is null)
        {
            return new(
                WindowsOptimizationStatus.Unavailable,
                "No existe un cambio registrado con ese identificador.");
        }

        if (change.Status is not (
            WindowsOptimizationStatus.Applied or
            WindowsOptimizationStatus.Verified or
            WindowsOptimizationStatus.PerformanceVerified or
            WindowsOptimizationStatus.PerformanceInconclusive))
        {
            return new(
                WindowsOptimizationStatus.Unavailable,
                "El cambio no está aplicado; no hay nada que evaluar.");
        }

        string expectedModificationKey = change.Target == WindowsOptimizationTarget.StartupApp
            ? change.SettingKey
            : $"windows:{change.Target}";
        OptimizationModificationKind expectedKind =
            change.Target == WindowsOptimizationTarget.StartupApp
                ? OptimizationModificationKind.Process
                : OptimizationModificationKind.Windows;

        if (!string.Equals(
                baseline.Context.ModificationKey,
                expectedModificationKey,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidate.Context.ModificationKey,
                expectedModificationKey,
                StringComparison.OrdinalIgnoreCase) ||
            baseline.Context.Kind != expectedKind ||
            candidate.Context.Kind != expectedKind)
        {
            return new(
                WindowsOptimizationStatus.MeasurementRequired,
                "La línea base y la medición posterior deben pertenecer a este " +
                "cambio específico. No se evaluó ni se declaró una mejora.");
        }

        if (baseline.CapturedAt >= change.CreatedAt ||
            candidate.CapturedAt <= change.CreatedAt)
        {
            return new(
                WindowsOptimizationStatus.MeasurementRequired,
                "La línea base debe ser anterior a la aplicación y la medición " +
                "posterior debe ser posterior al cambio. No se evaluó el resultado.");
        }

        if (change.Target == WindowsOptimizationTarget.StartupApp &&
            !StartupBootSessionGuard.HasRebooted(
                baseline.Context.BootSessionId,
                candidate.Context.BootSessionId))
        {
            return new(
                WindowsOptimizationStatus.MeasurementRequired,
                "Se requiere una captura de línea base anterior y otra posterior a un " +
                "reinicio de Windows. Sin evidencia de reinicio, EVO no evalúa ni " +
                "declara una mejora de arranque.");
        }

        if (change.Target == WindowsOptimizationTarget.StartupApp)
        {
            var startupEvidence = new OptimizationEvidenceResult(
                baseline.Context.ModificationId,
                expectedModificationKey,
                baseline.Context.ModificationName,
                OptimizationEvidenceVerdict.NotVerifiable,
                ShouldKeep: false,
                ShouldRollback: false,
                RequiresUserDecision: false,
                Array.Empty<OptimizationMetricComparison>(),
                new[]
                {
                    "Se detectó un reinicio entre las capturas; las métricas generales de 30 s no miden el tiempo de inicio de esta aplicación."
                },
                new[]
                {
                    "No existe una medición de duración de arranque atribuible a esta entrada. El efecto de rendimiento es NO VERIFICABLE."
                },
                DateTimeOffset.UtcNow);

            var unverifiable = change with
            {
                Status = WindowsOptimizationStatus.PerformanceInconclusive,
                Evidence = startupEvidence,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = null
            };

            await _journal.UpdateAsync(unverifiable, cancellationToken);
            await SaveEvidenceHistoryAsync(baseline, candidate, startupEvidence, cancellationToken);
            await SaveHistoryAsync(unverifiable, cancellationToken);

            return new(
                WindowsOptimizationStatus.PerformanceInconclusive,
                "NO VERIFICABLE: EVO no mide la duración de arranque atribuible a esta aplicación. " +
                "El cambio permanece aplicado y reversible, pero no se declara una mejora.",
                unverifiable);
        }

        var evidence = _evidenceEvaluator.Evaluate(baseline, candidate);

        await SaveEvidenceHistoryAsync(
            baseline,
            candidate,
            evidence,
            cancellationToken);

        if (evidence.ShouldRollback)
        {
            var rollback = await RollbackAsync(changeId, cancellationToken);

            if (rollback.Change is null)
                return rollback;

            var withEvidence = rollback.Change with
            {
                Evidence = evidence
            };

            await _journal.UpdateAsync(withEvidence, cancellationToken);
            await SaveHistoryAsync(withEvidence, cancellationToken);

            return new(
                WindowsOptimizationStatus.RolledBack,
                $"{evidence.VerdictLabel}: {rollback.Message}",
                withEvidence);
        }

        var status = evidence.Verdict ==
            OptimizationEvidenceVerdict.ImprovementVerified
                ? WindowsOptimizationStatus.PerformanceVerified
                : WindowsOptimizationStatus.PerformanceInconclusive;

        var updated = change with
        {
            Status = status,
            Evidence = evidence,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = null
        };

        await _journal.UpdateAsync(updated, cancellationToken);
        await SaveHistoryAsync(updated, cancellationToken);

        string message = evidence.Verdict switch
        {
            OptimizationEvidenceVerdict.ImprovementVerified =>
                "MEJORA VERIFICADA: el cambio se conserva con evidencia antes/después.",
            OptimizationEvidenceVerdict.Inconclusive =>
                "INCONCLUSO: no se conserva automáticamente como mejora; revisá la evidencia.",
            _ =>
                "DATOS INSUFICIENTES: el cambio no se declara beneficioso ni se conserva automáticamente."
        };

        return new(status, message, updated);
    }

    public Task<IReadOnlyList<WindowsOptimizationChange>> GetChangesAsync(
        CancellationToken cancellationToken = default) =>
        _journal.GetAllAsync(cancellationToken);

    // ── Captura y ejecución de operaciones ───────────────────────────────────

    private IReadOnlyList<WindowsChangeOperation>? CaptureOperations(
        WindowsOptimizationTarget target,
        string? startupName)
    {
        switch (target)
        {
            case WindowsOptimizationTarget.GameMode:
            {
                var value = ReadDword(Registry.CurrentUser, GameBarPath, GameModeValue);

                return new[]
                {
                    value is null
                        ? new WindowsChangeOperation(
                            WindowsChangeKind.RegistryDelete,
                            $"{GameBarPath}\\{GameModeValue}",
                            null)
                        : new WindowsChangeOperation(
                            WindowsChangeKind.RegistryDword,
                            $"{GameBarPath}\\{GameModeValue}",
                            value.Value.ToString(CultureInfo.InvariantCulture))
                };
            }

            case WindowsOptimizationTarget.GameDvr:
            {
                return new[]
                {
                    CaptureDword(GameDvrPath, AppCaptureValue),
                    CaptureDword(GameDvrPath, HistoricalCaptureValue)
                };
            }

            case WindowsOptimizationTarget.PowerPlan:
            {
                string? guid = _configService.ReadActivePowerPlanGuid();

                return guid is null
                    ? null
                    : new[]
                    {
                        new WindowsChangeOperation(
                            WindowsChangeKind.PowerPlan,
                            "powercfg:active",
                            guid)
                    };
            }

            case WindowsOptimizationTarget.StartupApp:
            {
                if (string.IsNullOrWhiteSpace(startupName)) return null;

                string? command = ReadString(RunPath, startupName);

                return new[]
                {
                    new WindowsChangeOperation(
                        WindowsChangeKind.StartupValue,
                        startupName,
                        command)
                };
            }

            case WindowsOptimizationTarget.UwpBackground:
                return new[]
                {
                    CaptureDword(BackgroundAccessPath, BackgroundAccessValue)
                };

            case WindowsOptimizationTarget.EdgeBackground:
                return new[]
                {
                    CaptureDword(EdgePolicyPath, EdgeBackgroundValue)
                };

            case WindowsOptimizationTarget.WindowsSuggestions:
                return SuggestionValues
                    .Select(value => CaptureDword(ContentDeliveryPath, value))
                    .ToArray();

            default:
                return null;
        }
    }

    private IReadOnlyList<WindowsChangeOperation>? BuildDesiredOperations(
        WindowsOptimizationTarget target)
    {
        switch (target)
        {
            case WindowsOptimizationTarget.GameMode:
                return new[]
                {
                    new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{GameBarPath}\\{GameModeValue}",
                        "1")
                };

            case WindowsOptimizationTarget.GameDvr:
                return new[]
                {
                    new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{GameDvrPath}\\{AppCaptureValue}",
                        "0"),
                    new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{GameDvrPath}\\{HistoricalCaptureValue}",
                        "0")
                };

            case WindowsOptimizationTarget.PowerPlan:
            {
                string? guid = _configService.FindPowerPlanGuid(
                    HighPerformancePlanHints);

                return guid is null
                    ? null
                    : new[]
                    {
                        new WindowsChangeOperation(
                            WindowsChangeKind.PowerPlan,
                            "powercfg:active",
                            guid)
                    };
            }

            case WindowsOptimizationTarget.UwpBackground:
                return new[]
                {
                    new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{BackgroundAccessPath}\\{BackgroundAccessValue}",
                        "1")
                };

            case WindowsOptimizationTarget.EdgeBackground:
                return new[]
                {
                    new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{EdgePolicyPath}\\{EdgeBackgroundValue}",
                        "0")
                };

            case WindowsOptimizationTarget.WindowsSuggestions:
                return SuggestionValues
                    .Select(value => new WindowsChangeOperation(
                        WindowsChangeKind.RegistryDword,
                        $"{ContentDeliveryPath}\\{value}",
                        "0"))
                    .ToArray();

            default:
                return null;
        }
    }

    private static WindowsChangeOperation CaptureDword(
        string path,
        string name)
    {
        int? value = ReadDword(Registry.CurrentUser, path, name);

        return value is null
            ? new WindowsChangeOperation(
                WindowsChangeKind.RegistryDelete,
                $"{path}\\{name}",
                null)
            : new WindowsChangeOperation(
                WindowsChangeKind.RegistryDword,
                $"{path}\\{name}",
                value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Execute(WindowsChangeOperation operation)
    {
        switch (operation.Kind)
        {
            case WindowsChangeKind.RegistryDword:
            {
                var (path, name) = SplitTarget(operation.Target);

                if (!int.TryParse(operation.Value, out int value))
                    throw new InvalidOperationException(
                        $"Valor DWORD inválido para {operation.Target}.");

                WriteDword(Registry.CurrentUser, path, name, value);
                break;
            }

            case WindowsChangeKind.RegistryString:
            case WindowsChangeKind.StartupValue:
            {
                if (operation.Kind == WindowsChangeKind.StartupValue)
                {
                    if (operation.Value is null)
                        DeleteValue(Registry.CurrentUser, RunPath, operation.Target);
                    else
                        WriteString(Registry.CurrentUser, RunPath, operation.Target, operation.Value);

                    break;
                }

                var (path, name) = SplitTarget(operation.Target);
                WriteString(Registry.CurrentUser, path, name, operation.Value ?? string.Empty);
                break;
            }

            case WindowsChangeKind.RegistryDelete:
            {
                var (path, name) = SplitTarget(operation.Target);
                DeleteValue(Registry.CurrentUser, path, name);
                break;
            }

            case WindowsChangeKind.PowerPlan:
                SetActivePowerPlan(operation.Value);
                break;

            default:
                throw new NotSupportedException(
                    $"Operación de cambio no soportada: {operation.Kind}.");
        }
    }

    private IReadOnlyList<string> TryRestore(
        IReadOnlyList<WindowsChangeOperation> operations)
    {
        var errors = new List<string>();

        foreach (var operation in operations.Reverse())
        {
            try
            {
                Execute(operation);
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"No se pudo restaurar {operation.Target}: {ex.Message}");
            }
        }

        return errors;
    }

    // ── Verificación y utilidades ────────────────────────────────────────────

    private static bool SameState(
        IReadOnlyList<WindowsChangeOperation> left,
        IReadOnlyList<WindowsChangeOperation> right)
    {
        if (left.Count != right.Count) return false;

        static string Key(WindowsChangeOperation op) =>
            $"{op.Kind}|{op.Target}|{op.Value ?? "<null>"}";

        return left.Select(Key).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(
                right.Select(Key).OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static string SettingKeyFor(
        WindowsOptimizationTarget target,
        string? startupName) =>
        target switch
        {
            WindowsOptimizationTarget.GameMode => $"{GameBarPath}\\{GameModeValue}",
            WindowsOptimizationTarget.GameDvr =>
                $"{GameDvrPath} ({AppCaptureValue}, {HistoricalCaptureValue})",
            WindowsOptimizationTarget.PowerPlan => "powercfg:active",
            WindowsOptimizationTarget.UwpBackground =>
                $"{BackgroundAccessPath}\\{BackgroundAccessValue}",
            WindowsOptimizationTarget.EdgeBackground =>
                $"{EdgePolicyPath}\\{EdgeBackgroundValue}",
            WindowsOptimizationTarget.WindowsSuggestions =>
                $"{ContentDeliveryPath} (sugerencias)",
            _ => $"startup:{startupName}"
        };

    private static string DescribeTarget(WindowsOptimizationTarget target) =>
        target switch
        {
            WindowsOptimizationTarget.GameMode => "Modo Juego",
            WindowsOptimizationTarget.GameDvr => "Game DVR",
            WindowsOptimizationTarget.PowerPlan => "Plan de energía",
            WindowsOptimizationTarget.UwpBackground =>
                "Aplicaciones UWP en segundo plano",
            WindowsOptimizationTarget.EdgeBackground =>
                "Edge en segundo plano",
            WindowsOptimizationTarget.WindowsSuggestions =>
                "Sugerencias de Windows",
            _ => "Aplicación de inicio"
        };

    private static string? StartupNameOf(WindowsOptimizationChange change) =>
        change.Target == WindowsOptimizationTarget.StartupApp &&
        change.SettingKey.StartsWith("startup:", StringComparison.Ordinal)
            ? change.SettingKey["startup:".Length..]
            : null;

    private static (string Path, string Name) SplitTarget(string target)
    {
        int separator = target.LastIndexOf('\\');

        if (separator <= 0 || separator == target.Length - 1)
            throw new InvalidOperationException(
                $"Ruta de registro inválida: {target}");

        return (target[..separator], target[(separator + 1)..]);
    }

    private async Task SaveHistoryAsync(
        WindowsOptimizationChange change,
        CancellationToken cancellationToken)
    {
        if (_history is null) return;

        try
        {
            await _history.SaveAsync(
                "WindowsOptimization",
                change,
                change.CreatedAt,
                cancellationToken);
        }
        catch
        {
            // El diario JSON sigue siendo la fuente para el rollback.
        }
    }

    private async Task SaveEvidenceHistoryAsync(
        OptimizationMeasurement baseline,
        OptimizationMeasurement candidate,
        OptimizationEvidenceResult evidence,
        CancellationToken cancellationToken)
    {
        if (_history is null) return;

        try
        {
            await _history.SaveAsync(
                "OptimizationEvidence",
                new
                {
                    Baseline = baseline,
                    Candidate = candidate,
                    Evidence = evidence
                },
                evidence.EvaluatedAt,
                cancellationToken);
        }
        catch
        {
            // La evaluación no debe fallar por un problema de SQLite.
        }
    }

    // ── Registro de Windows (sólo HKCU) ──────────────────────────────────────

    private static bool AreSuggestionsDisabled() =>
        SuggestionValues.All(value =>
            ReadDword(Registry.CurrentUser, ContentDeliveryPath, value) == 0);

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);

            return key?.GetValue(name) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);

            return key?.GetValue(
                name,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDword(
        RegistryKey root,
        string path,
        string name,
        int value)
    {
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"No se pudo abrir HKCU\\{path}");

        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void WriteString(
        RegistryKey root,
        string path,
        string name,
        string value)
    {
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"No se pudo abrir HKCU\\{path}");

        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteValue(
        RegistryKey root,
        string path,
        string name)
    {
        using var key = root.OpenSubKey(path, writable: true);

        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void SetActivePowerPlan(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("GUID de plan de energía inválido.");

        var psi = new ProcessStartInfo("powercfg", $"/setactive {guid}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo ejecutar powercfg.");

        if (!process.WaitForExit(4000))
        {
            throw new TimeoutException(
                "powercfg no respondió al intentar cambiar el plan de energía.");
        }

        if (process.ExitCode != 0)
            throw new Win32Exception(
                process.ExitCode,
                "Windows rechazó el cambio de plan de energía.");
    }
}
