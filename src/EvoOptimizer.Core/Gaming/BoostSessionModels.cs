namespace EvoOptimizer.Core.Gaming;

public enum BoostSessionStatus
{
    Active,
    Restored,
    Interrupted,
    Failed
}

public enum BoostActionKind
{
    PowerPlan,
    GameDvrCapture,
    GameDvrHistorical,
    GameMode,
    BackgroundPriority,
    ProcessClose,
    GamePriority,
    GameAffinity
}

public enum BoostActionStatus
{
    Applied,
    Restored,
    Failed,
    Skipped,
    Conflict
}

/// <summary>
/// Acción individual del Boost con todo lo necesario para restaurarla:
/// valor original, identidad del proceso (PID + ticks de inicio) y datos de
/// relanzamiento cuando EVO cerró una aplicación autorizada.
/// </summary>
public sealed record BoostActionRecord(
    Guid Id,
    BoostActionKind Kind,
    string Target,
    string DisplayName,
    string? OriginalValue,
    string? AppliedValue,
    BoostActionStatus Status,
    string? Error,
    DateTimeOffset AppliedAt,
    DateTimeOffset? RestoredAt)
{
    public int? ProcessId { get; init; }
    public long? ProcessStartTimeUtcTicks { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Arguments { get; init; }
    public bool RelaunchAuthorized { get; init; }
    /// <summary>Id de entrada en el RestoreCenter (ajustes de registro).</summary>
    public Guid? RestoreEntryId { get; init; }
    /// <summary>Id de token del optimizador de prioridad/afinidad del juego.</summary>
    public Guid? OptimizerTokenId { get; init; }
}

/// <summary>
/// Sesión de Boost persistente. Se escribe ANTES de tocar el sistema
/// (write-ahead) para poder recuperarla tras un cierre inesperado de EVO.
/// </summary>
public sealed record BoostSession(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? GameName,
    string? GameExecutablePath,
    GamingProfileKind ProfileKind,
    BoostSessionStatus Status,
    double? BeforeCpuPercent,
    double? BeforeAvailableMemoryMb,
    double? AfterCpuPercent,
    double? AfterAvailableMemoryMb,
    bool AutoRestoreOnGameExit,
    IReadOnlyList<BoostActionRecord> Actions,
    IReadOnlyList<string> Notes);

public static class BoostSessionRules
{
    public static bool IsValid(BoostSession session)
    {
        if (session.Id == Guid.Empty)
            return false;

        if (session.Status == BoostSessionStatus.Active && session.EndedAt is not null)
            return false;

        if (session.Actions.Any(action => action.Id == Guid.Empty))
            return false;

        if (session.Actions
            .GroupBy(action => action.Id)
            .Any(group => group.Count() > 1))
            return false;

        return true;
    }

    public static bool HasPendingRestore(BoostSession session) =>
        session.Status is BoostSessionStatus.Active or BoostSessionStatus.Interrupted &&
        session.Actions.Any(action => action.Status == BoostActionStatus.Applied);

    public static BoostSession UpsertAction(
        BoostSession session,
        BoostActionRecord action)
    {
        var actions = session.Actions.ToList();
        int index = actions.FindIndex(existing => existing.Id == action.Id);

        if (index >= 0)
            actions[index] = action;
        else
            actions.Add(action);

        return session with { Actions = actions };
    }

    public static BoostSession AddNote(BoostSession session, string note) =>
        session with { Notes = session.Notes.Append(note).ToArray() };
}