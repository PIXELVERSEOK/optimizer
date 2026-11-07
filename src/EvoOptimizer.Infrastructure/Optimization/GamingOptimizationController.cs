using EvoOptimizer.Core.Recovery;

namespace EvoOptimizer.Infrastructure.Optimization;

public sealed class GamingOptimizationController
{
    private const string GameDvrPath =
        @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    private const string GameBarPath =
        @"Software\Microsoft\GameBar";

    private readonly RestoreCenter _restoreCenter;

    public GamingOptimizationController(RestoreCenter restoreCenter)
    {
        ArgumentNullException.ThrowIfNull(restoreCenter);
        _restoreCenter = restoreCenter;
    }

    public Task<RestoreEntry> DisableBackgroundCaptureAsync(
        CancellationToken cancellationToken = default)
    {
        return ApplyDwordAsync(
            GameDvrPath,
            "AppCaptureEnabled",
            0,
            cancellationToken);
    }

    public Task<RestoreEntry> DisableHistoricalCaptureAsync(
        CancellationToken cancellationToken = default)
    {
        return ApplyDwordAsync(
            GameDvrPath,
            "HistoricalCaptureEnabled",
            0,
            cancellationToken);
    }

    public Task<RestoreEntry> EnableGameModeAsync(
        CancellationToken cancellationToken = default)
    {
        return ApplyDwordAsync(
            GameBarPath,
            "AutoGameModeEnabled",
            1,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RestoreEntry>> ApplyGamingProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<RestoreEntry>();

        results.Add(await DisableBackgroundCaptureAsync(cancellationToken));
        results.Add(await DisableHistoricalCaptureAsync(cancellationToken));
        results.Add(await EnableGameModeAsync(cancellationToken));

        return results;
    }

    public Task<bool> RestoreAsync(
        Guid changeId,
        CancellationToken cancellationToken = default)
    {
        return _restoreCenter.RestoreAsync(changeId, cancellationToken);
    }

    public Task<RestoreJournal> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        return _restoreCenter.GetJournalAsync(cancellationToken);
    }

    private Task<RestoreEntry> ApplyDwordAsync(
        string path,
        string name,
        int value,
        CancellationToken cancellationToken)
    {
        return _restoreCenter.ApplyAsync(
            path,
            name,
            new StoredSetting(
                true,
                StoredValueKind.DWord,
                value.ToString()),
            cancellationToken);
    }
}
