namespace EvoOptimizer.Core.Learning;

public enum LearningOutcome
{
    StrongImprovement,
    Improvement,
    Mixed,
    Regression,
    StrongRegression,
    InsufficientEvidence
}

public sealed record OptimizationExperiment(
    Guid Id,
    string GameId,
    string HardwareFingerprint,
    string OptimizationId,
    string OptimizationName,
    DateTimeOffset CapturedAt,
    double BaselineAverageFps,
    double CandidateAverageFps,
    double BaselineOnePercentLow,
    double CandidateOnePercentLow,
    double BaselinePointOnePercentLow,
    double CandidatePointOnePercentLow,
    double BaselineP99FrametimeMs,
    double CandidateP99FrametimeMs,
    double BaselineStutterRate,
    double CandidateStutterRate,
    double BaselineDurationSeconds,
    double CandidateDurationSeconds,
    int BaselineFrameCount,
    int CandidateFrameCount,
    bool SameScenario,
    bool SameResolution,
    bool SameSettings);

public sealed record LearningMetric(
    string Name,
    double Baseline,
    double Candidate,
    double ChangePercent,
    bool Improved,
    string Explanation);

public sealed record OptimizationLearningResult(
    Guid ExperimentId,
    LearningOutcome Outcome,
    double ConfidencePercent,
    double PerformanceScore,
    bool IsComparable,
    IReadOnlyList<LearningMetric> Metrics,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings);

public sealed record OptimizationKnowledge(
    string GameId,
    string HardwareFingerprint,
    string OptimizationId,
    int Experiments,
    int Improvements,
    int Regressions,
    int MixedResults,
    int InconclusiveResults,
    double AveragePerformanceScore,
    double AverageOnePercentLowChange,
    LearningOutcome RecommendedOutcome,
    string Recommendation);

public sealed class OptimizationLearningEngine
{
    public OptimizationLearningResult Evaluate(
        OptimizationExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        var warnings = new List<string>();
        var evidence = new List<string>();

        if (string.IsNullOrWhiteSpace(experiment.GameId) ||
            string.IsNullOrWhiteSpace(experiment.HardwareFingerprint) ||
            string.IsNullOrWhiteSpace(experiment.OptimizationId))
        {
            warnings.Add("Faltan identificadores del experimento.");
        }

        if (!IsValid(experiment))
            warnings.Add("Hay métricas inválidas o fuera de rango.");

        if (!experiment.SameScenario ||
            !experiment.SameResolution ||
            !experiment.SameSettings)
        {
            warnings.Add(
                "Las condiciones de comparación no son idénticas.");
        }

        if (experiment.BaselineDurationSeconds < 30 ||
            experiment.CandidateDurationSeconds < 30)
        {
            warnings.Add(
                "Cada captura debe durar al menos 30 segundos.");
        }

        if (experiment.BaselineFrameCount < 1000 ||
            experiment.CandidateFrameCount < 1000)
        {
            warnings.Add(
                "Se requieren al menos 1000 frames por captura.");
        }

        var durationDifference = RelativeDifference(
            experiment.BaselineDurationSeconds,
            experiment.CandidateDurationSeconds);

        if (durationDifference > 0.20)
        {
            warnings.Add(
                "La duración de las capturas difiere más del 20%.");
        }

        if (warnings.Count > 0)
        {
            return Inconclusive(experiment.Id, warnings);
        }

        var metrics = new[]
        {
            Compare("Average FPS",
                experiment.BaselineAverageFps,
                experiment.CandidateAverageFps,
                true),

            Compare("1% Low",
                experiment.BaselineOnePercentLow,
                experiment.CandidateOnePercentLow,
                true),

            Compare("0.1% Low",
                experiment.BaselinePointOnePercentLow,
                experiment.CandidatePointOnePercentLow,
                true),

            Compare("P99 Frametime",
                experiment.BaselineP99FrametimeMs,
                experiment.CandidateP99FrametimeMs,
                false),

            Compare("Stutter Rate",
                experiment.BaselineStutterRate,
                experiment.CandidateStutterRate,
                false)
        };

        var weightedScore =
            Change(experiment.BaselineAverageFps,
                   experiment.CandidateAverageFps) * 0.10 +
            Change(experiment.BaselineOnePercentLow,
                   experiment.CandidateOnePercentLow) * 0.35 +
            Change(experiment.BaselinePointOnePercentLow,
                   experiment.CandidatePointOnePercentLow) * 0.25 +
            ImprovementChange(experiment.BaselineP99FrametimeMs,
                              experiment.CandidateP99FrametimeMs) * 0.20 +
            ImprovementChange(experiment.BaselineStutterRate,
                              experiment.CandidateStutterRate) * 0.10;

        var lowsImproved =
            metrics[1].Improved && metrics[2].Improved;

        var lowsRegressed =
            !metrics[1].Improved && !metrics[2].Improved &&
            metrics[1].ChangePercent < -2 &&
            metrics[2].ChangePercent < -2;

        var outcome = Classify(weightedScore, lowsImproved, lowsRegressed);

        evidence.Add(
            $"Puntuación ponderada: {weightedScore:+0.00;-0.00;0.00}%.");

        evidence.Add(
            $"1% Low: {metrics[1].ChangePercent:+0.00;-0.00;0.00}%.");

        evidence.Add(
            $"0.1% Low: {metrics[2].ChangePercent:+0.00;-0.00;0.00}%.");

        evidence.Add(
            "La comparación pondera más los mínimos de FPS que el promedio.");

        var confidence = CalculateConfidence(
            experiment.BaselineDurationSeconds,
            experiment.CandidateDurationSeconds,
            experiment.BaselineFrameCount,
            experiment.CandidateFrameCount,
            durationDifference);

        return new OptimizationLearningResult(
            experiment.Id,
            outcome,
            confidence,
            weightedScore,
            true,
            metrics,
            evidence,
            warnings);
    }

    public OptimizationKnowledge Learn(
        string gameId,
        string hardwareFingerprint,
        string optimizationId,
        IEnumerable<OptimizationLearningResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var relevant = results
            .Where(x => x.IsComparable)
            .ToArray();

        var improvements = relevant.Count(x =>
            x.Outcome is LearningOutcome.Improvement
                or LearningOutcome.StrongImprovement);

        var regressions = relevant.Count(x =>
            x.Outcome is LearningOutcome.Regression
                or LearningOutcome.StrongRegression);

        var mixed = relevant.Count(x =>
            x.Outcome == LearningOutcome.Mixed);

        var inconclusive = results.Count() - relevant.Length;

        var averageScore = relevant.Length == 0
            ? 0
            : relevant.Average(x => x.PerformanceScore);

        var averageLowChange = relevant.Length == 0
            ? 0
            : relevant.Average(x =>
                x.Metrics.FirstOrDefault(m => m.Name == "1% Low")
                    ?.ChangePercent ?? 0);

        var recommended = relevant.Length < 3
            ? LearningOutcome.InsufficientEvidence
            : regressions >= improvements + 2
                ? LearningOutcome.StrongRegression
                : improvements >= regressions + 2
                    ? LearningOutcome.StrongImprovement
                    : mixed > improvements + regressions
                        ? LearningOutcome.Mixed
                        : LearningOutcome.InsufficientEvidence;

        var recommendation = recommended switch
        {
            LearningOutcome.StrongImprovement =>
                "Resultado favorable repetido. Puede priorizarse para nuevas pruebas controladas.",
            LearningOutcome.StrongRegression =>
                "Resultado desfavorable repetido. No recomendar esta optimización sin nueva evidencia.",
            LearningOutcome.Mixed =>
                "Resultados inconsistentes. Requiere pruebas adicionales.",
            _ =>
                "Evidencia insuficiente. Mantener la configuración actual."
        };

        return new OptimizationKnowledge(
            gameId,
            hardwareFingerprint,
            optimizationId,
            relevant.Length,
            improvements,
            regressions,
            mixed,
            inconclusive,
            averageScore,
            averageLowChange,
            recommended,
            recommendation);
    }

    private static bool IsValid(OptimizationExperiment x)
    {
        var values = new[]
        {
            x.BaselineAverageFps,
            x.CandidateAverageFps,
            x.BaselineOnePercentLow,
            x.CandidateOnePercentLow,
            x.BaselinePointOnePercentLow,
            x.CandidatePointOnePercentLow,
            x.BaselineP99FrametimeMs,
            x.CandidateP99FrametimeMs,
            x.BaselineStutterRate,
            x.CandidateStutterRate,
            x.BaselineDurationSeconds,
            x.CandidateDurationSeconds
        };

        return values.All(v => double.IsFinite(v) && v >= 0) &&
               x.BaselineAverageFps > 0 &&
               x.CandidateAverageFps > 0 &&
               x.BaselineOnePercentLow > 0 &&
               x.CandidateOnePercentLow > 0 &&
               x.BaselinePointOnePercentLow > 0 &&
               x.CandidatePointOnePercentLow > 0 &&
               x.BaselineP99FrametimeMs > 0 &&
               x.CandidateP99FrametimeMs > 0 &&
               x.BaselineStutterRate <= 100 &&
               x.CandidateStutterRate <= 100;
    }

    private static LearningMetric Compare(
        string name,
        double baseline,
        double candidate,
        bool higherIsBetter)
    {
        var change = Change(baseline, candidate);
        var improved = higherIsBetter ? change > 1 : change < -1;

        var explanation = Math.Abs(change) <= 1
            ? "Sin cambio significativo según el umbral del 1%."
            : improved
                ? "Mejora respecto a la referencia."
                : "Empeora respecto a la referencia.";

        return new LearningMetric(
            name,
            baseline,
            candidate,
            change,
            improved,
            explanation);
    }

    private static double Change(double baseline, double candidate)
    {
        if (baseline <= 0)
            return 0;

        return (candidate - baseline) / baseline * 100;
    }

    private static double ImprovementChange(
        double baseline,
        double candidate)
    {
        return -Change(baseline, candidate);
    }

    private static double RelativeDifference(double a, double b)
    {
        var maximum = Math.Max(a, b);

        return maximum <= 0
            ? 0
            : Math.Abs(a - b) / maximum;
    }

    private static LearningOutcome Classify(
        double score,
        bool lowsImproved,
        bool lowsRegressed)
    {
        if (score >= 5 && lowsImproved)
            return LearningOutcome.StrongImprovement;

        if (score >= 1)
            return LearningOutcome.Improvement;

        if (score <= -5 && lowsRegressed)
            return LearningOutcome.StrongRegression;

        if (score <= -1)
            return LearningOutcome.Regression;

        return LearningOutcome.Mixed;
    }

    private static double CalculateConfidence(
        double baselineDuration,
        double candidateDuration,
        int baselineFrames,
        int candidateFrames,
        double durationDifference)
    {
        var durationScore = Math.Min(
            1,
            Math.Min(baselineDuration, candidateDuration) / 120);

        var frameScore = Math.Min(
            1,
            Math.Min(baselineFrames, candidateFrames) / 5000d);

        var consistencyScore = 1 - Math.Min(1, durationDifference);

        return Math.Round(
            (durationScore * 0.35 +
             frameScore * 0.45 +
             consistencyScore * 0.20) * 100,
            1);
    }

    private static OptimizationLearningResult Inconclusive(
        Guid id,
        IReadOnlyList<string> warnings)
    {
        return new OptimizationLearningResult(
            id,
            LearningOutcome.InsufficientEvidence,
            0,
            0,
            false,
            Array.Empty<LearningMetric>(),
            Array.Empty<string>(),
            warnings);
    }
}
