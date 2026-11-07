using EvoOptimizer.Core.Optimization;

namespace EvoOptimizer.Infrastructure.Optimization;

public sealed record EvoDecisionPipelineRequest(
    OptimizationDecisionContext DecisionContext,
    IReadOnlyList<RegressionWatch> RegressionWatches,
    bool BenchmarkAvailable);

public sealed record EvoDecisionPipelineReport(
    Guid Id,
    DateTimeOffset GeneratedAt,
    OptimizationOrchestrationReport Orchestration,
    OptimizationDecisionReport Decisions,
    IReadOnlyList<RecoveryDecision> RecoveryActions,
    int TotalSignals,
    int TotalDecisions,
    int ActionableDecisions,
    int InvestigationsRequired,
    int ConfirmedRegressions,
    int SuspectedRegressions,
    string OverallStatus,
    IReadOnlyList<string> Warnings);

public sealed class EvoDecisionPipeline
{
    private readonly OptimizationOrchestrator _orchestrator;
    private readonly OptimizationDecisionEngine _decisionEngine;
    private readonly RegressionRecoveryService _recoveryService;

    public EvoDecisionPipeline(
        OptimizationOrchestrator orchestrator,
        OptimizationDecisionEngine decisionEngine,
        RegressionRecoveryService recoveryService)
    {
        _orchestrator = orchestrator ??
            throw new ArgumentNullException(nameof(orchestrator));

        _decisionEngine = decisionEngine ??
            throw new ArgumentNullException(nameof(decisionEngine));

        _recoveryService = recoveryService ??
            throw new ArgumentNullException(nameof(recoveryService));
    }

    public EvoDecisionPipelineReport Execute(
        EvoDecisionPipelineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DecisionContext);
        ArgumentNullException.ThrowIfNull(request.RegressionWatches);

        var context = request.DecisionContext;

        if (context.BenchmarkAvailable != request.BenchmarkAvailable)
        {
            throw new ArgumentException(
                "El estado del benchmark debe coincidir en todo el pipeline.",
                nameof(request));
        }

        var orchestration = _orchestrator.Analyze(
            context.Signals,
            request.RegressionWatches,
            request.BenchmarkAvailable);

        var decisions = _decisionEngine.Evaluate(context);

        var recoveryActions = _orchestrator.PrepareRecoveryActions(
            request.RegressionWatches,
            orchestration,
            _recoveryService);

        var warnings = new List<string>();

        warnings.AddRange(orchestration.SystemAlerts);
        warnings.AddRange(decisions.GlobalWarnings);

        if (decisions.Decisions.Any(x =>
            x.Disposition == DecisionDisposition.SuppressedByHistory))
        {
            warnings.Add(
                "Una o más optimizaciones fueron suprimidas por antecedentes negativos.");
        }

        if (recoveryActions.Any(x => x.CanRestore))
        {
            warnings.Add(
                "Hay restauraciones disponibles que requieren aprobación explícita.");
        }

        if (request.RegressionWatches.Count == 0)
        {
            warnings.Add(
                "No hay optimizaciones bajo vigilancia de regresiones.");
        }

        var actionable = decisions.Decisions.Count(x =>
            x.CanApply && x.RequiresUserApproval);

        var investigations = decisions.Decisions.Count(x =>
            x.Disposition == DecisionDisposition.Investigate);

        var status = orchestration.ConfirmedRegressions > 0
            ? "RECOVERY_REVIEW_REQUIRED"
            : decisions.Decisions.Any(x =>
                x.Disposition == DecisionDisposition.BlockedByEvidence)
                ? "EVIDENCE_REQUIRED"
                : actionable > 0
                    ? "OPTIMIZATIONS_READY_FOR_APPROVAL"
                    : investigations > 0
                        ? "INVESTIGATION_REQUIRED"
                        : "MONITORING";

        return new EvoDecisionPipelineReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            orchestration,
            decisions,
            recoveryActions,
            context.Signals.Count,
            decisions.DecisionsGenerated,
            actionable,
            investigations,
            orchestration.ConfirmedRegressions,
            orchestration.SuspectedRegressions,
            status,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    // Ejecutar solamente desde una acción explícita de aprobación en la UI.
    public async Task<RecoveryExecutionResult> ApproveRecoveryAsync(
        RegressionWatch watch,
        RegressionGuardResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(result);

        return await _recoveryService.RestoreAfterApprovalAsync(
            watch,
            result,
            cancellationToken);
    }
}
