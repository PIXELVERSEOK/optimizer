namespace EvoOptimizer.Core.Gaming;

/// <summary>
/// Política de protección de procesos del Game Booster. Ninguna regla de
/// usuario puede saltarla: EVO nunca finaliza procesos críticos de Windows,
/// seguridad/antivirus, anti-cheats, controladores, audio/red/periféricos,
/// launchers necesarios para el juego ni a sí mismo.
/// </summary>
public static class BoostProcessPolicy
{
    private static readonly HashSet<string> ProtectedNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        // Núcleo de Windows y shell ( coincide con ProcessGovernanceService ).
        "system", "idle", "registry", "memory compression", "csrss",
        "wininit", "winlogon", "services", "lsass", "smss", "svchost",
        "dwm", "fontdrvhost", "audiodg", "sihost", "taskhostw", "ctfmon",
        "explorer", "wudfhost", "securityhealthservice", "securityhealthsystray",
        "conhost", "dllhost", "wmiprvse", "spoolsv", "searchindexer",
        "searchprotocolhost", "runtimebroker", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "applicationframehost",
        "systemsettings", "lockapp", "logonui", "userinit", "winlogon",

        // Seguridad / antivirus / antimalware.
        "msmpeng", "mpdefendercoreservice", "nissrv", "mssense",
        "avp", "kavfs", "avpui", "ekrn", "egui", "efwd",
        "bdagent", "vsserv", "updatesrv", "bdservicehost",
        "mcshield", "masvc", "mfeann", "mfemms", "mfevtps",
        "ns", "nortonsecurity", "wrsa", "savservice", "sophosfs",
        "mbamservice", "mbamtray", "csfalconservice", "falcon",
        "sentinelagent", "sentinelservicehost", "sentinelstaticscanner",
        "cbdefense", "cylancesvc", "cyserver", "ntrtscan", "tmccsf",
        "coreserviceshell", "dsagent", "avgsvc", "avastsvc", "aswidsagent",

        // Anti-cheats y servicios exigidos por juegos.
        "easyanticheat", "easyanticheat_eos", "beservice", "beclient",
        "vgc", "vgk", "faceitservice", "faceitclient",
        "pbsvc", "pnkbstra", "pnkbstrb", "anticheatexpert",
        "ace-service", "sguard", "xigncode", "npggsvc", "npsc",

        // Audio, red y periféricos.
        "ravbg64", "ravcpl64", "rtkauduservice64", "rtkngui64",
        "nahimic", "nahimicservice", "nahimiclauncher", "a-volute",
        "sonicstudio", "sonicradar", "wavesys", "maxxaudio",
        "corsair.service", "icue", "cue", "lghub", "lghub_agent",
        "lghub_updater", "logi_lamparray", "razercentral", "rzservice",
        "rzsynapse", "steelseriesgg", "steelseriesengine",
        "hidfind", "hkcmd", "igfxtray", "igfxext", "igfxsrvc",
        "nvcontainer", "nvidia web helper", "nvdisplay.container",
        "amdrsserv", "amdfendrsr", "atieclxx",

        // Launchers y servicios de juego (necesarios durante la sesión).
        "steam", "steamwebhelper", "steamservice", "gameoverlayui",
        "epicgameslauncher", "epicwebhelper", "eabackgroundservice",
        "eadesktop", "galaxyclient", "goggalaxy", "galaxycommunication",
        "upc", "ubisoftconnect", "uplayservice", "battle.net",
        "agent", "blizzard update agent", "origin", "erl",
        "riotclientservices", "riotclient", "valorant", "leagueclient",

        // EVO y su infraestructura.
        "evooptimizer.app", "evooptimizer", "presentmon", "dotnet"
    };

    /// <summary>Prefijos protegidos adicionales (variantes de servicios).</summary>
    private static readonly string[] ProtectedPrefixes =
    [
        "msmpeng", "securityhealth", "easyanticheat", "beservice",
        "faceit", "sophos", "sentinel", "avg", "avast", "bitdefender",
        "mcafee", "norton", "webroot", "eset_", "kaspersky",
        "nvdisplay", "amdfendr", "rtkaudio"
    ];

    public static bool IsProtected(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return true;

        string name = Normalize(processName);

        if (ProtectedNames.Contains(name))
            return true;

        return ProtectedPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Un proceso es candidato a cierre sólo si no está protegido, no es el
    /// juego objetivo, no está en la lista de exclusión de la usuaria y no es
    /// un proceso del sistema sin memoria de usuario relevante.
    /// </summary>
    public static bool IsCloseCandidate(
        string? processName,
        string? gameExecutableName,
        IReadOnlyCollection<string>? userExclusions)
    {
        if (IsProtected(processName))
            return false;

        string name = Normalize(processName!);

        if (!string.IsNullOrWhiteSpace(gameExecutableName) &&
            string.Equals(name, Normalize(gameExecutableName),
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (userExclusions is not null &&
            userExclusions.Any(exclusion =>
                string.Equals(Normalize(exclusion), name,
                    StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
}