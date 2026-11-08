using System.Diagnostics;
using EvoOptimizer.Core.Audit;
using EvoOptimizer.Core.Diagnosis;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;
using EvoOptimizer.Infrastructure.Analysis;

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

    [Fact]
    public void Services_InventoryPreservesRealStateAndStartMode()
    {
        var audit = Audit(
            Fact("Servicios", "[0] Name", "Spooler"),
            Fact("Servicios", "[0] DisplayName", "Print Spooler"),
            Fact("Servicios", "[0] State", "Running"),
            Fact("Servicios", "[0] StartMode", "Auto"),
            Fact("Servicios", "[0] ProcessId", "1234"),
            Fact("Servicios", "[1] Name", "Example"),
            Fact("Servicios", "[1] State", "Stopped"),
            Fact("Servicios", "[1] StartMode", "Manual"),
            Fact("Servicios", "[1] ProcessId", "0"));

        var evidence = HardwareEvidenceEvaluator.AssessServices(audit);

        Assert.Equal(1, evidence.AutoStartCount);
        Assert.Equal(0, evidence.StoppedAutoStartCount);
        Assert.Equal(2, evidence.Inventory?.Count);
        Assert.Equal("Print Spooler", evidence.Inventory?[0].DisplayName);
        Assert.Equal("Running", evidence.Inventory?[0].State);
        Assert.Equal("Auto", evidence.Inventory?[0].StartMode);
        Assert.Equal(1234, evidence.Inventory?[0].ProcessId);
        Assert.Equal("Example", evidence.Inventory?[1].DisplayName);
        Assert.Equal("Stopped", evidence.Inventory?[1].State);
        Assert.Equal("Manual", evidence.Inventory?[1].StartMode);
        Assert.Equal(0, evidence.Inventory?[1].ProcessId);
    }

    private static AuditFact[] Gpu(
        int index,
        string name = "Intel UHD Graphics",
        string? driverVersion = "31.0.101.2111",
        string? driverDate = "2024-05-09") =>
        new[]
        {
            Fact("GPU", $"[{index}] Name", name),
            Fact("GPU", $"[{index}] AdapterCompatibility", "Intel Corporation"),
            Fact("GPU", $"[{index}] DriverVersion", driverVersion),
            Fact("GPU", $"[{index}] DriverDate", driverDate),
            Fact("GPU", $"[{index}] Status", "OK"),
            Fact("GPU", $"[{index}] CurrentRefreshRate", "200")
        };

    [Fact]
    public void Graphics_DriverVersionAndDate_AreSurfacedAsEvidence()
    {
        var devices = HardwareEvidenceEvaluator.AssessGraphics(Audit(Gpu(0)));

        var device = Assert.Single(devices);
        Assert.Equal("Intel UHD Graphics", device.Name);
        Assert.Equal("31.0.101.2111", device.DriverVersion);
        Assert.Equal("2024-05-09", device.DriverDate);
        Assert.True(device.HasDriverEvidence);
        Assert.Equal(200u, device.RefreshRateHz);
    }

    [Fact]
    public void Graphics_MissingDriver_StaysNullNotInvented()
    {
        var devices = HardwareEvidenceEvaluator.AssessGraphics(
            Audit(Gpu(0, driverVersion: null, driverDate: null)));

        var device = Assert.Single(devices);
        Assert.Null(device.DriverVersion);
        Assert.Null(device.DriverDate);
        Assert.False(device.HasDriverEvidence);
    }

    [Fact]
    public void Graphics_MultipleAdapters_AreAllListed()
    {
        var facts = Gpu(0)
            .Concat(Gpu(1, name: "Microsoft Basic Render Driver",
                driverVersion: null, driverDate: null))
            .ToArray();

        var devices = HardwareEvidenceEvaluator.AssessGraphics(Audit(facts));

        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].HasDriverEvidence);
        Assert.False(devices[1].HasDriverEvidence);
    }

    [Fact]
    public void Graphics_NoData_ReturnsEmptyInsteadOfFakeDevice()
    {
        var devices = HardwareEvidenceEvaluator.AssessGraphics(
            Audit(Fact("CPU", "Name", "Intel")));

        Assert.Empty(devices);
    }

    [Fact]
    public void Evaluate_SnapshotIncludesGraphicsEvidence()
    {
        var snapshot = HardwareEvidenceEvaluator.Evaluate(Audit(Gpu(0)));

        Assert.Single(snapshot.Graphics);
        Assert.Equal("Intel UHD Graphics", snapshot.Graphics[0].Name);
    }

    [Fact]
    public void Graphics_SingleObjectWithoutIndex_IsStillRead()
    {
        // PowerShell 5.1 serializa una única GPU como objeto JSON (hechos sin
        // prefijo [n]). No debe perderse el adaptador ni su driver.
        var devices = HardwareEvidenceEvaluator.AssessGraphics(Audit(
            Fact("GPU", "Name", "Intel(R) UHD Graphics"),
            Fact("GPU", "DriverVersion", "32.0.101.6913"),
            Fact("GPU", "AdapterCompatibility", "Intel Corporation")));

        var device = Assert.Single(devices);
        Assert.Equal("Intel(R) UHD Graphics", device.Name);
        Assert.Equal("32.0.101.6913", device.DriverVersion);
        Assert.True(device.HasDriverEvidence);
    }

    [Fact]
    public void Storage_SingleDiskWithoutIndex_IsReadFromDiskDriveFallback()
    {
        // Get-PhysicalDisk vacío + un único Win32_DiskDrive serializado como
        // objeto (sin [n]): el respaldo debe leerlo igual.
        var storage = HardwareEvidenceEvaluator.AssessStorage(Audit(
            Fact("Disco físico", "Model", "KINGSTON SA400S37480G"),
            Fact("Disco físico", "InterfaceType", "IDE"),
            Fact("Disco físico", "Size", "480101368320"),
            Fact("Disco físico", "Status", "OK")));

        var disk = Assert.Single(storage);
        Assert.Equal("KINGSTON SA400S37480G", disk.Name);
        Assert.Equal(480101368320UL, disk.SizeBytes);
        Assert.False(disk.IsNvme);
    }

    [Fact]
    public void Memory_SingleModuleWithoutIndex_IsRead()
    {
        var assessment = HardwareEvidenceEvaluator.AssessMemory(Audit(
            Fact("RAM", "DeviceLocator", "DIMM_A1"),
            Fact("RAM", "Capacity", "8589934592"),
            Fact("RAM", "Speed", "4800"),
            Fact("RAM", "ConfiguredClockSpeed", "4800")));

        Assert.Equal(1, assessment.ModuleCount);
        Assert.Equal(DualChannelMode.VerifiedSingleChannel, assessment.Mode);
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

    [Fact]
    public void Analyze_WithGraphicsDriverEvidence_ReportsGpuDecisionWithoutUpdateAdvice()
    {
        var hardware = HardwareDiagnosticSnapshotFixture.Create();

        var report = EvoAiCoordinator.Analyze(Input(hardware: hardware));

        var decision = Assert.Single(report.Decisions, d => d.Title == "GPU y driver");
        Assert.Contains("31.0.101.2111", decision.Explanation);
        Assert.Contains("no declara el driver como antiguo", decision.Explanation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Medium", decision.Confidence);
    }

    [Fact]
    public void Analyze_WithoutGraphicsEvidence_WarnsGpuUnknown()
    {
        var report = EvoAiCoordinator.Analyze(Input());

        Assert.Contains(report.Warnings, w =>
            w.Contains("GPU UNKNOWN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Decisions, d => d.Title == "GPU y driver");
    }

    [Fact]
    public void Analyze_GraphicsWithoutDriver_DecisionConfidenceIsUnknown()
    {
        var hardware = HardwareDiagnosticSnapshotFixture.Create(
            graphics: new[]
            {
                new GraphicsDeviceEvidence(
                    0, "Intel UHD Graphics", "Intel Corporation",
                    null, null, null, null, "OK", null, null, null)
            });

        var report = EvoAiCoordinator.Analyze(Input(hardware: hardware));

        var decision = Assert.Single(report.Decisions, d => d.Title == "GPU y driver");
        Assert.Equal("UNKNOWN", decision.Confidence);
        Assert.Contains("UNKNOWN", decision.Explanation);
    }
}

public class AdaptiveDiagnosticEngineGraphicsTests
{
    private static SystemAuditReport Audit(params AuditFact[] facts) =>
        new(DateTimeOffset.UtcNow, "EVO-TEST", facts, Array.Empty<string>());

    private static AuditFact Fact(string name, string? value) =>
        new("GPU", name, value, "test", "High");

    [Fact]
    public void Analyze_GpuWithDriver_IncludesDriverEvidenceInProfile()
    {
        var report = new AdaptiveDiagnosticEngine().Analyze(Audit(
            Fact("[0] Name", "Intel UHD Graphics"),
            Fact("[0] DriverVersion", "31.0.101.2111"),
            Fact("[0] DriverDate", "2024-05-09")));

        var profile = Assert.Single(report.Findings, f => f.Id == "GPU_PROFILE");
        Assert.Contains("31.0.101.2111", profile.Evidence);
        Assert.Contains("2024-05-09", profile.Evidence);
        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("GPU_DRIVER_UNKNOWN"));
    }

    [Fact]
    public void Analyze_GpuWithoutDriver_ReportsUnknownFinding()
    {
        var report = new AdaptiveDiagnosticEngine().Analyze(Audit(
            Fact("[0] Name", "Intel UHD Graphics")));

        Assert.Contains(report.Findings, f => f.Id == "GPU_DRIVER_UNKNOWN_0");
        var profile = Assert.Single(report.Findings, f => f.Id == "GPU_PROFILE");
        Assert.Contains("DriverVersion: UNKNOWN", profile.Evidence);
    }
}

internal static class HardwareDiagnosticSnapshotFixture
{
    public static HardwareDiagnosticSnapshot Create(
        DualChannelMode mode = DualChannelMode.VerifiedDualChannel,
        SmartCoverage coverage = SmartCoverage.HealthStatusOnly,
        IReadOnlyList<GraphicsDeviceEvidence>? graphics = null) =>
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
            graphics ?? new[]
            {
                new GraphicsDeviceEvidence(
                    0, "Intel UHD Graphics", "Intel Corporation",
                    "31.0.101.2111", "2024-05-09", "Intel UHD Graphics",
                    "PCI\\VEN_8086", "OK", 1920, 1080, 200)
            },
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
    public void ClassifyName_ReturnsUnknownForUnrecognizedProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            ProcessImportance.Unknown,
            ProcessGovernanceService.ClassifyName("unknown-evo-test-process"));
    }

    public class NetworkProbeSummaryTests
    {
        [Fact]
        public void FromSamples_UsesSuccessfulRepliesAndCountsLoss()
        {
            NetworkProbeSummary summary = NetworkProbeSummary.FromSamples(
                new[]
                {
                    new NetworkPingSample("gateway", "Success", 4, null),
                    new NetworkPingSample("gateway", "TimedOut", null, null),
                    new NetworkPingSample("gateway", "Success", 8, null)
                });

            Assert.Equal(3, summary.Sent);
            Assert.Equal(2, summary.Received);
            Assert.Equal(1, summary.Lost);
            Assert.Equal(100d / 3, summary.PacketLossPercent!.Value, precision: 1);
            Assert.Equal(4d, summary.MinimumLatencyMs);
            Assert.Equal(6d, summary.AverageLatencyMs);
            Assert.Equal(8d, summary.MaximumLatencyMs);
        }

        [Fact]
        public void FromSamples_DoesNotInventLatencyWhenNoProbeReplies()
        {
            NetworkProbeSummary summary = NetworkProbeSummary.FromSamples(
                new[]
                {
                    new NetworkPingSample("gateway", "TimedOut", null, null),
                    new NetworkPingSample("gateway", "Error", null, "blocked")
                });

            Assert.Equal(2, summary.Sent);
            Assert.Equal(0, summary.Received);
            Assert.Equal(2, summary.Lost);
            Assert.Equal(100, summary.PacketLossPercent);
            Assert.Null(summary.AverageLatencyMs);
        }
    }

    public class MemoryPressureEvaluatorTests
    {
        [Fact]
        public void Evaluate_UsesMeasuredMemoryAndCommitPressure()
        {
            var result = new MemoryPressureEvaluator().Evaluate(
                new MemoryPressureEvidence(
                    TotalPhysicalBytes: 16_000,
                    AvailablePhysicalBytes: 1_200,
                    CommitChargeBytes: 9_500,
                    CommitLimitBytes: 10_000,
                    SystemHardFaultsPerSecond: null));

            Assert.True(result.HasSufficientEvidence);
            Assert.Equal(MemoryPressureLevel.High, result.Level);
            Assert.Equal(92.5, result.PhysicalMemoryUsedPercent!.Value, precision: 1);
            Assert.Equal(95d, result.CommitUsedPercent);
            Assert.Null(result.HardFaultsPerSecond);
        }

        [Fact]
        public void Evaluate_LeavesMissingEvidenceUnknown()
        {
            var result = new MemoryPressureEvaluator().Evaluate(
                new MemoryPressureEvidence(null, null, null, null, null));

            Assert.False(result.HasSufficientEvidence);
            Assert.Equal(MemoryPressureLevel.Unknown, result.Level);
            Assert.Null(result.PhysicalMemoryUsedPercent);
            Assert.Null(result.CommitUsedPercent);
        }
    }

    [Fact]
    public async Task Throttle_ProtectsUnclassifiedCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using Process process = Process.GetCurrentProcess();
        ProcessPriorityClass originalPriority = process.PriorityClass;
        var service = new ProcessGovernanceService();

        ProcessGovernanceResult result =
            await service.ThrottleAsync(process.Id, userAuthorized: true);

        Assert.False(result.Success);
        Assert.Contains("no está clasificado", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalPriority, process.PriorityClass);
    }

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

/// <summary>
/// Verificación funcional de solo lectura sobre Windows real: ejecuta el auditor
/// CIM (PowerShell 5.1), lo evalúa y lo analiza. No modifica ninguna configuración.
/// Se omite fuera de Windows.
/// </summary>
public sealed class WindowsSystemAuditorIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public WindowsSystemAuditorIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task CaptureAsync_OnRealWindows_ProducesReadOnlyEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var auditor = new EvoOptimizer.Infrastructure.Audit.WindowsSystemAuditor();
        var report = await auditor.CaptureAsync();

        Assert.NotEmpty(report.Facts);

        var snapshot = HardwareEvidenceEvaluator.Evaluate(report);
        Assert.Equal(report.CapturedAt, snapshot.MeasuredAt);

        _output.WriteLine($"CPU: {snapshot.Cpu.Name ?? "UNKNOWN"} " +
                          $"({snapshot.Cpu.PhysicalCores?.ToString() ?? "?"}/" +
                          $"{snapshot.Cpu.LogicalProcessors?.ToString() ?? "?"})");
        _output.WriteLine($"Memoria: {snapshot.Memory.ModeText} " +
                          $"· módulos={snapshot.Memory.ModuleCount}");
        _output.WriteLine($"GPU: {snapshot.Graphics.Count} adaptador(es)");
        foreach (var gpu in snapshot.Graphics)
            _output.WriteLine($"  {gpu.Name ?? "UNKNOWN"} · driver " +
                              $"{gpu.DriverVersion ?? "UNKNOWN"} ({gpu.DriverDate ?? "UNKNOWN"})");
        foreach (var disk in snapshot.Storage)
            _output.WriteLine($"Disco: {disk.Name ?? "UNKNOWN"} · NVMe={disk.IsNvme?.ToString() ?? "UNKNOWN"} " +
                              $"· {disk.SmartCoverageText}");
        _output.WriteLine($"TRIM: {snapshot.Trim.Enabled?.ToString() ?? "UNKNOWN"}");
        _output.WriteLine($"Plan: {snapshot.PowerPlan.Name ?? "UNKNOWN"}");
        _output.WriteLine($"Procesos con CPU instantánea: {snapshot.Processes.TopProcesses.Count}");

        var diagnostic = new AdaptiveDiagnosticEngine().Analyze(report);
        Assert.NotEmpty(diagnostic.Findings);
        _output.WriteLine($"Hallazgos: {diagnostic.Findings.Count}");

        // La potencia en watts nunca se inventa sin RAPL/PDH.
        Assert.Null(snapshot.Cpu.InstantaneousPowerWatts);
        Assert.Equal(EvidenceConfidence.Unknown, snapshot.Cpu.PowerConfidence);
    }
}

/// <summary>
/// Regresión del crash de arranque: WindowsPowerTelemetryService liberaba el
/// buffer de PowerGetActiveScheme con un export inexistente (PowerFreeMemory),
/// provocando EntryPointNotFoundException dentro del constructor en cadena y
/// tumbando el load de XAML. Ahora se libera con LocalFree y la lectura opcional
/// degrada a UNKNOWN sin lanzar.
/// </summary>
public sealed class WindowsPowerTelemetryStartupTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public WindowsPowerTelemetryStartupTests(Xunit.Abstractions.ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public void ReadCurrent_OnRealWindows_DoesNotThrowEntryPoint()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var telemetry = new EvoOptimizer.Infrastructure.Hardware.WindowsPowerTelemetryService()
            .ReadCurrent();

        // Si el plan activo es legible, debe ser un GUID real; si no, UNKNOWN
        // con advertencia. Nunca una excepción de punto de entrada.
        if (telemetry.ActiveSchemeGuid is not null)
            Assert.True(Guid.TryParse(telemetry.ActiveSchemeGuid, out _));
        else
            Assert.NotEmpty(telemetry.Warnings);

        _output.WriteLine($"AC: {telemetry.IsOnAcPower?.ToString() ?? "UNKNOWN"} · " +
                          $"plan: {telemetry.ActiveSchemeGuid ?? "UNKNOWN"} · " +
                          $"EPP AC/DC: {telemetry.ProcessorEppAc?.ToString() ?? "?"}/" +
                          $"{telemetry.ProcessorEppDc?.ToString() ?? "?"}");
    }

    [Fact]
    public void ReadConfiguration_StartupPath_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Esta es la llamada exacta que hacía el constructor del Control Center
        // durante el load de XAML; no debe propagar ninguna excepción.
        var config = new EvoOptimizer.Infrastructure.Hardware.WindowsGamingConfigService()
            .ReadConfiguration();

        Assert.NotNull(config.DiagnosticNotes);
        _output.WriteLine($"Plan: {config.PowerPlanName} · " +
                          $"AC: {config.IsOnAcPower?.ToString() ?? "UNKNOWN"} · " +
                          $"Alto rendimiento: {config.HasHighPerformancePlan}");
    }
}