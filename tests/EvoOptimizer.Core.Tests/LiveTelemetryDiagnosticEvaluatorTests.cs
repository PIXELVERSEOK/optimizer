using EvoOptimizer.Core.Diagnosis;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Telemetry;

namespace EvoOptimizer.Core.Tests;

public sealed class LiveTelemetryDiagnosticEvaluatorTests
{
    private static readonly HardwareProfile Hardware = new(
        "Windows test",
        "Example CPU",
        8,
        3200,
        16UL * 1024 * 1024 * 1024,
        new[] { new GpuInfo("Example GPU", null) });

    private static TelemetrySnapshot Sample(double? cpu, double? memory, int second = 0)
        => new(
            DateTimeOffset.UnixEpoch.AddSeconds(second),
            cpu,
            memory,
            16UL * 1024 * 1024 * 1024,
            8UL * 1024 * 1024 * 1024,
            2UL * 1024 * 1024 * 1024,
            24UL * 1024 * 1024 * 1024);

    [Fact]
    public void Evaluate_InsufficientSamples_ReportsCoverageWithoutInventingLoad()
    {
        var findings = LiveTelemetryDiagnosticEvaluator.Evaluate(
            Hardware,
            new[] { Sample(null, null), Sample(null, 50, 1) });

        var coverage = Assert.Single(findings, finding =>
            finding.Id == "LIVE_TELEMETRY_COVERAGE");
        Assert.Contains("CPU 0/5", coverage.Explanation);
        Assert.Contains("memoria 1/5", coverage.Explanation);
        Assert.DoesNotContain(findings, finding =>
            finding.Id is "LIVE_CPU_LOAD" or "LIVE_MEMORY_LOAD");
    }

    [Fact]
    public void Evaluate_NormalSamples_ReportsMeasuredAverageAndMaximum()
    {
        var findings = LiveTelemetryDiagnosticEvaluator.Evaluate(
            Hardware,
            Enumerable.Range(0, 5).Select(second => Sample(20 + second, 45 + second, second)).ToArray());

        var cpu = Assert.Single(findings, finding => finding.Id == "LIVE_CPU_LOAD");
        Assert.Equal(DiagnosticSeverity.Information, cpu.Severity);
        Assert.Contains("promedió 22%", cpu.Explanation);
        Assert.Contains("alcanzó 24%", cpu.Explanation);

        var memory = Assert.Single(findings, finding => finding.Id == "LIVE_MEMORY_LOAD");
        Assert.Equal(DiagnosticSeverity.Information, memory.Severity);
        Assert.Contains("promedió 47%", memory.Explanation);
    }

    [Fact]
    public void Evaluate_SustainedHighCpuLoad_ReportsObservationNotBottleneck()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(second => Sample(second < 8 ? 92 : 20, 50, second))
            .ToArray();

        var cpu = Assert.Single(
            LiveTelemetryDiagnosticEvaluator.Evaluate(Hardware, samples),
            finding => finding.Id == "LIVE_CPU_LOAD");

        Assert.Equal(DiagnosticSeverity.Warning, cpu.Severity);
        Assert.Contains("8/10 muestras", cpu.Explanation);
        Assert.Contains("≥80% de 10 muestras", cpu.Evidence);
        Assert.Contains("no un diagnóstico de cuello de botella", cpu.Recommendation);
    }

    [Fact]
    public void Evaluate_PeaksBelowSustainedRatio_DoNotWarn()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(second => Sample(second < 7 ? 95 : 20, 50, second))
            .ToArray();

        var cpu = Assert.Single(
            LiveTelemetryDiagnosticEvaluator.Evaluate(Hardware, samples),
            finding => finding.Id == "LIVE_CPU_LOAD");

        Assert.Equal(DiagnosticSeverity.Information, cpu.Severity);
        Assert.Contains("no cumple el criterio", cpu.Explanation);
    }

    [Fact]
    public void Evaluate_SustainedHighMemoryLoad_ReportsMeasuredPressure()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(second => Sample(30, second < 4 ? 93 : 70, second))
            .ToArray();

        var memory = Assert.Single(
            LiveTelemetryDiagnosticEvaluator.Evaluate(Hardware, samples),
            finding => finding.Id == "LIVE_MEMORY_LOAD");

        Assert.Equal(DiagnosticSeverity.Warning, memory.Severity);
        Assert.Contains("4/5 muestras", memory.Explanation);
        Assert.Contains("no un diagnóstico de cuello de botella", memory.Recommendation);
    }

    [Fact]
    public void Evaluate_ReportsHardwareIdentityFromProvidedProfile()
    {
        var finding = Assert.Single(
            LiveTelemetryDiagnosticEvaluator.Evaluate(Hardware, Array.Empty<TelemetrySnapshot>()),
            item => item.Id == "LIVE_HARDWARE_PROFILE");

        Assert.Contains("Example CPU", finding.Explanation);
        Assert.Contains("8 procesadores lógicos", finding.Explanation);
        Assert.Contains("16.0 GB", finding.Explanation);
        Assert.Contains("Example GPU", finding.Explanation);
    }
}
