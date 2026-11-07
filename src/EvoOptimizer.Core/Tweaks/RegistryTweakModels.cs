namespace EvoOptimizer.Core.Tweaks;

/// <summary>Categorías de tweaks orientadas al brief original (FPS/latencia/debloat).</summary>
public enum TweakCategory
{
    Gaming,
    Fps,
    Input,
    Network,
    Power,
    Visual,
    Memory,
    Storage,
    System
}

public enum RegistryHiveChoice
{
    CurrentUser,
    LocalMachine
}

public enum RegistryValueChoice
{
    Dword,
    String
}

public enum TweakRisk
{
    Low,
    Medium,
    High
}

/// <summary>Estado en tiempo real de un tweak leído del registro.</summary>
public enum TweakState
{
    Unknown,
    Available,
    Applied,
    NotApplicable,
    RequiresElevation,
    Blocked,
    Failed
}

/// <summary>
/// Definición pura de un tweak de registro. No ejecuta nada: describe la clave,
/// el valor deseado, el riesgo y si requiere elevación o reinicio.
/// </summary>
public sealed record RegistryTweakDefinition(
    string Id,
    string Name,
    string Description,
    TweakCategory Category,
    RegistryHiveChoice Hive,
    string KeyPath,
    string ValueName,
    RegistryValueChoice ValueKind,
    string DesiredValue,
    string? DefaultHint,
    TweakRisk Risk,
    string RiskNote,
    string ExpectedBenefit,
    bool RequiresReboot,
    string Compatibility)
{
    public bool RequiresElevation => Hive == RegistryHiveChoice.LocalMachine;

    public string HiveLabel => Hive == RegistryHiveChoice.CurrentUser ? "HKCU" : "HKLM";

    public string RegistryLocation => $"{HiveLabel}\\{KeyPath}\\{ValueName}";

    public string CategoryLabel => Category switch
    {
        TweakCategory.Gaming => "GAMING",
        TweakCategory.Fps => "FPS / LATENCIA",
        TweakCategory.Input => "INPUT",
        TweakCategory.Network => "RED",
        TweakCategory.Power => "ENERGÍA",
        TweakCategory.Visual => "VISUAL",
        TweakCategory.Memory => "MEMORIA",
        TweakCategory.Storage => "ALMACENAMIENTO",
        TweakCategory.System => "SISTEMA",
        _ => Category.ToString().ToUpperInvariant()
    };

    public string RiskLabel => Risk switch
    {
        TweakRisk.Low => "Riesgo bajo",
        TweakRisk.Medium => "Riesgo medio",
        TweakRisk.High => "Riesgo alto",
        _ => "Riesgo desconocido"
    };
}

/// <summary>Resultado de leer un tweak contra el registro real del equipo.</summary>
public sealed record RegistryTweakStatus(
    RegistryTweakDefinition Definition,
    string? CurrentValue,
    bool ReadSucceeded,
    TweakState State,
    string StatusDetail,
    DateTimeOffset ReadAt,
    string EvidenceSource)
{
    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string CategoryLabel => Definition.CategoryLabel;

    public bool IsApplied => State == TweakState.Applied;

    public bool CanApply => State is TweakState.Available or TweakState.RequiresElevation;

    public string StateLabel => State switch
    {
        TweakState.Available => "DISPONIBLE",
        TweakState.Applied => "APLICADO",
        TweakState.NotApplicable => "NO APLICA",
        TweakState.RequiresElevation => "REQUIERE ADMINISTRADOR",
        TweakState.Blocked => "BLOQUEADO POR POLÍTICA EVO",
        TweakState.Failed => "FALLIDO",
        _ => "UNKNOWN"
    };

    public string StateColor => State switch
    {
        TweakState.Available => "#2F81F7",
        TweakState.Applied => "#3FB950",
        TweakState.RequiresElevation => "#D29922",
        TweakState.Blocked => "#F85149",
        TweakState.Failed => "#F85149",
        TweakState.NotApplicable => "#6E7681",
        _ => "#8B949E"
    };

    public string CurrentValueLabel => !ReadSucceeded
        ? "UNKNOWN (no se pudo leer)"
        : CurrentValue is null
            ? "No existe (valor por defecto de Windows)"
            : CurrentValue;

    public string ElevationLabel => Definition.RequiresElevation
        ? "HKLM · requiere UAC"
        : "HKCU · sin elevación";

    public string RebootLabel => Definition.RequiresReboot
        ? "Requiere reinicio"
        : "Sin reinicio";
}

/// <summary>
/// Catálogo puro de tweaks de registro. Las claves prohibidas por la política de
/// seguridad de EVO (Defender, firewall, servicios críticos) nunca se listan y,
/// si aparecieran, quedan bloqueadas en la evaluación.
/// </summary>
public static class RegistryTweakCatalog
{
    private static readonly string[] BlockedPathFragments =
    {
        @"Windows Defender",
        @"WindowsDefender",
        @"Firewall",
        @"Windows Firewall",
        @"SharedAccess",
        @"Policies\Microsoft\Windows\Safer",
        @"BitLocker"
    };

    /// <summary>
    /// Nombres de valor que permitirían deshabilitar o reconfigurar un servicio.
    /// EVO no los escribe nunca, aunque la clave sea legible.
    /// </summary>
    private static readonly string[] BlockedValueNames =
    {
        "Start",
        "ImagePath",
        "DelayedAutoStart",
        "FailureActions",
        "Type",
        "ObjectName",
        "RequiredPrivileges"
    };

    /// <summary>
    /// Verdadero si la clave toca zonas protegidas. Se evalúa siempre, incluso
    /// sobre definiciones agregadas en el futuro.
    /// </summary>
    public static bool IsBlockedPath(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            return true;

        return BlockedPathFragments.Any(fragment =>
            keyPath.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bloquea valores capaces de desactivar o reconfigurar servicios.</summary>
    public static bool IsBlockedValue(string valueName) =>
        BlockedValueNames.Any(blocked =>
            string.Equals(valueName, blocked, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comprobación completa de política para una definición.</summary>
    public static bool IsBlocked(RegistryTweakDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return IsBlockedPath(definition.KeyPath) ||
               IsBlockedValue(definition.ValueName);
    }

    public static RegistryTweakStatus Evaluate(
        RegistryTweakDefinition definition,
        string? currentValue,
        bool readSucceeded,
        bool isElevated,
        bool hasActiveChange,
        DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string evidence = $"Registro de Windows · {definition.RegistryLocation}";

        if (IsBlocked(definition))
        {
            return new RegistryTweakStatus(
                definition,
                currentValue,
                readSucceeded,
                TweakState.Blocked,
                "Política EVO: no se modifican claves de Defender, firewall ni servicios.",
                readAt,
                evidence);
        }

        bool matchesDesired = readSucceeded &&
            string.Equals(
                currentValue,
                definition.DesiredValue,
                StringComparison.OrdinalIgnoreCase);

        if (matchesDesired)
        {
            return new RegistryTweakStatus(
                definition,
                currentValue,
                true,
                TweakState.Applied,
                hasActiveChange
                    ? "Aplicado por EVO. Podés revertirlo al valor original registrado."
                    : "El valor ya coincide con el deseado (puede ser de Windows o de otra herramienta).",
                readAt,
                evidence);
        }

        if (definition.RequiresElevation && !isElevated)
        {
            return new RegistryTweakStatus(
                definition,
                currentValue,
                readSucceeded,
                TweakState.RequiresElevation,
                "HKLM: EVO pedirá autorización de administrador (UAC) al aplicar. " +
                "Sin elevación no se escribe nada.",
                readAt,
                evidence);
        }

        return new RegistryTweakStatus(
            definition,
            currentValue,
            readSucceeded,
            readSucceeded ? TweakState.Available : TweakState.Unknown,
            readSucceeded
                ? $"Valor actual: {(currentValue ?? "inexistente")} → deseado: {definition.DesiredValue}."
                : "No se pudo leer la clave; el estado real queda UNKNOWN.",
            readAt,
            evidence);
    }
}