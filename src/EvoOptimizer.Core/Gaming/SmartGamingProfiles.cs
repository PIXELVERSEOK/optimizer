using System.Diagnostics;
using EvoOptimizer.Core.Hardware;

namespace EvoOptimizer.Core.Gaming;

public enum GamingProfileKind
{
    Competitive,
    Balanced,
    MaxPerformance
}

/// <summary>
/// Perfil gaming con ajustes reales y acotados. Ningún perfil usa prioridad
/// High/Realtime ni afinidades arbitrarias: la afinidad sólo se propone cuando
/// la topología híbrida es confiable y el motor la resuelve contra los núcleos
/// reales del equipo.
/// </summary>
public sealed record SmartGamingProfile(
    GamingProfileKind Kind,
    string Name,
    string Description,
    ProcessPriorityClass GamePriority,
    GamingAffinityMode? PreferredAffinity,
    bool DisableGameDvr,
    bool EnableGameMode,
    bool RequestHighPerformancePowerPlan,
    bool ThrottleBackgroundApps,
    IReadOnlyList<string> Rationale);

public static class SmartGamingProfiles
{
    public static IReadOnlyList<SmartGamingProfile> All { get; } =
    [
        new(
            GamingProfileKind.Competitive,
            "COMPETITIVO",
            "Minimiza la variabilidad de frametime: prioridad AboveNormal, P-Cores exclusivos en CPU híbrida, captura de Windows desactivada y plan de alto rendimiento sólo con corriente alterna.",
            ProcessPriorityClass.AboveNormal,
            GamingAffinityMode.PerformanceCoresOnly,
            DisableGameDvr: true,
            EnableGameMode: true,
            RequestHighPerformancePowerPlan: true,
            ThrottleBackgroundApps: true,
            Rationale:
            [
                "AboveNormal mejora la planificación del juego sin usar High/Realtime (que pueden congelar el sistema).",
                "En CPU híbridas, limitar el juego a P-Cores evita migraciones a E-Cores que disparan los 1% lows.",
                "GameDVR consume hilos de CPU y memoria en segundo plano; desactivarlo es reversible.",
                "El plan de alto rendimiento sólo se solicita con AC conectada; en batería no se fuerza."
            ]),
        new(
            GamingProfileKind.Balanced,
            "EQUILIBRADO",
            "Prioridad AboveNormal y afinidad balanceada (P-Cores + E-Cores auxiliares) conservando el plan de energía actual. Recomendado para uso mixto y portátiles en batería.",
            ProcessPriorityClass.AboveNormal,
            GamingAffinityMode.PerformanceAndLightEfficiency,
            DisableGameDvr: true,
            EnableGameMode: true,
            RequestHighPerformancePowerPlan: false,
            ThrottleBackgroundApps: false,
            Rationale:
            [
                "Mantiene el plan de energía actual: no sacrifica autonomía ni comportamiento térmico.",
                "La afinidad balanceada deja hilos secundarios en E-Cores sin forzar al juego fuera de ellos.",
                "No baja la prioridad de aplicaciones de fondo automáticamente."
            ]),
        new(
            GamingProfileKind.MaxPerformance,
            "MÁXIMO RENDIMIENTO",
            "Prioridad AboveNormal, todos los núcleos disponibles, plan de alto rendimiento (sólo AC) y reducción de carga en segundo plano autorizada por la usuaria.",
            ProcessPriorityClass.AboveNormal,
            PreferredAffinity: null,
            DisableGameDvr: true,
            EnableGameMode: true,
            RequestHighPerformancePowerPlan: true,
            ThrottleBackgroundApps: true,
            Rationale:
            [
                "No restringe la afinidad: el juego puede usar todos los núcleos (útil en cargas multi-hilo pesadas).",
                "El plan de alto rendimiento reduce la latencia de escalado de frecuencia; se restaura al terminar.",
                "El throttle de fondo sólo se aplica a procesos autorizados y nunca a procesos protegidos."
            ])
    ];

    public static SmartGamingProfile Get(GamingProfileKind kind) =>
        All.First(profile => profile.Kind == kind);

    /// <summary>
    /// Resuelve la afinidad realmente aplicable: si la topología no es híbrida
    /// o no es confiable, no se propone ninguna afinidad (null) y se explica.
    /// </summary>
    public static GamingAffinityMode? ResolveAffinity(
        SmartGamingProfile profile,
        bool isHybridTopology,
        bool topologyReliable,
        int performanceCores)
    {
        if (profile.PreferredAffinity is null)
            return null;

        if (!topologyReliable || !isHybridTopology || performanceCores <= 0)
            return null;

        return profile.PreferredAffinity;
    }
}

/// <summary>
/// Foto del entorno gaming con datos medidos. Los campos nullable significan
/// "no disponible", nunca un valor asumido.
/// </summary>
public sealed record GamingEnvironmentSnapshot(
    bool IsHybridTopology,
    bool TopologyReliable,
    int PerformanceCores,
    int LogicalProcessors,
    double? TotalRamMb,
    double? AvailableRamMb,
    bool GameDvrEnabled,
    bool GameModeEnabled,
    bool? IsOnAcPower,
    bool HasHighPerformancePlan,
    bool IsBalancedPowerPlan,
    string PowerPlanName);

public sealed record GamingRecommendation(
    string Id,
    string Title,
    string Explanation,
    string Severity,
    bool IsActionable);

/// <summary>
/// Recomendaciones específicas según CPU, GPU/energía y memoria detectadas.
/// Cada recomendación cita la evidencia que la dispara; no se proponen ajustes
/// universales sin compatibilidad verificada.
/// </summary>
public static class GamingRecommendationEngine
{
    public static (GamingProfileKind Profile, string Rationale) RecommendProfile(
        GamingEnvironmentSnapshot env)
    {
        ArgumentNullException.ThrowIfNull(env);

        bool onBattery = env.IsOnAcPower == false;

        if (onBattery)
        {
            return (GamingProfileKind.Balanced,
                "El equipo está en batería: se recomienda EQUILIBRADO para no forzar el plan de alto rendimiento ni sacrificar autonomía.");
        }

        if (env.IsHybridTopology && env.TopologyReliable && env.PerformanceCores > 0)
        {
            return (GamingProfileKind.Competitive,
                $"CPU híbrida confiable ({env.PerformanceCores} P-Cores): COMPETITIVO puede aislar el juego en P-Cores para reducir la variabilidad de frametime.");
        }

        return (GamingProfileKind.Balanced,
            "Sin topología híbrida confiable no se propone afinidad exclusiva; EQUILIBRADO aporta prioridad y configuración gaming sin restringir núcleos.");
    }

    public static IReadOnlyList<GamingRecommendation> Evaluate(
        GamingEnvironmentSnapshot env,
        SmartGamingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(profile);

        var recommendations = new List<GamingRecommendation>();

        if (env.GameDvrEnabled && profile.DisableGameDvr)
        {
            recommendations.Add(new(
                "GAMING_GAMEDVR_ACTIVE",
                "Captura en segundo plano de Windows (GameDVR) activa",
                "AppCaptureEnabled=1 en el registro. Consume CPU/memoria mientras jugás. El perfil propone desactivarla de forma reversible.",
                "Warning",
                IsActionable: true));
        }

        if (!env.GameModeEnabled && profile.EnableGameMode)
        {
            recommendations.Add(new(
                "GAMING_GAMEMODE_DISABLED",
                "Modo Juego de Windows desactivado",
                "AllowAutoGameMode=0. El perfil propone activarlo para que Windows priorice el juego y posponga tareas en segundo plano.",
                "Warning",
                IsActionable: true));
        }

        if (profile.RequestHighPerformancePowerPlan)
        {
            if (env.IsOnAcPower == false)
            {
                recommendations.Add(new(
                    "GAMING_POWER_ON_BATTERY",
                    "Plan de alto rendimiento no aplicado: equipo en batería",
                    "El perfil solicita alto rendimiento pero el equipo está en batería (DC). EVO no fuerza el plan para proteger autonomía y temperatura.",
                    "Info",
                    IsActionable: false));
            }
            else if (!env.HasHighPerformancePlan)
            {
                recommendations.Add(new(
                    "GAMING_POWER_PLAN_MISSING",
                    "Windows no expone un plan de Alto rendimiento",
                    "powercfg /list no lista un plan de alto rendimiento en este equipo. EVO no inventa GUIDs ni crea planes sin autorización.",
                    "Info",
                    IsActionable: false));
            }
            else if (env.IsBalancedPowerPlan)
            {
                recommendations.Add(new(
                    "GAMING_POWER_BALANCED",
                    $"Plan activo: {env.PowerPlanName}",
                    "Con AC conectada, cambiar temporalmente a Alto rendimiento reduce la latencia de escalado de frecuencia. El plan original se guarda y se restaura al terminar el Boost.",
                    "Action",
                    IsActionable: true));
            }
        }

        var affinity = SmartGamingProfiles.ResolveAffinity(
            profile, env.IsHybridTopology, env.TopologyReliable, env.PerformanceCores);

        if (profile.PreferredAffinity is not null && affinity is null)
        {
            recommendations.Add(new(
                "GAMING_AFFINITY_NOT_APPLICABLE",
                "Afinidad exclusiva no aplicable en esta CPU",
                env.TopologyReliable
                    ? "La topología detectada no es híbrida (o no reporta P-Cores). Restringir la afinidad no aportaría beneficio y podría reducir el rendimiento."
                    : "La topología de CPU no es confiable. EVO no aplica afinidades arbitrarias para proteger la estabilidad.",
                "Info",
                IsActionable: false));
        }
        else if (affinity is not null)
        {
            recommendations.Add(new(
                "GAMING_AFFINITY_AVAILABLE",
                $"Afinidad recomendada: {affinity}",
                $"Topología híbrida confiable con {env.PerformanceCores} P-Cores. La afinidad se aplica sólo al ejecutable del juego seleccionado y es reversible.",
                "Action",
                IsActionable: true));
        }

        if (env.AvailableRamMb is < 2048)
        {
            recommendations.Add(new(
                "GAMING_LOW_AVAILABLE_RAM",
                $"Memoria disponible baja: {env.AvailableRamMb:F0} MB",
                "Con menos de 2 GB libres, cerrar aplicaciones de fondo autorizadas puede evitar presión de memoria y paginación durante la partida. EVO no vacía cachés útiles de Windows.",
                "Warning",
                IsActionable: true));
        }

        if (profile.ThrottleBackgroundApps)
        {
            recommendations.Add(new(
                "GAMING_BACKGROUND_THROTTLE",
                "Reducción de carga en segundo plano disponible",
                "El Boost puede bajar la prioridad (BelowNormal) de las aplicaciones que autorices y cerrar las que selecciones explícitamente. Nunca toca procesos críticos, seguridad ni anti-cheat.",
                "Action",
                IsActionable: true));
        }

        return recommendations;
    }
}