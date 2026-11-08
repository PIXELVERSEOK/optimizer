using System.Text.Json.Serialization;

namespace EvoOptimizer.Core.Optimization;

/// <summary>
/// Ajustes de Windows que EVO puede preparar, aplicar y revertir de forma
/// individual. Deliberadamente no incluye Defender, firewall, servicios
/// críticos ni claves de politicas de sistema.
/// </summary>
public enum WindowsOptimizationTarget
{
    GameMode,
    GameDvr,
    PowerPlan,
    StartupApp,
    UwpBackground,
    EdgeBackground,
    WindowsSuggestions
}

public enum WindowsOptimizationStatus
{
    Pending,
    NotAuthorized,
    MeasurementRequired,
    Applied,
    Verified,
    PerformanceVerified,
    PerformanceInconclusive,
    Failed,
    RollbackFailed,
    RolledBack,
    AlreadyOptimal,
    Unavailable
}

public enum WindowsOptimizationRisk
{
    Low,
    Medium
}

/// <summary>
/// Operación atómica registrada en el diario de cambios. Permite revertir
/// ajustes de registro, del plan de energía y del inicio de aplicaciones.
/// </summary>
public enum WindowsChangeKind
{
    RegistryDword,
    RegistryString,
    RegistryDelete,
    PowerPlan,
    StartupValue
}

public sealed record WindowsChangeOperation(
    WindowsChangeKind Kind,
    string Target,
    string? Value);

/// <summary>
/// Estado detectado del sistema, sin efectos secundarios. Alimenta al planificador.
/// </summary>
public sealed record WindowsOptimizationSnapshot(
    bool GameModeEnabled,
    bool GameDvrEnabled,
    bool HistoricalCaptureEnabled,
    string PowerPlanName,
    bool IsBalancedPowerPlan,
    int StartupCandidateCount,
    bool UwpBackgroundDisabled = false,
    bool EdgeBackgroundDisabled = false,
    bool WindowsSuggestionsDisabled = false,
    bool? IsOnAcPower = null,
    bool HasHighPerformancePlan = false);

/// <summary>
/// Ajuste propuesto con su estado actual, beneficio y riesgo esperados.
/// No promete mejoras de FPS: sólo describe el efecto técnico conocido.
/// </summary>
public sealed record WindowsOptimizationItem(
    WindowsOptimizationTarget Target,
    string Title,
    string CurrentState,
    string ProposedState,
    string ExpectedBenefit,
    string Risk,
    WindowsOptimizationRisk RiskLevel,
    bool CanApply,
    string? BlockedReason)
{
    public string RiskLabel => RiskLevel == WindowsOptimizationRisk.Low
        ? "Riesgo bajo"
        : "Riesgo medio";
}

/// <summary>
/// Aplicación del inicio de Windows (sólo HKCU) que el usuario puede desactivar
/// individualmente y restaurar después.
/// </summary>
public sealed record WindowsStartupCandidate(
    string Name,
    string Command,
    string Source,
    bool IsRecommended,
    string Recommendation);

public sealed record WindowsOptimizationChange(
    Guid Id,
    WindowsOptimizationTarget Target,
    string SettingKey,
    IReadOnlyList<WindowsChangeOperation> OriginalOperations,
    IReadOnlyList<WindowsChangeOperation> AppliedOperations,
    WindowsOptimizationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    OptimizationEvidenceResult? Evidence = null)
{
    /// <summary>
    /// Distingue BUILD_VERIFIED (el ajuste se aplicó y el estado coincide) de
    /// PERFORMANCE_VERIFIED (además hay evidencia antes/después que demuestra
    /// beneficio sin perjuicios).
    /// </summary>
    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        WindowsOptimizationStatus.Pending => "PENDIENTE",
        WindowsOptimizationStatus.NotAuthorized => "NO AUTORIZADO",
        WindowsOptimizationStatus.MeasurementRequired => "MEDICIÓN REQUERIDA",
        WindowsOptimizationStatus.Applied => "APLICADO · PENDIENTE DE MEDICIÓN",
        WindowsOptimizationStatus.Verified => "BUILD_VERIFIED · PENDIENTE DE RENDIMIENTO",
        WindowsOptimizationStatus.PerformanceVerified => "PERFORMANCE_VERIFIED",
        WindowsOptimizationStatus.PerformanceInconclusive => "RENDIMIENTO INCONCLUSO",
        WindowsOptimizationStatus.Failed => "FALLIDO",
        WindowsOptimizationStatus.RollbackFailed => "RESTAURACIÓN PENDIENTE",
        WindowsOptimizationStatus.RolledBack => "REVERTIDO",
        WindowsOptimizationStatus.AlreadyOptimal => "YA ÓPTIMO",
        WindowsOptimizationStatus.Unavailable => "NO DISPONIBLE",
        _ => "DESCONOCIDO"
    };

    [JsonIgnore]
    public string EvidenceSummary => Evidence is null
        ? "Sin evidencia antes/después"
        : $"{Evidence.VerdictLabel} · {Evidence.Metrics.Count} métricas · " +
          $"{Evidence.Warnings.Count} advertencias";

    [JsonIgnore]
    public string DecisionLabel => Evidence is null
        ? "Pendiente de medición posterior"
        : Evidence.ShouldKeep
            ? "CONSERVADO"
            : Evidence.ShouldRollback
                ? "REVERTIDO"
                : "REQUIERE DECISIÓN DEL USUARIO";
}

public sealed record WindowsOptimizationResult(
    WindowsOptimizationStatus Status,
    string Message,
    WindowsOptimizationChange? Change = null);

/// <summary>
/// Planificador puro: convierte el estado detectado en propuestas explicables.
/// </summary>
public static class WindowsOptimizationPlanner
{
    public static IReadOnlyList<WindowsOptimizationItem> Build(
        WindowsOptimizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new[]
        {
            BuildGameMode(snapshot),
            BuildGameDvr(snapshot),
            BuildPowerPlan(snapshot),
            BuildStartup(snapshot),
            BuildUwpBackground(snapshot),
            BuildEdgeBackground(snapshot),
            BuildWindowsSuggestions(snapshot)
        };
    }

    private static WindowsOptimizationItem BuildGameMode(
        WindowsOptimizationSnapshot snapshot) =>
        new(
            Target: WindowsOptimizationTarget.GameMode,
            Title: "Modo Juego",
            CurrentState: snapshot.GameModeEnabled
                ? "Activado"
                : "Desactivado",
            ProposedState: snapshot.GameModeEnabled
                ? "Sin cambios (ya está activo)"
                : "Activar Modo Juego (HKCU\\Software\\Microsoft\\GameBar\\AllowAutoGameMode = 1)",
            ExpectedBenefit:
                "Windows prioriza el juego en primer plano y reduce interrupciones del planificador.",
            Risk: "Bajo. No toca Defender, firewall ni servicios del sistema.",
            RiskLevel: WindowsOptimizationRisk.Low,
            CanApply: !snapshot.GameModeEnabled,
            BlockedReason: snapshot.GameModeEnabled
                ? "El Modo Juego ya está activado."
                : null);

    private static WindowsOptimizationItem BuildGameDvr(
        WindowsOptimizationSnapshot snapshot)
    {
        bool enabled = snapshot.GameDvrEnabled ||
                       snapshot.HistoricalCaptureEnabled;

        string current = enabled
            ? $"Captura: {(snapshot.GameDvrEnabled ? "activa" : "inactiva")} · Histórico: {(snapshot.HistoricalCaptureEnabled ? "activo" : "inactivo")}"
            : "Captura en segundo plano desactivada";

        return new(
            Target: WindowsOptimizationTarget.GameDvr,
            Title: "Game DVR y capturas en segundo plano",
            CurrentState: current,
            ProposedState: enabled
                ? "Desactivar AppCaptureEnabled e HistoricalCaptureEnabled (0)"
                : "Sin cambios (ya está desactivado)",
            ExpectedBenefit:
                "Evita que Windows grabe y codifique capturas en segundo plano mientras jugás.",
            Risk: "Bajo. Se pierde la captura rápida de la Barra de juegos hasta revertir el cambio.",
            RiskLevel: WindowsOptimizationRisk.Low,
            CanApply: enabled,
            BlockedReason: enabled
                ? null
                : "La captura en segundo plano ya está desactivada.");
    }

    private static WindowsOptimizationItem BuildPowerPlan(
        WindowsOptimizationSnapshot snapshot)
    {
        bool canApply = snapshot.IsBalancedPowerPlan &&
                        snapshot.IsOnAcPower == true &&
                        snapshot.HasHighPerformancePlan;

        string? blockedReason = !snapshot.IsBalancedPowerPlan
            ? "El plan activo no es Equilibrado."
            : snapshot.IsOnAcPower != true
                ? "Se exige alimentación AC verificada; con batería o estado desconocido no se habilita un plan de mayor consumo."
                : !snapshot.HasHighPerformancePlan
                    ? "Windows no expone un plan Alto rendimiento instalado."
                    : null;

        return new(
            Target: WindowsOptimizationTarget.PowerPlan,
            Title: "Plan de energía",
            CurrentState: string.IsNullOrWhiteSpace(snapshot.PowerPlanName)
                ? "No se pudo leer el plan activo"
                : snapshot.PowerPlanName,
            ProposedState: canApply
                ? "Cambiar a Alto rendimiento"
                : "Sin cambios (no se cumplen condiciones de alimentación/compatibilidad)",
            ExpectedBenefit:
                "Puede modificar la política de escalado de rendimiento del procesador; no garantiza una mejora y debe medirse en el escenario objetivo.",
            Risk: "Medio. Mayor consumo eléctrico y temperatura; reversible en un paso.",
            RiskLevel: WindowsOptimizationRisk.Medium,
            CanApply: canApply,
            BlockedReason: blockedReason);
    }

    private static WindowsOptimizationItem BuildStartup(
        WindowsOptimizationSnapshot snapshot) =>
        new(
            Target: WindowsOptimizationTarget.StartupApp,
            Title: "Aplicaciones de inicio",
            CurrentState: snapshot.StartupCandidateCount == 0
                ? "Sin aplicaciones de inicio detectadas en HKCU"
                : $"{snapshot.StartupCandidateCount} aplicaciones detectadas en el inicio del usuario",
            ProposedState:
                "Desactivar sólo las aplicaciones que elijas de la lista (una por vez)",
            ExpectedBenefit:
                "Libera memoria y actividad de disco al iniciar sesión al quitar programas que no necesitás en segundo plano.",
            Risk: "Medio. Cada aplicación se desactiva por separado y se puede restaurar.",
            RiskLevel: WindowsOptimizationRisk.Medium,
            CanApply: false,
            BlockedReason: snapshot.StartupCandidateCount == 0
                ? "No hay aplicaciones de inicio en HKCU para revisar."
                : "Elegí una aplicación de la lista para desactivarla individualmente.");

    private static WindowsOptimizationItem BuildUwpBackground(
        WindowsOptimizationSnapshot snapshot) =>
        new(
            Target: WindowsOptimizationTarget.UwpBackground,
            Title: "Aplicaciones UWP en segundo plano",
            CurrentState: snapshot.UwpBackgroundDisabled
                ? "Acceso en segundo plano desactivado globalmente"
                : "Las aplicaciones UWP pueden ejecutarse en segundo plano",
            ProposedState: snapshot.UwpBackgroundDisabled
                ? "Sin cambios (ya está desactivado)"
                : "Desactivar BackgroundAccessApplications\\GlobalUserDisabled = 1 (HKCU)",
            ExpectedBenefit:
                "Reduce la actividad de aplicaciones de la Store mientras jugás. " +
                "Algunas aplicaciones pueden tardar más en actualizar notificaciones.",
            Risk: "Bajo. Reversible en un paso; no afecta Defender ni servicios del sistema.",
            RiskLevel: WindowsOptimizationRisk.Low,
            CanApply: !snapshot.UwpBackgroundDisabled,
            BlockedReason: snapshot.UwpBackgroundDisabled
                ? "El acceso en segundo plano de aplicaciones UWP ya está desactivado."
                : null);

    private static WindowsOptimizationItem BuildEdgeBackground(
        WindowsOptimizationSnapshot snapshot) =>
        new(
            Target: WindowsOptimizationTarget.EdgeBackground,
            Title: "Microsoft Edge en segundo plano",
            CurrentState: snapshot.EdgeBackgroundDisabled
                ? "Continuación de Edge en segundo plano desactivada"
                : "Edge puede seguir ejecutándose en segundo plano al cerrarlo",
            ProposedState: snapshot.EdgeBackgroundDisabled
                ? "Sin cambios (ya está desactivado)"
                : "Desactivar Edge\\BackgroundModeEnabled = 0 (HKCU\\Software\\Policies\\Microsoft\\Edge)",
            ExpectedBenefit:
                "Evita procesos de Edge en segundo plano. Las extensiones y " +
                "notificaciones de Edge pueden dejar de recibir actividad.",
            Risk: "Bajo. Reversible; sólo aplica a la política del usuario.",
            RiskLevel: WindowsOptimizationRisk.Low,
            CanApply: !snapshot.EdgeBackgroundDisabled,
            BlockedReason: snapshot.EdgeBackgroundDisabled
                ? "La ejecución de Edge en segundo plano ya está desactivada."
                : null);

    private static WindowsOptimizationItem BuildWindowsSuggestions(
        WindowsOptimizationSnapshot snapshot) =>
        new(
            Target: WindowsOptimizationTarget.WindowsSuggestions,
            Title: "Sugerencias y experiencias de Windows",
            CurrentState: snapshot.WindowsSuggestionsDisabled
                ? "Sugerencias y contenido promocional desactivados"
                : "Windows puede mostrar sugerencias y contenido sugerido",
            ProposedState: snapshot.WindowsSuggestionsDisabled
                ? "Sin cambios (ya está desactivado)"
                : "Desactivar sugerencias de ContentDeliveryManager (HKCU, 6 valores)",
            ExpectedBenefit:
                "Reduce actividad de contenido sugerido y notificaciones promocionales. " +
                "No promete mejoras de FPS.",
            Risk: "Bajo. Reversible; algunos mosaicos pueden quedar sin sugerencias.",
            RiskLevel: WindowsOptimizationRisk.Low,
            CanApply: !snapshot.WindowsSuggestionsDisabled,
            BlockedReason: snapshot.WindowsSuggestionsDisabled
                ? "Las sugerencias de Windows ya están desactivadas."
                : null);
}
