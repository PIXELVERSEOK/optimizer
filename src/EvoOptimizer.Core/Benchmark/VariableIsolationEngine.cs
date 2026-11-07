namespace EvoOptimizer.Core.Benchmark;

public enum VariableChangeType
{
    Added,
    Removed,
    Modified,
    Unknown
}

public sealed record VariableDifference(
    string Name,
    string PreviousValue,
    string CurrentValue,
    VariableChangeType ChangeType);

public sealed record VariableIsolationReport(
    bool Isolated,
    int ChangedVariableCount,
    IReadOnlyList<VariableDifference> Differences,
    IReadOnlyList<string> MissingVariables,
    string Explanation,
    DateTimeOffset AnalyzedAt);

public sealed class VariableIsolationEngine
{
    public VariableIsolationReport Compare(
        IReadOnlyDictionary<string, string> control,
        IReadOnlyDictionary<string, string> candidate,
        IReadOnlySet<string>? allowedChanges = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(candidate);

        var differences = new List<VariableDifference>();
        var missing = new List<string>();

        var keys = control.Keys
            .Concat(candidate.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            bool hasOld = control.TryGetValue(key, out var oldValue);
            bool hasNew = candidate.TryGetValue(key, out var newValue);

            if (!hasOld || !hasNew)
            {
                missing.Add(key);

                differences.Add(new VariableDifference(
                    key,
                    hasOld ? oldValue ?? "" : "NOT_PRESENT",
                    hasNew ? newValue ?? "" : "NOT_PRESENT",
                    hasOld
                        ? VariableChangeType.Removed
                        : VariableChangeType.Added));

                continue;
            }

            if (!string.Equals(
                oldValue?.Trim(),
                newValue?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new VariableDifference(
                    key,
                    oldValue ?? "",
                    newValue ?? "",
                    VariableChangeType.Modified));
            }
        }

        var unexpected = differences
            .Where(x =>
                allowedChanges == null ||
                !allowedChanges.Contains(x.Name))
            .ToArray();

        bool isolated =
            missing.Count == 0 &&
            unexpected.Length <= 1;

        var explanation = isolated
            ? "Los cambios están aislados o limitados a una variable permitida."
            : "Se detectaron múltiples cambios o variables ausentes. No se puede atribuir el resultado a una sola optimización.";

        return new VariableIsolationReport(
            isolated,
            differences.Count,
            differences,
            missing,
            explanation,
            DateTimeOffset.UtcNow);
    }
}
