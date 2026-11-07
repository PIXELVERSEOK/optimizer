using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Telemetry;

namespace EvoOptimizer.Core.Decisions;

public sealed record DecisionContext(
    HardwareProfile Hardware,
    IReadOnlyList<TelemetrySnapshot> RecentTelemetry,
    OptimizationGoal Goal);

public sealed record DecisionReport(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Recommendation> Recommendations);

/// <summary>Punto de integración del Adaptive Decision Engine. Solo analiza; nunca aplica cambios.</summary>
public interface IDecisionEngine
{
    DecisionReport Evaluate(DecisionContext context);
}
