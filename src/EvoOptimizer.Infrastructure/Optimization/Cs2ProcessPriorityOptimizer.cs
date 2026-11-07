using System.Collections.Concurrent;
using System.Diagnostics;

namespace EvoOptimizer.Infrastructure.Optimization;

public enum PriorityOptimizationStatus
{
    Applied,
    AlreadyApplied,
    RolledBack,
    ProcessNotFound,
    AmbiguousProcess,
    IdentityMismatch,
    ExternalChangeDetected,
    PermissionDenied,
    Failed
}

public sealed record PriorityOptimizationToken(
    Guid Id,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    ProcessPriorityClass OriginalPriority,
    ProcessPriorityClass AppliedPriority,
    DateTimeOffset AppliedAt);

public sealed record PriorityOptimizationResult(
    PriorityOptimizationStatus Status,
    string Message,
    PriorityOptimizationToken? Token = null);

/// <summary>
/// Cambio reversible y limitado a la prioridad del proceso CS2.
/// Nunca utiliza High ni Realtime.
/// </summary>
public sealed class Cs2ProcessPriorityOptimizer
{
    private const string TargetProcessName = "cs2";
    private static readonly ProcessPriorityClass TargetPriority =
        ProcessPriorityClass.AboveNormal;

    private readonly ConcurrentDictionary<Guid, PriorityOptimizationToken>
        _activeChanges = new();

    public PriorityOptimizationResult Apply()
    {
        Process[] processes = Process.GetProcessesByName(TargetProcessName);

        try
        {
            if (processes.Length == 0)
            {
                return new(
                    PriorityOptimizationStatus.ProcessNotFound,
                    "CS2 no está ejecutándose.");
            }

            if (processes.Length != 1)
            {
                return new(
                    PriorityOptimizationStatus.AmbiguousProcess,
                    "Se detectaron varias instancias de CS2. No se modificó ninguna.");
            }

            using Process process = processes[0];

            if (process.HasExited)
            {
                return new(
                    PriorityOptimizationStatus.ProcessNotFound,
                    "CS2 terminó antes de aplicar la optimización.");
            }

            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            ProcessPriorityClass original = process.PriorityClass;

            if (original == TargetPriority)
            {
                return new(
                    PriorityOptimizationStatus.AlreadyApplied,
                    "CS2 ya utiliza AboveNormal. No se realizó otro cambio.");
            }

            process.PriorityClass = TargetPriority;

            var token = new PriorityOptimizationToken(
                Guid.NewGuid(),
                process.Id,
                startTicks,
                original,
                TargetPriority,
                DateTimeOffset.UtcNow);

            _activeChanges[token.Id] = token;

            return new(
                PriorityOptimizationStatus.Applied,
                $"Prioridad cambiada de {original} a {TargetPriority}.",
                token);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(
                PriorityOptimizationStatus.PermissionDenied,
                $"Windows rechazó el cambio de prioridad: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(
                PriorityOptimizationStatus.Failed,
                $"No se pudo aplicar la optimización: {ex.Message}");
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    public PriorityOptimizationResult Rollback(Guid tokenId)
    {
        if (!_activeChanges.TryGetValue(tokenId, out var token))
        {
            return new(
                PriorityOptimizationStatus.Failed,
                "No existe un cambio activo con ese identificador.");
        }

        try
        {
            using Process process = Process.GetProcessById(token.ProcessId);

            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks !=
                token.ProcessStartTimeUtcTicks)
            {
                return new(
                    PriorityOptimizationStatus.IdentityMismatch,
                    "La identidad de CS2 cambió. Se rechazó el rollback.");
            }

            if (process.PriorityClass != token.AppliedPriority)
            {
                return new(
                    PriorityOptimizationStatus.ExternalChangeDetected,
                    "La prioridad fue modificada externamente. No se sobrescribió.");
            }

            process.PriorityClass = token.OriginalPriority;

            _activeChanges.TryRemove(tokenId, out _);

            return new(
                PriorityOptimizationStatus.RolledBack,
                $"Prioridad restaurada a {token.OriginalPriority}.",
                token);
        }
        catch (ArgumentException)
        {
            return new(
                PriorityOptimizationStatus.IdentityMismatch,
                "El proceso CS2 ya no existe.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(
                PriorityOptimizationStatus.PermissionDenied,
                $"Windows rechazó la restauración: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(
                PriorityOptimizationStatus.Failed,
                $"Falló el rollback: {ex.Message}");
        }
    }

    public IReadOnlyCollection<PriorityOptimizationToken> GetActiveChanges()
    {
        return _activeChanges.Values.ToArray();
    }
}