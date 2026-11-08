using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Native;

namespace EvoOptimizer.Infrastructure.Optimization;

public enum AffinityOptimizationStatus
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

public sealed record AffinityOptimizationToken(
    Guid Id,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    ulong OriginalAffinityMask,
    ulong AppliedAffinityMask,
    GamingAffinityMode AppliedMode,
    DateTimeOffset AppliedAt);

public sealed record AffinityOptimizationResult(
    AffinityOptimizationStatus Status,
    string Message,
    AffinityOptimizationToken? Token = null);

/// <summary>
/// Ajuste adaptativo y reversible de afinidad para el proceso CS2 en arquitecturas híbridas.
/// Permite priorizar P-Cores para reducir variabilidad de frametime (1% low y 0.1% low)
/// o excluir el Core 0 para aislarlo de interrupciones DPC/ISR del sistema operativo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Cs2AffinityOptimizer
{
    private const string TargetProcessName = "cs2";

    private readonly ConcurrentDictionary<Guid, AffinityOptimizationToken>
        _activeChanges = new();

    /// <summary>
    /// Aplica la afinidad al proceso indicado. Sin nombre usa CS2 (comportamiento
    /// histórico). El motor de tokens/rollback es el mismo para cualquier juego.
    /// </summary>
    public AffinityOptimizationResult Apply(
        CpuTopologyInfo topology,
        GamingAffinityMode mode,
        string? processName = null)
    {
        string target = string.IsNullOrWhiteSpace(processName)
            ? TargetProcessName
            : processName.Trim();
        string display = target.Equals(TargetProcessName, StringComparison.OrdinalIgnoreCase)
            ? "CS2"
            : target;

        if (topology is null || !topology.IsTopologyReliable)
        {
            return new(
                AffinityOptimizationStatus.Failed,
                "La topología de CPU no es confiable o no contiene núcleos válidos. Se canceló la optimización de afinidad para proteger la estabilidad.");
        }

        if (mode == GamingAffinityMode.PerformanceCoresOnly && topology.PerformanceCores == 0)
        {
            return new(
                AffinityOptimizationStatus.Failed,
                "La topología no reporta núcleos de rendimiento (P-Cores). Se canceló la optimización.");
        }

        Process[] processes = Process.GetProcessesByName(target);

        try
        {
            if (processes.Length == 0)
            {
                return new(
                    AffinityOptimizationStatus.ProcessNotFound,
                    $"{display} no está ejecutándose.");
            }

            if (processes.Length != 1)
            {
                return new(
                    AffinityOptimizationStatus.AmbiguousProcess,
                    $"Se detectaron varias instancias de {display}. No se modificó ninguna.");
            }

            using Process process = processes[0];

            if (process.HasExited)
            {
                return new(
                    AffinityOptimizationStatus.ProcessNotFound,
                    $"{display} terminó antes de aplicar la afinidad.");
            }

            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            ulong targetMask = topology.GetAffinityMask(mode);

            if (targetMask == 0 || (targetMask & topology.AllCoresMask) != targetMask)
            {
                return new(
                    AffinityOptimizationStatus.Failed,
                    $"La máscara de afinidad calculada (0x{targetMask:X}) es inválida o excede los núcleos del sistema.");
            }

            IntPtr handle = process.Handle;

            if (!NativeMethods.GetProcessAffinityMask(handle, out var currentAffinity, out _))
            {
                return new(
                    AffinityOptimizationStatus.PermissionDenied,
                    "Windows no permitió consultar la afinidad del proceso objetivo.");
            }

            ulong originalMask = currentAffinity.ToUInt64();

            if (originalMask == targetMask)
            {
                return new(
                    AffinityOptimizationStatus.AlreadyApplied,
                    $"{display} ya utiliza la máscara 0x{targetMask:X}. No se realizó ningún cambio.");
            }

            if (!NativeMethods.SetProcessAffinityMask(handle, new UIntPtr(targetMask)))
            {
                return new(
                    AffinityOptimizationStatus.PermissionDenied,
                    "Windows rechazó aplicar la máscara de afinidad.");
            }

            var token = new AffinityOptimizationToken(
                Guid.NewGuid(),
                process.Id,
                startTicks,
                originalMask,
                targetMask,
                mode,
                DateTimeOffset.UtcNow);

            _activeChanges[token.Id] = token;

            string modeDesc = mode switch
            {
                GamingAffinityMode.PerformanceCoresOnly => $"{topology.PerformanceCores} P-Cores ({topology.PerformanceLogicalProcessors} hilos) exclusivos",
                GamingAffinityMode.PerformanceAndLightEfficiency => $"{topology.PerformanceCores} P-Cores + E-Cores balanceados",
                GamingAffinityMode.ExcludeCore0 => "Todos los núcleos excepto Core 0 (aislamiento DPC/ISR)",
                _ => "Todos los núcleos"
            };

            return new(
                AffinityOptimizationStatus.Applied,
                $"Afinidad cambiada a {modeDesc} (Máscara: 0x{targetMask:X}, Anterior: 0x{originalMask:X}).",
                token);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(
                AffinityOptimizationStatus.PermissionDenied,
                $"Error Win32 al modificar afinidad: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(
                AffinityOptimizationStatus.Failed,
                $"No se pudo aplicar la afinidad: {ex.Message}");
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    public AffinityOptimizationResult Rollback(Guid tokenId)
    {
        if (!_activeChanges.TryGetValue(tokenId, out var token))
        {
            return new(
                AffinityOptimizationStatus.Failed,
                "No existe un cambio activo de afinidad con ese identificador.");
        }

        try
        {
            using Process process = Process.GetProcessById(token.ProcessId);

            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks !=
                token.ProcessStartTimeUtcTicks)
            {
                return new(
                    AffinityOptimizationStatus.IdentityMismatch,
                    "La identidad del proceso cambió. Se rechazó el rollback de afinidad.");
            }

            IntPtr handle = process.Handle;

            if (!NativeMethods.GetProcessAffinityMask(handle, out var currentAffinity, out _))
            {
                return new(
                    AffinityOptimizationStatus.PermissionDenied,
                    "Windows no permitió verificar la afinidad actual. Se rechazó el rollback para no sobrescribir un cambio externo.");
            }

            if (currentAffinity.ToUInt64() != token.AppliedAffinityMask)
            {
                return new(
                    AffinityOptimizationStatus.ExternalChangeDetected,
                    "La afinidad fue modificada externamente. No se sobrescribió.");
            }

            if (!NativeMethods.SetProcessAffinityMask(handle, new UIntPtr(token.OriginalAffinityMask)))
            {
                return new(
                    AffinityOptimizationStatus.PermissionDenied,
                    "Windows rechazó restaurar la afinidad original.");
            }

            _activeChanges.TryRemove(tokenId, out _);

            return new(
                AffinityOptimizationStatus.RolledBack,
                $"Afinidad restaurada a 0x{token.OriginalAffinityMask:X}.",
                token);
        }
        catch (ArgumentException)
        {
            return new(
                AffinityOptimizationStatus.IdentityMismatch,
                "El proceso optimizado ya no existe.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(
                AffinityOptimizationStatus.PermissionDenied,
                $"Windows rechazó la restauración: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(
                AffinityOptimizationStatus.Failed,
                $"Falló el rollback de afinidad: {ex.Message}");
        }
    }

    public IReadOnlyCollection<AffinityOptimizationToken> GetActiveChanges()
    {
        return _activeChanges.Values.ToArray();
    }
}
