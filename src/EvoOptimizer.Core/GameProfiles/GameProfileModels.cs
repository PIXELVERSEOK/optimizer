namespace EvoOptimizer.Core.GameProfiles;

public enum GameProfileStatus
{
    Unconfigured,
    Ready,
    NeedsBenchmark,
    RegressionDetected
}

public enum GameProfileSettingKind
{
    Registry,
    ConfigurationFile,
    ApplicationSetting,
    Informational
}

public sealed record GameProfileSetting(
    string Id,
    string Name,
    GameProfileSettingKind Kind,
    string Target,
    string ValueName,
    string OriginalValue,
    string ProposedValue,
    bool IsReversible,
    bool RequiresRestart,
    bool RequiresApproval);

public sealed record GameProfileBenchmark(
    Guid Id,
    DateTimeOffset CapturedAt,
    string Scenario,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double AverageFrametimeMs,
    double P99FrametimeMs,
    double StutterRatePercent,
    string Source,
    bool IsBaseline);

public sealed record GameProfileDefinition(
    string Id,
    string Name,
    string ExecutableName,
    string? ExecutablePath,
    string HardwareDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    GameProfileStatus Status,
    IReadOnlyList<GameProfileSetting> Settings,
    IReadOnlyList<GameProfileBenchmark> Benchmarks,
    IReadOnlyList<string> Notes);

public sealed record GameProfileSummary(
    string Id,
    string Name,
    string ExecutableName,
    GameProfileStatus Status,
    int SettingCount,
    int BenchmarkCount,
    DateTimeOffset UpdatedAt);

public static class GameProfileRules
{
    public static bool IsValid(GameProfileDefinition profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) ||
            string.IsNullOrWhiteSpace(profile.Name) ||
            string.IsNullOrWhiteSpace(profile.ExecutableName))
            return false;

        if (profile.ExecutableName.IndexOfAny(
            Path.GetInvalidFileNameChars()) >= 0)
            return false;

        if (!profile.ExecutableName.EndsWith(
            ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        if (profile.Settings.Any(setting =>
            string.IsNullOrWhiteSpace(setting.Id) ||
            string.IsNullOrWhiteSpace(setting.Name) ||
            string.IsNullOrWhiteSpace(setting.Target)))
            return false;

        if (profile.Settings
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            return false;

        if (profile.Benchmarks.Any(x =>
            !double.IsFinite(x.AverageFps) ||
            !double.IsFinite(x.OnePercentLowFps) ||
            !double.IsFinite(x.PointOnePercentLowFps) ||
            !double.IsFinite(x.AverageFrametimeMs) ||
            !double.IsFinite(x.P99FrametimeMs) ||
            !double.IsFinite(x.StutterRatePercent) ||
            x.AverageFps < 0 ||
            x.OnePercentLowFps < 0 ||
            x.PointOnePercentLowFps < 0 ||
            x.AverageFrametimeMs < 0 ||
            x.P99FrametimeMs < 0 ||
            x.StutterRatePercent < 0 ||
            x.StutterRatePercent > 100))
            return false;

        return true;
    }

    public static GameProfileSummary ToSummary(
        GameProfileDefinition profile)
    {
        return new GameProfileSummary(
            profile.Id,
            profile.Name,
            profile.ExecutableName,
            profile.Status,
            profile.Settings.Count,
            profile.Benchmarks.Count,
            profile.UpdatedAt);
    }
}
