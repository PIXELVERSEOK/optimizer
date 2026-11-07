namespace EvoOptimizer.Core.Audit;

/// <summary>Auditoría de solo lectura. Los campos desconocidos permanecen nulos.</summary>
public sealed record AuditFact(string Category, string Name, string? Value, string EvidenceSource, string Confidence);
public sealed record SystemAuditReport(DateTimeOffset CapturedAt, string ComputerName, IReadOnlyList<AuditFact> Facts, IReadOnlyList<string> Warnings);
