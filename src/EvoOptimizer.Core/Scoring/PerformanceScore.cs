using EvoOptimizer.Core.Decisions;

namespace EvoOptimizer.Core.Scoring;

public enum ScoreDimension { Stability, Performance, Efficiency, Health }

/// <param name="Value">0..100, o null si la dimensión no se pudo medir.</param>
/// <param name="Confidence">0..1</param>
/// <param name="Basis">Qué datos sustentan el valor, o qué falta para medirlo.</param>
public sealed record DimensionScore(ScoreDimension Dimension, double? Value, double Confidence, string Basis);

/// <summary>
/// Multidimensional a propósito: no existe un número global. Solo es comparable con otro score
/// que tenga el mismo Goal y WorkloadContext.
/// </summary>
public sealed record PerformanceScore(
    OptimizationGoal Goal,
    string WorkloadContext,
    IReadOnlyList<DimensionScore> Dimensions);
