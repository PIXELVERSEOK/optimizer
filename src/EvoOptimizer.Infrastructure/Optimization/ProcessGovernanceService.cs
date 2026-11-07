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
    private static readonly HashSet<string> CriticalNames = new(
        new[]
        {
            "system", "idle", "registry", "memory compression", "csrss",
            "wininit", "winlogon", "services", "lsass", "smss", "svchost",
            "dwm", "iskrnl", "fontdrvhost", "audiodg", "sihost", "taskhostw",
            "ctfmon", "explorer", "wudfhost", "securityhealthservice"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, ProcessPriorityClass> _originalPriorities = new();
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
        CancellationToken cancellationToken = default)
    {
        if (!userAuthorized)
            return new(false, "Bajar la prioridad requiere autorización explícita.");

        string? name;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;

            if (ClassifyName(process.ProcessName) == ProcessImportance.Critical)
            {
                return new(
                    false,
                    $"'{process.ProcessName}' es un proceso crítico protegido. No se modifica.");
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

            lock (_gate)
            {
                if (!_originalPriorities.ContainsKey(processId))
                    _originalPriorities[processId] = process.PriorityClass;
            }

            process.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
            {
                RestoreSilently(processId);
                return new(false, "Windows no confirmó el cambio de prioridad.");
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            RestoreSilently(processId);
            return new(false, $"No se pudo cambiar la prioridad: {ex.Message}");
        }

        await SaveHistoryAsync("Throttle", processId, name, cancellationToken);

        return new(
            true,
            $"'{name}' (PID {processId}) quedó en BelowNormal. Es reversible.");
    }

    public async Task<ProcessGovernanceResult> RestoreAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        string? name = null;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;

            ProcessPriorityClass original;

            lock (_gate)
            {
                if (!_originalPriorities.TryGetValue(processId, out original))
                    return new(false, "No hay un cambio de prioridad registrado para ese PID.");
            }

            process.PriorityClass = original;

            lock (_gate)
                _originalPriorities.Remove(processId);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new(false, $"No se pudo restaurar la prioridad: {ex.Message}");
        }

        await SaveHistoryAsync("Restore", processId, name, cancellationToken);

        return new(true, $"Prioridad original restaurada para el PID {processId}.");
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
            _ => ProcessImportance.Background
        };
    }

    private void RestoreSilently(int processId)
    {
        try
        {
            ProcessPriorityClass original;

            lock (_gate)
            {
                if (!_originalPriorities.TryGetValue(processId, out original))
                    return;

                _originalPriorities.Remove(processId);
            }

            using var process = Process.GetProcessById(processId);
            process.PriorityClass = original;
        }
        catch
        {
            // El proceso pudo finalizar; no hay nada que restaurar.
        }
    }

    private async Task SaveHistoryAsync(
        string action,
        int processId,
        string? name,
        CancellationToken cancellationToken)
    {
        if (_history is null)
            return;

        try
        {
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
        catch
        {
            // El cambio de prioridad no debe fallar por SQLite.
        }
    }
}