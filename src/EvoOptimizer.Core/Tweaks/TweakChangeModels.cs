namespace EvoOptimizer.Core.Tweaks;

public enum TweakChangeStatus
{
    /// <summary>Registrado antes de escribir (write-ahead). Puede estar a medio aplicar.</summary>
    Pending,
    Applied,
    Verified,
    RolledBack,
    Failed
}

/// <summary>
/// Registro persistido de un tweak aplicado. Guarda el valor original para que
/// el rollback sea posible incluso si la app se cerró en el medio.
/// </summary>
public sealed record TweakChange(
    string Id,
    string DefinitionId,
    string Hive,
    string KeyPath,
    string ValueName,
    string OriginalValue,
    string? DesiredValue,
    TweakValueKind OriginalKind,
    TweakChangeStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Error)
{
    public string Location => $"{Hive}\\{KeyPath}\\{ValueName}";

    public bool IsActive => Status is TweakChangeStatus.Pending
        or TweakChangeStatus.Applied
        or TweakChangeStatus.Verified;
}

public enum TweakValueKind
{
    None,
    Dword,
    String,
    Other
}
