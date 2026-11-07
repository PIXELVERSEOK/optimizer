namespace EvoOptimizer.Core.Optimization;

public enum RegressionStatus
{
    Healthy,
    Suspected,
    Confirmed,
    Inconclusive
}

public sealed record RegressionMeasurement(
    DateTimeOffset CapturedAt,
    string Scenario,
    string Resolution,
    string Source,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double P99FrametimeMs,
    double StutterRatePercent,
    double DurationSeconds,
    int FrameCount);

public sealed record RegressionWatch(
    Guid Id,
    string OptimizationId,
    string OptimizationName,
    Guid? RestoreEntryId,
    RegressionMeasurement Baseline,
    IReadOnlyList<RegressionMeasurement> RecentMeasurements);

public sealed record RegressionMetric(
    string Name,
    double Baseline,
    double Current,
    double ChangePercent,
    bool IsRegression,
    string Explanation);

public sealed record RegressionGuardResult(
    Guid WatchId,
    string OptimizationId,
    RegressionStatus Status,
    int ConsecutiveRegressions,
    bool RestoreAvailable,
    bool RestoreRecommended,
    double RegressionScore,
    IReadOnlyList<RegressionMetric> Metrics,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings);

public sealed class RegressionGuard
{
    private const double LowFpsRegressionThreshold = 5.0;
    private const double P99RegressionThreshold = 10.0;
    private const double StutterRegressionThreshold = 2.0;

    public RegressionGuardResult Evaluate(RegressionWatch watch)
    {
        ArgumentNullException.ThrowIfNull(watch);

        var warnings = new List<string>();
        var evidence = new List<string>();

        if (string.IsNullOrWhiteSpace(watch.OptimizationId))
            warnings.Add("Falta el identificador de optimización.");

        if (string.IsNullOrWhiteSpace(watch.OptimizationName))
            warnings.Add("Falta el nombre de optimización.");

        if (!IsValid(watch.Baseline))
            warnings.Add("La medición de referencia no es válida.");

        var recentMeasurements = watch.RecentMeasurements
            ?? Array.Empty<RegressionMeasurement>();

        if (recentMeasurements.Count == 0)
        {
            warnings.Add("Todavía no existen mediciones posteriores.");
        }

        if (warnings.Count > 0)
            return Inconclusive(watch.Id, watch.OptimizationId, warnings);

        var comparable = recentMeasurements
            .Where(x => IsValid(x) && IsComparable(watch.Baseline, x))
            .OrderBy(x => x.CapturedAt)
            .ToArray();

        if (comparable.Length == 0)
        {
            warnings.Add(
                "No hay mediciones comparables con la referencia.");

            return Inconclusive(
                watch.Id, watch.OptimizationId, warnings);
        }

        var latest = comparable[^1];

        if (latest.DurationSeconds < 30 || latest.FrameCount < 1000)
        {
            warnings.Add(
                "La última captura no alcanza 30 segundos y 1000 frames.");

            return Inconclusive(
                watch.Id, watch.OptimizationId, warnings);
        }

        var metrics = Compare(watch.Baseline, latest);
        var score = CalculateScore(metrics);

        var consecutive = CountConsecutiveRegressions(
            watch.Baseline,
            comparable);

        var status = consecutive >= 2
            ? RegressionStatus.Confirmed
            : consecutive == 1
                ? RegressionStatus.Suspected
                : RegressionStatus.Healthy;

        evidence.Add(
            $"Mediciones comparables: {comparable.Length}.");

        evidence.Add(
            $"Regresiones consecutivas: {consecutive}.");

        evidence.Add(
            $"Puntuación de regresión: {score:0.0}/100.");

        evidence.Add(
            $"Optimización evaluada: {watch.OptimizationName}.");

        if (status == RegressionStatus.Suspected)
        {
            evidence.Add(
                "Una medición indica deterioro. Se necesita otra captura para confirmarlo.");
        }
        else if (status == RegressionStatus.Confirmed)
        {
            evidence.Add(
                "Dos o más mediciones consecutivas confirman el deterioro.");
        }
        else
        {
            evidence.Add(
                "No se detecta una regresión sostenida.");
        }

        var restoreAvailable = watch.RestoreEntryId.HasValue;

        var restoreRecommended =
            status == RegressionStatus.Confirmed &&
            restoreAvailable;

        if (status == RegressionStatus.Confirmed && !restoreAvailable)
        {
            warnings.Add(
                "No existe una entrada de Restore Center asociada. No se puede preparar una restauración automática.");
        }

        return new RegressionGuardResult(
            watch.Id,
            watch.OptimizationId,
            status,
            consecutive,
            restoreAvailable,
            restoreRecommended,
            score,
            metrics,
            evidence,
            warnings);
    }

    private static IReadOnlyList<RegressionMetric> Compare(
        RegressionMeasurement baseline,
        RegressionMeasurement current)
    {
        return new[]
        {
            CreateMetric(
                "Average FPS",
                baseline.AverageFps,
                current.AverageFps,
                false,
                5),

            CreateMetric(
                "1% Low",
                baseline.OnePercentLowFps,
                current.OnePercentLowFps,
                true,
                LowFpsRegressionThreshold),

            CreateMetric(
                "0.1% Low",
                baseline.PointOnePercentLowFps,
                current.PointOnePercentLowFps,
                true,
                LowFpsRegressionThreshold),

            CreateMetric(
                "P99 Frametime",
                baseline.P99FrametimeMs,
                current.P99FrametimeMs,
                false,
                P99RegressionThreshold),

            CreateMetric(
                "Stutter Rate",
                baseline.StutterRatePercent,
                current.StutterRatePercent,
                false,
                StutterRegressionThreshold)
        };
    }

    private static RegressionMetric CreateMetric(
        string name,
        double baseline,
        double current,
        bool lowFpsMetric,
        double threshold)
    {
        var change = baseline > 0
            ? (current - baseline) / baseline * 100
            : 0;

        var isRegression = lowFpsMetric
            ? change <= -threshold
            : name == "Stutter Rate"
                ? current - baseline >= threshold
                : change >= threshold;

        var explanation = isRegression
            ? "Deterioro por encima del umbral configurado."
            : "Sin deterioro significativo según el umbral.";

        return new RegressionMetric(
            name,
            baseline,
            current,
            change,
            isRegression,
            explanation);
    }

    private static int CountConsecutiveRegressions(
        RegressionMeasurement baseline,
        IReadOnlyList<RegressionMeasurement> measurements)
    {
        var count = 0;

        for (var i = measurements.Count - 1; i >= 0; i--)
        {
            var metrics = Compare(baseline, measurements[i]);

            if (!metrics.Any(x => x.IsRegression))
                break;

            count++;
        }

        return count;
    }

    private static double CalculateScore(
        IReadOnlyList<RegressionMetric> metrics)
    {
        var weights = new Dictionary<string, double>
        {
            ["Average FPS"] = 0.05,
            ["1% Low"] = 0.30,
            ["0.1% Low"] = 0.30,
            ["P99 Frametime"] = 0.25,
            ["Stutter Rate"] = 0.10
        };

        var score = metrics.Sum(metric =>
        {
            if (!metric.IsRegression)
                return 0;

            var magnitude = Math.Abs(metric.ChangePercent);

            return weights.GetValueOrDefault(metric.Name) *
                   Math.Min(100, magnitude);
        });

        return Math.Round(Math.Min(100, score), 1);
    }

    private static bool IsComparable(
        RegressionMeasurement baseline,
        RegressionMeasurement current)
    {
        return string.Equals(
                   baseline.Scenario,
                   current.Scenario,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   baseline.Resolution,
                   current.Resolution,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   baseline.Source,
                   current.Source,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValid(RegressionMeasurement x)
    {
        var values = new[]
        {
            x.AverageFps,
            x.OnePercentLowFps,
            x.PointOnePercentLowFps,
            x.P99FrametimeMs,
            x.StutterRatePercent,
            x.DurationSeconds
        };

        return values.All(v => double.IsFinite(v) && v >= 0) &&
               x.AverageFps > 0 &&
               x.OnePercentLowFps > 0 &&
               x.PointOnePercentLowFps > 0 &&
               x.P99FrametimeMs > 0 &&
               x.StutterRatePercent <= 100 &&
               x.DurationSeconds > 0 &&
               x.FrameCount > 0 &&
               !string.IsNullOrWhiteSpace(x.Scenario) &&
               !string.IsNullOrWhiteSpace(x.Resolution) &&
               !string.IsNullOrWhiteSpace(x.Source);
    }

    private static RegressionGuardResult Inconclusive(
        Guid id,
        string optimizationId,
        IReadOnlyList<string> warnings)
    {
        return new RegressionGuardResult(
            id,
            optimizationId,
            RegressionStatus.Inconclusive,
            0,
            false,
            false,
            0,
            Array.Empty<RegressionMetric>(),
            Array.Empty<string>(),
            warnings);
    }
}
