using EvoOptimizer.Core.Audit;
using EvoOptimizer.Core.Diagnosis;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.Core.Tests;

public class HardwareEvidenceEvaluatorTests
{
    private static SystemAuditReport Audit(params AuditFact[] facts) =>
        new(DateTimeOffset.UtcNow, "EVO-TEST", facts, Array.Empty<string>());

    private static AuditFact Fact(string category, string name, string? value) =>
        new(category, name, value, "test", "High");

    private static AuditFact[] RamModule(
        int index,
        string locator,
        string capacity = "8589934592",
        string speed = "4800") =>
        new[]
        {
            Fact("RAM", $"[{index}] DeviceLocator", locator),
            Fact("RAM", $"[{index}] Capacity", capacity),
            Fact("RAM", $"[{index}] Speed", speed),
            Fact("RAM", $"[{index}] ConfiguredClockSpeed", speed),
            Fact("RAM", $"[{index}] Manufacturer", "TestVendor")
        };

    [Fact]
    public void Memory_TwoModulesInDistinctChannels_IsVerifiedDualChannel()
    {
        var facts = RamModule(0, "ChannelA-DIMM0")
            .Concat(RamModule(1, "ChannelB-DIMM0"))
            .Append(Fact("Arreglo de memoria", "MemoryDevices", "2"))
            .ToArray();

        var assessment = HardwareEvidenceEvaluator.AssessMemory(Audit(facts));

        Assert.Equal(DualChannelMode.VerifiedDualChannel, assessment.Mode);
        Assert.Equal(2, assessment.ChannelCount);
        Assert.Equal(2, assessment.ModuleCount);
        Assert.Equal(EvidenceConfidence.High, assessment.Confidence);
        Assert.Empty(assessment.Warnings);
    }

    [Fact]
    public void Memory_TwoModulesWithAmbiguousLocators_StaysUnknown()
    {
        var facts = RamModule(0, "DIMM 0")
            .Concat(RamModule(1, "DIMM 1"))
            .ToArray();

        var assessment = HardwareEvidenceEvaluator.AssessMemory(Audit(facts));

        Assert.Equal(DualChannelMode.Unknown, assessment.Mode);
        Assert.Null(assessment.ChannelCount);
        Assert.Contains(assessment.Warnings, w =>
            w.Contains("Dual Channel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assessment.Evidence, e =>
            e.Contains("Dual Channel verificado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Memory_SingleModule_IsVerifiedSingleChannel()
    {
        var assessment = HardwareEvidenceEvaluator.AssessMemory(
            Audit(RamModule(0, "DIMM_A1")));

        Assert.Equal(DualChannelMode.VerifiedSingleChannel, assessment.Mode);
        Assert.Equal(1, assessment.ModuleCount);
    }

    [Fact]
    public void Memory_MixedCapacities_AddsFlexModeWarningButKeepsVerification()
    {
        var facts = RamModule(0, "ChannelA-DIMM0", capacity: "8589934592")
            .Concat(RamModule(1, "ChannelB-DIMM0", capacity: "17179869184"))
            .ToArray();

        var assessment = HardwareEvidenceEvaluator.AssessMemory(Audit(facts));

        Assert.Equal(DualChannelMode.VerifiedDualChannel, assessment.Mode);
        Assert.Contains(assessment.Warnings, w =>
            w.Contains("flex mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Processor_UtilizationIsNotReportedAsPower()
    {
        var audit = Audit(
            Fact("CPU", "Name", "Intel Core i5"),
            Fact("CPU", "NumberOfCores", "6"),
            Fact("CPU", "NumberOfLogicalProcessors", "8"),
            Fact("CPU", "LoadPercentage", "37"));

        var cpu = HardwareEvidenceEvaluator.AssessProcessor(audit);

        Assert.Equal(37u, cpu.InstantaneousUtilizationPercent);
        Assert.Null(cpu.InstantaneousPowerWatts);
        Assert.Equal(EvidenceConfidence.Unknown, cpu.PowerConfidence);
        Assert.Contains("RAPL", cpu.PowerExplanation);
    }

    [Fact]
    public void Trim_DisableDeleteNotifyZero_MeansEnabled()
    {
        var audit = Audit(Fact(
            "TRIM",
            "Output",
            "NTFS DisableDeleteNotify = 0\r\nReFS DisableDeleteNotify = 0"));

        var trim = HardwareEvidenceEvaluator.AssessTrim(audit);

        Assert.True(trim.Enabled);
        Assert.Equal(EvidenceConfidence.High, trim.Confidence);
        Assert.Contains(trim.Entries, e => e.Contains("NTFS"));
    }

    [Fact]
    public void Trim_DisableDeleteNotifyOne_MeansDisabled()
    {
        var audit = Audit(Fact(
            "TRIM",
            "Output",
            "NTFS DisableDeleteNotify = 1"));

        var trim = HardwareEvidenceEvaluator.AssessTrim(audit);

        Assert.False(trim.Enabled);
    }

    [Fact]
    public void Storage_ReliabilityCounters_ProvideFullSmartCoverage()
    {
        var audit = Audit(
            Fact("Almacenamiento", "[0] DeviceId", "1"),
            Fact("Almacenamiento", "[0] FriendlyName", "NVMe Test"),
            Fact("Almacenamiento", "[0] MediaType", "SSD"),
            Fact("Almacenamiento", "[0] BusType", "NVMe"),
            Fact("Almacenamiento", "[0] HealthStatus", "Healthy"),
            Fact("Contadores de confiabilidad", "[0] DeviceId", "1"),
            Fact("Contadores de confiabilidad", "[0] Wear", "3"),
            Fact("Contadores de confiabilidad", "[0] Temperature", "41"),
            Fact("Contadores de confiabilidad", "[0] PowerOnHours", "1200"));

        var disks = HardwareEvidenceEvaluator.AssessStorage(audit);

        var disk = Assert.Single(disks);
        Assert.Equal(SmartCoverage.ReliabilityCounters, disk.SmartCoverage);
        Assert.True(disk.IsNvme);
        Assert.Equal(3d, disk.WearPercent);
        Assert.Equal(41d, disk.TemperatureCelsius);
    }

    [Fact]
    public void Storage_HealthStatusOnly_IsNotTreatedAsFullSmart()
    {
        var audit = Audit(
            Fact("Almacenamiento", "[0] DeviceId", "1"),
            Fact("Almacenamiento", "[0] FriendlyName", "SATA Test"),
            Fact("Almacenamiento", "[0] BusType", "SATA"),
            Fact("Almacenamiento", "[0] HealthStatus", "Healthy"));

        var disk = Assert.Single(HardwareEvidenceEvaluator.AssessStorage(audit));

        Assert.Equal(SmartCoverage.HealthStatusOnly, disk.SmartCoverage);
        Assert.False(disk.IsNvme);
        Assert.Contains("NO equivale a SMART completo", disk.SmartCoverageText);
    }

    [Fact]
    public void Processes_UsesDeltaBetweenSamples_NotCumulativeCpu()
    {
        var audit = Audit(
            Fact("Muestreo de procesos", "elapsedSeconds", "1"),
            Fact("Muestreo de procesos", "logicalProcessors", "8"),
            Fact("Procesos muestra 1", "[0] Id", "4242"),
            Fact("Procesos muestra 1", "[0] ProcessName", "game"),
            Fact("Procesos muestra 1", "[0] CPU", "10.0"),
            Fact("Procesos muestra 1", "[0] WorkingSet64", "104857600"),
            Fact("Procesos muestra 2", "[0] Id", "4242"),
            Fact("Procesos muestra 2", "[0] ProcessName", "game"),
            Fact("Procesos muestra 2", "[0] CPU", "10.5"),
            Fact("Procesos muestra 2", "[0] WorkingSet64", "104857600"));

        var assessment = HardwareEvidenceEvaluator.AssessProcesses(audit);

        Assert.True(assessment.HasSufficientEvidence);
        var sample = Assert.Single(assessment.TopProcesses);

        // 0.5 s de CPU en 1 s sobre 8 procesadores lógicos = 6.25 % del sistema.
        Assert.InRange(sample.InstantaneousCpuPercent, 6.2, 6.3);
        Assert.DoesNotContain(assessment.TopProcesses, s => s.InstantaneousCpuPercent >= 10);
    }

    [Fact]
    public void Processes_WithoutSecondSample_ReturnsUnknownInsteadOfZero()
    {
        var audit = Audit(
            Fact("Muestreo de procesos", "elapsedSeconds", "1"),
            Fact("Procesos muestra 1", "[0] Id", "4242"),
            Fact("Procesos muestra 1", "[0] CPU", "10.0"));

        var assessment = HardwareEvidenceEvaluator.AssessProcesses(audit);

        Assert.False(assessment.HasSufficientEvidence);
        Assert.Empty(assessment.TopProcesses);
    }
}

public class OptimizationCatalogBuilderTests
{
    private static WindowsOptimizationSnapshot Snapshot() =>
        new(false, false, false, "Equilibrado", true, 2);

    private static IReadOnlyList<OptimizationCatalogEntry> Build(
        IReadOnlyList<WindowsOptimizationChange>? changes = null,
        HardwareDiagnosticSnapshot? hardware = null,
        OptimizationLiveContext? live = null)
    {
        var snapshot = Snapshot();

        return OptimizationCatalogBuilder.Build(
            DateTimeOffset.UtcNow,
            snapshot,
            WindowsOptimizationPlanner.Build(snapshot),
            changes ?? Array.Empty<WindowsOptimizationChange>(),
            hardware,
            live ?? new OptimizationLiveContext());
    }

    private static WindowsOptimizationChange VerifiedGameModeChange() =>
        new(
            Guid.NewGuid(),
            WindowsOptimizationTarget.GameMode,
            "GameMode:AllowAutoGameMode",
            new[]
            {
                new WindowsChangeOperation(
                    WindowsChangeKind.RegistryDword,
                    @"Software\Microsoft\GameBar\AllowAutoGameMode",
                    "0")
            },
            new[]
            {
                new WindowsChangeOperation(
                    WindowsChangeKind.RegistryDword,
                    @"Software\Microsoft\GameBar\AllowAutoGameMode",
                    "1")
            },
            WindowsOptimizationStatus.PerformanceVerified,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null);

    [Fact]
    public void Build_ProducesBroadCatalogWithUniqueIds()
    {
        var entries = Build();

        Assert.True(entries.Count >= 30,
            $"Se esperaban al menos 30 entradas y llegaron {entries.Count}.");

        var duplicates = entries
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);

        foreach (var category in Enum.GetValues<OptimizationCategory>())
            Assert.Contains(entries, x => x.Category == category);
    }

    [Fact]
    public void Build_WindowsItemsAreAvailableWithActionKey()
    {
        var entries = Build();

        foreach (var target in new[]
        {
            WindowsOptimizationTarget.UwpBackground,
            WindowsOptimizationTarget.EdgeBackground,
            WindowsOptimizationTarget.WindowsSuggestions
        })
        {
            var entry = Assert.Single(entries, x =>
                string.Equals(
                    x.ActionKey,
                    $"windows:{target}",
                    StringComparison.OrdinalIgnoreCase));

            Assert.True(entry.CanApply);
            Assert.Equal(OptimizationCatalogState.Available, entry.State);
            Assert.True(entry.SupportsBaseline);
        }
    }

    [Fact]
    public void Build_PerformanceVerifiedChange_MapsToImprovementVerified()
    {
        var entries = Build(new[] { VerifiedGameModeChange() });

        var entry = Assert.Single(entries, x => x.Id == "WIN-GAMEMODE");

        Assert.Equal(OptimizationCatalogState.ImprovementVerified, entry.State);
        Assert.False(entry.CanApply);
        Assert.True(entry.CanRollback);
        Assert.True(entry.IsApplied);
    }

    [Fact]
    public void Build_WithoutHardware_StandsByUnavailableWithoutInventingSensors()
    {
        var entries = Build();

        var standby = Assert.Single(entries, x => x.Id == "MEM-SUPPRESS-STANDBY-PURGE");
        Assert.Equal(OptimizationCatalogState.NotImplemented, standby.State);
        Assert.False(standby.CanApply);

        var services = Assert.Single(entries, x => x.Id == "WIN-OPTIONAL-SERVICES");
        Assert.Equal(OptimizationCatalogState.NotApplicable, services.State);

        var thermal = Assert.Single(entries, x => x.Id == "THERM-SAFETY-LOCK-CPU-BOOST");
        Assert.NotEqual(OptimizationCatalogState.Available, thermal.State);
    }

    [Fact]
    public void Build_AdvisoryAndMonitoringEntriesNeverClaimCanApply()
    {
        var entries = Build();

        foreach (var entry in entries.Where(x =>
            x.ExecutionKind is OptimizationExecutionKind.Advisory
                or OptimizationExecutionKind.Monitoring
                or OptimizationExecutionKind.Unavailable))
        {
            Assert.False(entry.CanApply, $"{entry.Id} no debe aplicar cambios.");
        }
    }
}

public class EvoAiCoordinatorTests
{
    private static EvoAiInput Input(
        string? bottleneck = null,
        bool hasFpsEvidence = false,
        int applied = 0,
        int verified = 0,
        int inconclusive = 0,
        HardwareDiagnosticSnapshot? hardware = null,
        OptimizationLiveContext? live = null) =>
        new(
            bottleneck,
            55,
            99,
            70,
            70,
            Cs2Running: true,
            HasFpsEvidence: hasFpsEvidence,
            VerifiedOptimizations: verified,
            InconclusiveOptimizations: inconclusive,
            AppliedOptimizations: applied,
            Hardware: hardware,
            Live: live ?? new OptimizationLiveContext());

    [Fact]
    public void Analyze_WithoutEvidence_AbstainsAndWarns()
    {
        var report = EvoAiCoordinator.Analyze(Input());

        Assert.Contains("se abstiene", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Warnings, w =>
            w.Contains("Dual Channel", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("memoria", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w =>
            w.Contains("Almacenamiento UNKNOWN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w =>
            w.Contains("Temperaturas UNKNOWN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w =>
            w.Contains("INCONCLUSO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_GpuBottleneckWithAppliedCpuOptimizations_FlagsConflict()
    {
        var report = EvoAiCoordinator.Analyze(Input(
            bottleneck: "GPU",
            applied: 2));

        Assert.Contains(report.Conflicts, c =>
            c.Contains("GPU como límite", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w =>
            w.Contains("no se puede atribuir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_VerifiedOptimization_IsReportedWithEvidenceOrigin()
    {
        var report = EvoAiCoordinator.Analyze(Input(
            bottleneck: "CPU",
            hasFpsEvidence: true,
            applied: 1,
            verified: 1));

        var decision = Assert.Single(report.Decisions, d =>
            d.Title == "Optimizaciones verificadas");

        Assert.Contains("1", decision.Explanation);
        Assert.Equal("High", decision.Confidence);
    }

    [Fact]
    public void Analyze_MemoryPressureWithSaturatedGpu_AddsConflict()
    {
        var hardware = HardwareDiagnosticSnapshotFixture.Create();

        var report = EvoAiCoordinator.Analyze(Input(
            hardware: hardware,
            live: new OptimizationLiveContext(
                HighMemoryPressure: true,
                GpuPercent: 97)));

        Assert.Contains(report.Conflicts, c =>
            c.Contains("Presión de memoria", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class HardwareDiagnosticSnapshotFixture
{
    public static HardwareDiagnosticSnapshot Create(
        DualChannelMode mode = DualChannelMode.VerifiedDualChannel,
        SmartCoverage coverage = SmartCoverage.HealthStatusOnly) =>
        new(
            DateTimeOffset.UtcNow,
            "EVO-TEST",
            new DualChannelAssessment(
                mode,
                EvidenceConfidence.High,
                2,
                2,
                2,
                Array.Empty<MemoryModuleEvidence>(),
                new[] { "Canales 2" },
                Array.Empty<string>()),
            new ProcessorEvidence(
                "Intel Core i5", 6, 8, 4400, 3800, 45, null,
                EvidenceConfidence.Unknown,
                "Sin RAPL la potencia es UNKNOWN."),
            new[]
            {
                new StorageDeviceEvidence(
                    "NVMe Test", "SSD", "NVMe", true, "Healthy", "OK",
                    512UL * 1024 * 1024 * 1024, null, null, null, null, null, null,
                    coverage)
            },
            new TrimAssessment(true, new[] { "NTFS: TRIM activo" }, EvidenceConfidence.High),
            new PowerPlanEvidence("guid", "Equilibrado", null, EvidenceConfidence.High),
            new ServiceEvidence(10, 1, new[] { "Servicio X" }),
            new StartupEntryEvidence(Array.Empty<string>()),
            new ProcessLoadAssessment(
                Array.Empty<ProcessLoadSample>(),
                false,
                null,
                Array.Empty<string>()));
}

public class ProcessGovernanceServiceTests
{
    [Fact]
    public void Classify_ProtectsCriticalProcessesByName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new ProcessGovernanceService();

        var governed = service.Classify(new[]
        {
            new ProcessSummary(999_999, "csrss", 0, 10),
            new ProcessSummary(999_998, "dotnet", 0, 10)
        });

        Assert.Equal(
            ProcessImportance.Critical,
            governed.Single(x => x.Name == "csrss").Importance);
        Assert.NotEqual(
            ProcessImportance.Critical,
            governed.Single(x => x.Name == "dotnet").Importance);
    }

    [Fact]
    public async Task Throttle_WithoutAuthorization_ChangesNothing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new ProcessGovernanceService();

        var result = await service.ThrottleAsync(
            Environment.ProcessId,
            userAuthorized: false);

        Assert.False(result.Success);
        Assert.Contains("autorización", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}