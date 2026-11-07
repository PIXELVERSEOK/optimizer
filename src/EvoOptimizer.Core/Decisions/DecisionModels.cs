namespace EvoOptimizer.Core.Decisions;

public enum OptimizationGoal { Everyday, Gaming, MaximumPerformance }
public enum RiskLevel { None, Low, Medium, High }
public enum Severity { Info, Warning, Critical }

/// <summary>Observación concreta que respalda un hallazgo.</summary>
public sealed record Evidence(string Metric, string Observation, string Source);

public sealed record Finding(
    string Id,
    string Title,
    Severity Severity,
    double Confidence,
    string TechnicalExplanation,
    IReadOnlyList<Evidence> Evidence,
    string? Limitations);

public sealed record Recommendation(
    string Id,
    string Title,
    string Rationale,
    double Confidence,
    RiskLevel Risk,
    string ExpectedBenefit,
    string RevertStrategy,
    string? CausalityCaveat);
