using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Core.Recovery;

namespace EvoOptimizer.Infrastructure.Optimization;

public sealed record RecoveryDecision(
    bool CanRestore,
    bool RequiresUserApproval,
    string Message,
    Guid? RestoreEntryId);

public sealed record RecoveryExecutionResult(
    bool Succeeded,
    Guid WatchId,
    Guid? RestoreEntryId,
    string Message,
    DateTimeOffset ExecutedAt);

public sealed class RegressionRecoveryService
{
    private readonly RegressionGuard _guard;
    private readonly RestoreCenter _restoreCenter;

    public RegressionRecoveryService(
        RegressionGuard guard,
        RestoreCenter restoreCenter)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _restoreCenter = restoreCenter ??
            throw new ArgumentNullException(nameof(restoreCenter));
    }

    public RegressionGuardResult Evaluate(RegressionWatch watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return _guard.Evaluate(watch);
    }

    public RecoveryDecision PrepareRecovery(
        RegressionWatch watch,
        RegressionGuardResult result)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(result);

        if (watch.Id != result.WatchId ||
            !string.Equals(
                watch.OptimizationId,
                result.OptimizationId,
                StringComparison.Ordinal))
        {
            return new RecoveryDecision(
                false,
                false,
                "El resultado no corresponde a esta optimización.",
                null);
        }

        if (result.Status != RegressionStatus.Confirmed)
        {
            return new RecoveryDecision(
                false,
                false,
                "La regresión todavía no está confirmada.",
                null);
        }

        if (!watch.RestoreEntryId.HasValue)
        {
            return new RecoveryDecision(
                false,
                false,
                "No existe una entrada de recuperación asociada.",
                null);
        }

        return new RecoveryDecision(
            true,
            true,
            "Regresión confirmada. Se puede restaurar el valor original con aprobación del usuario.",
            watch.RestoreEntryId);
    }

    // Llamar únicamente desde una acción explícita del usuario en la interfaz.
    public async Task<RecoveryExecutionResult> RestoreAfterApprovalAsync(
        RegressionWatch watch,
        RegressionGuardResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(result);

        var decision = PrepareRecovery(watch, result);

        if (!decision.CanRestore ||
            !decision.RequiresUserApproval ||
            !decision.RestoreEntryId.HasValue)
        {
            return new RecoveryExecutionResult(
                false,
                watch.Id,
                decision.RestoreEntryId,
                decision.Message,
                DateTimeOffset.UtcNow);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _restoreCenter.RestoreAsync(
                decision.RestoreEntryId.Value);

            return new RecoveryExecutionResult(
                true,
                watch.Id,
                decision.RestoreEntryId,
                "Restauración ejecutada. El Restore Center debe verificar el valor recuperado.",
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RecoveryExecutionResult(
                false,
                watch.Id,
                decision.RestoreEntryId,
                $"No se pudo completar la restauración: {ex.Message}",
                DateTimeOffset.UtcNow);
        }
    }
}
