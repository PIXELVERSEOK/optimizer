namespace EvoOptimizer.Core.Recovery;

public enum StoredValueKind
{
    DWord,
    QWord,
    String
}

public sealed record StoredSetting(
    bool Exists,
    StoredValueKind Kind,
    string? Value);

public sealed record RestoreEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Category,
    string SettingPath,
    string ValueName,
    StoredSetting OriginalValue,
    StoredSetting RequestedValue,
    string Status,
    string? Error);

public sealed record RestoreJournal(
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RestoreEntry> Entries);

public interface ISettingStore
{
    Task<StoredSetting> ReadAsync(
        string path,
        string valueName,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        string path,
        string valueName,
        StoredSetting value,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string path,
        string valueName,
        CancellationToken cancellationToken = default);
}
