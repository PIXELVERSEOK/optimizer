namespace EvoOptimizer.Core.Benchmark;

public enum BenchmarkBaselineStatus
{
    Active,
    Superseded
}

public enum BaselinePromotionStatus
{
    Approved,
    ApprovalRequired,
    Rejected,
    NoMatchingBaseline
}

public sealed record BenchmarkBaseline(
    Guid Id,
    string GameId,
    string HardwareId,
    string EnvironmentId,
    string Scenario,
    string Resolution,
    string CaptureSource,
    double AverageFps,
    double OnePercentLow,
    double PointOnePercentLow,
    double FrametimeP99Ms,
    double StutterRate,
    DateTimeOffset CapturedAt,
    BenchmarkBaselineStatus Status,
    bool EvidenceAccepted);

public sealed record BaselinePromotionPlan(
    BaselinePromotionStatus Status,
    Guid CandidateId,
    Guid? PreviousBaselineId,
    string Explanation,
    IReadOnlyList<string> Warnings);

public sealed record BaselinePromotionResult(
    bool Promoted,
    IReadOnlyList<BenchmarkBaseline> Baselines,
    string Message);

public sealed class BenchmarkBaselineManager
{
    public BenchmarkBaseline? GetActiveBaseline(
        IEnumerable<BenchmarkBaseline> baselines,
        string gameId,
        string hardwareId,
        string environmentId,
        string scenario,
        string resolution,
        string captureSource)
    {
        ArgumentNullException.ThrowIfNull(baselines);

        return baselines
            .Where(x =>
                x.Status == BenchmarkBaselineStatus.Active &&
                x.EvidenceAccepted &&
                Same(x.GameId, gameId) &&
                Same(x.HardwareId, hardwareId) &&
                Same(x.EnvironmentId, environmentId) &&
                Same(x.Scenario, scenario) &&
                Same(x.Resolution, resolution) &&
                Same(x.CaptureSource, captureSource))
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefault();
    }

    public BaselinePromotionPlan PreparePromotion(
        IEnumerable<BenchmarkBaseline> baselines,
        BenchmarkBaseline candidate,
        BenchmarkContextResolution contextResolution)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(contextResolution);

        var current = baselines.ToList();
        var warnings = new List<string>();

        if (!candidate.EvidenceAccepted)
        {
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.Rejected,
                candidate.Id,
                null,
                "El candidato no superó la validación de evidencia.",
                warnings);
        }

        if (!ValidMetrics(candidate))
        {
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.Rejected,
                candidate.Id,
                null,
                "El candidato contiene métricas inválidas o incompletas.",
                warnings);
        }

        if (contextResolution.Decision != BenchmarkContextDecision.Comparable &&
            contextResolution.Decision != BenchmarkContextDecision.NewHardwareBaselineRequired &&
            contextResolution.Decision != BenchmarkContextDecision.NewEnvironmentBaselineRequired)
        {
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.Rejected,
                candidate.Id,
                null,
                "El contexto no permite crear o promover esta línea base.",
                contextResolution.Differences);
        }

        if (string.IsNullOrWhiteSpace(candidate.GameId) ||
            string.IsNullOrWhiteSpace(candidate.HardwareId) ||
            string.IsNullOrWhiteSpace(candidate.EnvironmentId) ||
            string.IsNullOrWhiteSpace(candidate.Scenario) ||
            string.IsNullOrWhiteSpace(candidate.Resolution) ||
            string.IsNullOrWhiteSpace(candidate.CaptureSource))
        {
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.Rejected,
                candidate.Id,
                null,
                "Faltan identificadores necesarios para clasificar la línea base.",
                warnings);
        }

        var previous = current
            .Where(x =>
                x.Status == BenchmarkBaselineStatus.Active &&
                Same(x.GameId, candidate.GameId) &&
                Same(x.HardwareId, candidate.HardwareId) &&
                Same(x.EnvironmentId, candidate.EnvironmentId) &&
                Same(x.Scenario, candidate.Scenario) &&
                Same(x.Resolution, candidate.Resolution) &&
                Same(x.CaptureSource, candidate.CaptureSource))
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefault();

        if (previous is null)
        {
            warnings.Add("No existe una línea base activa para este contexto.");
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.ApprovalRequired,
                candidate.Id,
                null,
                "Se propone crear una nueva línea base.",
                warnings);
        }

        if (candidate.CapturedAt <= previous.CapturedAt)
        {
            return new BaselinePromotionPlan(
                BaselinePromotionStatus.Rejected,
                candidate.Id,
                previous.Id,
                "El candidato no es más reciente que la línea base actual.",
                warnings);
        }

        warnings.Add("La línea base anterior se conservará como histórica.");

        return new BaselinePromotionPlan(
            BaselinePromotionStatus.ApprovalRequired,
            candidate.Id,
            previous.Id,
            "Se propone reemplazar la referencia activa. Requiere aprobación explícita.",
            warnings);
    }

    public BaselinePromotionResult Promote(
        IEnumerable<BenchmarkBaseline> baselines,
        BenchmarkBaseline candidate,
        BaselinePromotionPlan plan,
        bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(plan);

        var current = baselines.ToList();

        if (!userApproved)
        {
            return new BaselinePromotionResult(
                false,
                current,
                "Promoción cancelada. No se modificó ninguna línea base.");
        }

        if (plan.Status != BaselinePromotionStatus.ApprovalRequired ||
            plan.CandidateId != candidate.Id ||
            !candidate.EvidenceAccepted ||
            !ValidMetrics(candidate))
        {
            return new BaselinePromotionResult(
                false,
                current,
                "El plan no es válido para promover este candidato.");
        }

        var matching = current
            .Where(x =>
                x.Status == BenchmarkBaselineStatus.Active &&
                Same(x.GameId, candidate.GameId) &&
                Same(x.HardwareId, candidate.HardwareId) &&
                Same(x.EnvironmentId, candidate.EnvironmentId) &&
                Same(x.Scenario, candidate.Scenario) &&
                Same(x.Resolution, candidate.Resolution) &&
                Same(x.CaptureSource, candidate.CaptureSource))
            .ToList();

        if (matching.Any(x => candidate.CapturedAt <= x.CapturedAt))
        {
            return new BaselinePromotionResult(
                false,
                current,
                "El candidato dejó de ser más reciente que la referencia activa.");
        }

        var updated = current
            .Select(x => matching.Any(m => m.Id == x.Id)
                ? x with { Status = BenchmarkBaselineStatus.Superseded }
                : x)
            .ToList();

        updated.Add(candidate with
        {
            Status = BenchmarkBaselineStatus.Active
        });

        return new BaselinePromotionResult(
            true,
            updated,
            "Nueva línea base activada. La anterior permanece en el historial.");
    }

    private static bool ValidMetrics(BenchmarkBaseline item)
    {
        return double.IsFinite(item.AverageFps) &&
               item.AverageFps > 0 &&
               double.IsFinite(item.OnePercentLow) &&
               item.OnePercentLow > 0 &&
               double.IsFinite(item.PointOnePercentLow) &&
               item.PointOnePercentLow > 0 &&
               double.IsFinite(item.FrametimeP99Ms) &&
               item.FrametimeP99Ms > 0 &&
               double.IsFinite(item.StutterRate) &&
               item.StutterRate >= 0;
    }

    private static bool Same(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(
            first.Trim(),
            second.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
