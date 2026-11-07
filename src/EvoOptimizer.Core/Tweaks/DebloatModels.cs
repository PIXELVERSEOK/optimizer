namespace EvoOptimizer.Core.Tweaks;

/// <summary>
/// Estado de una aplicación UWP frente al escáner de debloat. Nunca se inventa:
/// sin escaneo previo todo queda <see cref="NotScanned"/> y la UI lo muestra como
/// UNKNOWN en lugar de asumir que está instalada o ausente.
/// </summary>
public enum DebloatState
{
    NotScanned,
    Installed,
    Removed,
    Protected,
    NotInstalled,
    Unknown
}

/// <summary>
/// Paquete UWP detectado por <c>Get-AppxPackage</c>. Se conserva el
/// <c>PackageFullName</c> y la <c>InstallLocation</c> porque son los datos
/// necesarios para volver a registrar la app después de quitarla.
/// </summary>
public sealed record InstalledUwpApp(
    string PackageName,
    string PackageFullName,
    string? InstallLocation,
    string? Version,
    string? Publisher)
{
    public bool CanRestore => !string.IsNullOrWhiteSpace(InstallLocation);

    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "UNKNOWN" : Version;

    public string LocationLabel =>
        string.IsNullOrWhiteSpace(InstallLocation) ? "UNKNOWN" : InstallLocation!;
}

/// <summary>
/// Definición pura de una app eliminable. No ejecuta nada: describe el prefijo
/// real del paquete, por qué se ofrece quitarla y qué riesgo tiene.
/// </summary>
public sealed record DebloatAppDefinition(
    string Id,
    string NamePrefix,
    string DisplayName,
    string Reason,
    TweakRisk Risk,
    string ExpectedBenefit,
    string ReinstallHint)
{
    public string RiskLabel => Risk switch
    {
        TweakRisk.Low => "Riesgo bajo",
        TweakRisk.Medium => "Riesgo medio",
        TweakRisk.High => "Riesgo alto",
        _ => "Riesgo desconocido"
    };

    public string CategoryLabel => "DEBLOAT";
}

/// <summary>Resultado de cruzar una definición con el escaneo real de paquetes.</summary>
public sealed record DebloatAppStatus(
    DebloatAppDefinition Definition,
    InstalledUwpApp? InstalledApp,
    DebloatState State,
    string StatusDetail,
    DateTimeOffset? ScannedAt)
{
    public string Id => Definition.Id;

    public string Name => Definition.DisplayName;

    public string CategoryLabel => Definition.CategoryLabel;

    public string RiskLabel => Definition.RiskLabel;

    public bool IsInstalled => State == DebloatState.Installed;

    public bool CanRemove => State == DebloatState.Installed;

    public bool CanRestore => State == DebloatState.Removed &&
                              InstalledApp is { CanRestore: true };

    public string VersionLabel => InstalledApp?.VersionLabel ?? "UNKNOWN";

    public string PackageLabel => InstalledApp?.PackageFullName ?? "No detectado en este usuario";

    public string StateLabel => State switch
    {
        DebloatState.Installed => "INSTALADA · removable",
        DebloatState.Removed => "REMOVIDA POR EVO",
        DebloatState.Protected => "PROTEGIDA · no se toca",
        DebloatState.NotInstalled => "NO INSTALADA",
        DebloatState.Unknown => "UNKNOWN",
        _ => "SIN ESCANEO"
    };

    public string StateColor => State switch
    {
        DebloatState.Installed => "#2F81F7",
        DebloatState.Removed => "#3FB950",
        DebloatState.Protected => "#6E7681",
        DebloatState.NotInstalled => "#6E7681",
        DebloatState.Unknown => "#8B949E",
        _ => "#8B949E"
    };

    public string ScannedAtLabel => ScannedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        ?? "Nunca escaneado";
}

/// <summary>Registro persistido de una app quitada, suficiente para volver a registrarla.</summary>
public sealed record DebloatChange(
    string Id,
    string DefinitionId,
    string PackageName,
    string PackageFullName,
    string? InstallLocation,
    string? Version,
    DebloatChangeStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Error)
{
    public bool CanRegister => !string.IsNullOrWhiteSpace(InstallLocation);
}

public enum DebloatChangeStatus
{
    Pending,
    Removed,
    Restored,
    Failed
}
