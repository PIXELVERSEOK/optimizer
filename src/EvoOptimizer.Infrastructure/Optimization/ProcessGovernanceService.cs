using System.Diagnostics;
using System.Runtime.Versioning;
using EvoOptimizer.Infrastructure.History;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.Infrastructure.Optimization;

public enum ProcessImportance
{
    Critical,
    High,
    Normal,
    Background,
    Unknown
}

public sealed record GovernedProcess(
    int ProcessId,
    string Name,
    double CpuPercent,
    double MemoryMb,
    ProcessImportance Importance,
    bool IsThrottled,
    string PriorityClass);

public sealed record ProcessGovernanceResult(
    bool Success,
    string Message,
    GovernedProcess? Process = null);

/// <summary>
/// Gobierno de procesos de sólo prioridad: nunca termina procesos, nunca usa
/// High/Realtime y protege procesos críticos. Cada cambio conserva la clase
/// original para poder revertirse.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessGovernanceService
{
    private sealed record OriginalProcessState(
        ProcessPriorityClass Priority,
        long StartTimeUtcTicks);

    private static readonly HashSet<string> CriticalNames = new(
        new[]
        {
            "system", "idle", "registry", "memory compression", "csrss",
            "wininit", "winlogon", "services", "lsass", "smss", "svchost",
            "dwm", "iskrnl", "fontdrvhost", "audiodg", "sihost", "taskhostw",
            "ctfmon", "explorer", "wudfhost", "securityhealthservice"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, OriginalProcessState> _originalPriorities = new();
    private readonly EvoHistoryStore? _history;
    private readonly object _gate = new();

    public ProcessGovernanceService(EvoHistoryStore? history = null)
    {
        _history = history;
    }

    public IReadOnlyList<GovernedProcess> Classify(
        IEnumerable<ProcessSummary> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var result = new List<GovernedProcess>();

        foreach (var summary in processes)
        {
            ProcessImportance importance = ClassifyName(summary.Name);
            bool throttled;
            string priority = "UNKNOWN";

            lock (_gate)
                throttled = _originalPriorities.ContainsKey(summary.ProcessId);

            try
            {
                using var process = Process.GetProcessById(summary.ProcessId);
                priority = process.PriorityClass.ToString();
                throttled = throttled ||
                            process.PriorityClass == ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Proceso protegido o finalizado: se informa sin inventar datos.
            }

            result.Add(new GovernedProcess(
                summary.ProcessId,
                summary.Name,
                summary.CpuPercent,
                summary.MemoryMb,
                importance,
                throttled,
                priority));
        }

        return result;
    }

    public async Task<ProcessGovernanceResult> ThrottleAsync(
        int processId,
        bool userAuthorized,
        bool allowUnclassified = false,
        CancellationToken cancellationToken = default)
    {
        if (!userAuthorized)
            return new(false, "Bajar la prioridad requiere autorización explícita.");

        string? name;
        long startTimeUtcTicks;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
            startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;

            ProcessImportance importance = ClassifyName(process.ProcessName);
            if (importance == ProcessImportance.Critical)
            {
                return new(
                    false,
                    $"'{process.ProcessName}' es un proceso crítico protegido. No se modifica.");
            }

            if (importance == ProcessImportance.Unknown)
            {
                // Game Booster: la usuaria seleccionó el proceso explícitamente.
                // Sólo se permite si no está protegido por la política de Boost.
                if (!allowUnclassified ||
                    EvoOptimizer.Core.Gaming.BoostProcessPolicy.IsProtected(process.ProcessName))
                {
                    return new(
                        false,
                        $"'{process.ProcessName}' no está clasificado. EVO no cambia la prioridad de procesos desconocidos.");
                }
            }

            if (process.PriorityClass is ProcessPriorityClass.High or
                ProcessPriorityClass.RealTime)
            {
                return new(
                    false,
                    "La prioridad actual es High/Realtime. EVO no la modifica.");
            }

            if (process.PriorityClass == ProcessPriorityClass.BelowNormal)
                return new(false, $"'{process.ProcessName}' ya está en BelowNormal.");

            if (_history is null)
                return new(false, "No se configuró el historial EVO; no se modificó la prioridad.");

            lock (_gate)
            {
                if (!_originalPriorities.ContainsKey(processId))
                    _originalPriorities[processId] = new OriginalProcessState(
                        process.PriorityClass,
                        startTimeUtcTicks);
            }

            process.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
            {
                ProcessGovernanceResult rollback = TryRestoreOriginalPriority(processId);
                return new(false, $"Windows no confirmó el cambio de prioridad. {rollback.Message}");
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            TryRestoreOriginalPriority(processId);
            return new(false, $"No se pudo cambiar la prioridad: {ex.Message}");
        }

        try
        {
            await SaveHistoryAsync("Throttle", processId, name, cancellationToken);
        }
        catch (Exception ex)
        {
            ProcessGovernanceResult rollback = TryRestoreOriginalPriority(processId);
            return new(
                false,
                rollback.Success
                    ? $"La prioridad original fue restaurada porque falló el historial EVO: {ex.Message}"
                    : $"Falló el historial EVO y no se pudo verificar la restauración. {rollback.Message}");
        }

        return new(
            true,
            $"'{name}' (PID {processId}) quedó en BelowNormal. Es reversible.");
    }

    public async Task<ProcessGovernanceResult> RestoreAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        string? name = null;

        ProcessGovernanceResult restore = TryRestoreOriginalPriority(processId);
        if (!restore.Success)
            return restore;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new(false, $"La prioridad se restauró, pero no se pudo identificar el proceso para registrar el historial: {ex.Message}");
        }

        try
        {
            await SaveHistoryAsync("Restore", processId, name, cancellationToken);
        }
        catch (Exception ex)
        {
            return new(
                true,
                $"{restore.Message} Advertencia: la restauración no pudo guardarse en el historial EVO: {ex.Message}");
        }

        return restore;
    }

    public IReadOnlyList<int> GetThrottledProcessIds()
    {
        lock (_gate)
            return _originalPriorities.Keys.ToArray();
    }

    public static ProcessImportance ClassifyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ProcessImportance.Unknown;

        if (CriticalNames.Contains(name))
            return ProcessImportance.Critical;

        return name.ToLowerInvariant() switch
        {
            "cs2" or "cs2.exe" => ProcessImportance.High,
            "steam" or "steamwebhelper" or "discord" or "obs64" or
                "explorer" or "msedge" or "chrome" or "firefox" => ProcessImportance.Normal,
            _ => ProcessImportance.Unknown
        };
    }

    private ProcessGovernanceResult TryRestoreOriginalPriority(int processId)
    {
        OriginalProcessState original;
        lock (_gate)
        {
            if (!_originalPriorities.TryGetValue(processId, out original!))
                return new(false, "No hay una prioridad original registrada para ese PID.");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != original.StartTimeUtcTicks)
            {
                lock (_gate)
                    _originalPriorities.Remove(processId);
                return new(false, "El PID ahora pertenece a otro proceso. Se rechazó el rollback.");
            }

            if (process.PriorityClass == original.Priority)
            {
                lock (_gate)
                    _originalPriorities.Remove(processId);
                return new(true, "La prioridad original seguía activa; no se requirió rollback.");
            }

            if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
                return new(false, "La prioridad fue modificada externamente; no se sobrescribió.");

            process.PriorityClass = original.Priority;
            if (process.PriorityClass != original.Priority)
                return new(false, "Windows no confirmó la restauración de la prioridad original.");

            lock (_gate)
                _originalPriorities.Remove(processId);

            return new(true, $"Prioridad original restaurada para el PID {processId}.");
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new(false, $"No se pudo restaurar la prioridad: {ex.Message}");
        }
    }

    private async Task SaveHistoryAsync(
        string action,
        int processId,
        string? name,
        CancellationToken cancellationToken)
    {
        if (_history is null)
            throw new InvalidOperationException("No se configuró el historial EVO.");

        await _history.SaveAsync(
            "ProcessGovernance",
            new
            {
                Action = action,
                ProcessId = processId,
                ProcessName = name,
                NewPriority = action == "Throttle" ? "BelowNormal" : "Original",
                Timestamp = DateTimeOffset.UtcNow
            },
            DateTimeOffset.UtcNow,
            cancellationToken);
    }
}