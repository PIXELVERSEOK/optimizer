using System.Text.Json;
using EvoOptimizer.Core.Optimization;

namespace EvoOptimizer.Core.Tests;

public class AdaptiveOptimizationEngineTests
{
    private readonly AdaptiveOptimizationEngine _engine = new();

    [Fact]
    public void Evaluate_EmptySignals_ReturnsNoRecommendations()
    {
        var report = _engine.Evaluate(Array.Empty<OptimizationSignal>());

        Assert.Equal(0, report.RecommendationsCount);
        Assert.True(report.Warnings.Count > 0);
    }

    [Fact]
    public void Evaluate_AbsentSignal_ProducesNoRecommendation()
    {
        var signals = new[]
        {
            new OptimizationSignal("HIGH_CPU_PRESSURE", false, 0.80, "CPU carga normal")
        };

        var report = _engine.Evaluate(signals);

        Assert.Equal(0, report.RecommendationsCount);
    }

    [Fact]
    public void Evaluate_HighCpuPressureSignal_ProducesInvestigateRecommendation()
    {
        var signals = new[]
        {
            new OptimizationSignal("HIGH_CPU_PRESSURE", true, 0.90, "CPU al 92% en 8 de 10 muestras")
        };

        var report = _engine.Evaluate(signals);

        Assert.Equal(1, report.RecommendationsCount);
        var rec = report.Recommendations[0];
        Assert.Equal(OptimizationAction.InvestigateCpuPressure, rec.Action);
        Assert.Equal(OptimizationPriority.High, rec.Priority);
        Assert.False(rec.CanApply); // Investigar no tiene CanApply
    }

    [Fact]
    public void Evaluate_HighMemoryPressureSignal_ProducesInvestigateRecommendation()
    {
        var signals = new[]
        {
            new OptimizationSignal("HIGH_MEMORY_PRESSURE", true, 0.85, "RAM al 93% disponible")
        };

        var report = _engine.Evaluate(signals);

        Assert.Equal(1, report.RecommendationsCount);
        var rec = report.Recommendations[0];
        Assert.Equal(OptimizationAction.InvestigateMemoryPressure, rec.Action);
        Assert.Equal(OptimizationPriority.High, rec.Priority);
    }

    [Fact]
    public void Evaluate_GameModDisabledHighConfidence_CanApply()
    {
        var signals = new[]
        {
            new OptimizationSignal("GAME_MODE_DISABLED", true, 0.95, "GameMode = 0 en registro")
        };

        // Sin benchmark: no se puede confirmar mejora, pero la señal de confianza alta permite aplicar
        var report = _engine.Evaluate(signals, benchmarkAvailable: false);

        Assert.Equal(1, report.RecommendationsCount);
        var rec = report.Recommendations[0];
        Assert.Equal(OptimizationAction.EnableGameMode, rec.Action);
        // Con confianza >= 0.75 y riesgo Low, CanApply debe ser true
        Assert.True(rec.CanApply);
        Assert.True(rec.RequiresUserApproval);
    }

    [Fact]
    public void Evaluate_UnknownSignal_IsIgnored()
    {
        var signals = new[]
        {
            new OptimizationSignal("SIGNAL_DESCONOCIDA_XYZ", true, 0.90, "evidencia")
        };

        var report = _engine.Evaluate(signals);

        Assert.Equal(0, report.RecommendationsCount);
    }

    [Fact]
    public void Evaluate_InvalidConfidence_SignalIsFiltered()
    {
        var signals = new[]
        {
            new OptimizationSignal("HIGH_CPU_PRESSURE", true, 1.5, "confianza fuera de rango")
        };

        var report = _engine.Evaluate(signals);

        // Señal inválida filtrada → 0 recomendaciones
        Assert.Equal(0, report.RecommendationsCount);
    }

    [Fact]
    public void Evaluate_DuplicateSignals_DeduplicatesByHighestConfidence()
    {
        var signals = new[]
        {
            new OptimizationSignal("HIGH_CPU_PRESSURE", true, 0.70, "primera"),
            new OptimizationSignal("HIGH_CPU_PRESSURE", true, 0.90, "segunda con más confianza")
        };

        var report = _engine.Evaluate(signals);

        // Deduplicado → solo 1 recomendación
        Assert.Equal(1, report.RecommendationsCount);
        Assert.Equal(90.0, report.Recommendations[0].ConfidencePercent, precision: 1);
    }

    [Fact]
    public void Evaluate_NoBenchmarkWithBenchmarkRequired_AddsWarning()
    {
        var signals = new[]
        {
            new OptimizationSignal("GAMEDVR_CAPTURE_ENABLED", true, 0.80, "captura activa")
        };

        var report = _engine.Evaluate(signals, benchmarkAvailable: false);

        Assert.Contains(report.Warnings, w => w.Contains("benchmark", StringComparison.OrdinalIgnoreCase));
    }
}

public class RegressionGuardTests
{
    private readonly RegressionGuard _guard = new();

    private static RegressionMeasurement MakeMeasurement(
        double avgFps, double onePercentLow = 80.0,
        string scenario = "dust2", string resolution = "1920x1080")
        => new(
            DateTimeOffset.UtcNow,
            scenario,
            resolution,
            "PresentMon",
            avgFps,
            onePercentLow,
            PointOnePercentLowFps: onePercentLow * 0.75,
            P99FrametimeMs: 1000.0 / onePercentLow,
            StutterRatePercent: 1.0,
            DurationSeconds: 60,
            FrameCount: 3600);

    [Fact]
    public void Evaluate_NullWatch_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _guard.Evaluate(null!));
    }

    [Fact]
    public void Evaluate_NoRecentMeasurements_ReturnsInconclusive()
    {
        var watch = new RegressionWatch(
            Guid.NewGuid(),
            "opt-id",
            "Test Optimization",
            RestoreEntryId: null,
            MakeMeasurement(120.0),
            Array.Empty<RegressionMeasurement>());

        var result = _guard.Evaluate(watch);

        Assert.Equal(RegressionStatus.Inconclusive, result.Status);
    }

    [Fact]
    public void Evaluate_SevereDrop_ReturnsConfirmed()
    {
        // FPS cayó de 120 a 100 (-16.7%) en 2 mediciones consecutivas → Confirmed
        var baseline = MakeMeasurement(120.0, onePercentLow: 90.0);
        var post1 = MakeMeasurement(100.0, onePercentLow: 70.0);
        var post2 = MakeMeasurement(100.0, onePercentLow: 70.0);

        var watch = new RegressionWatch(
            Guid.NewGuid(),
            "opt-fps-drop",
            "FPS Drop Watch",
            RestoreEntryId: null,
            baseline,
            new[] { post1, post2 });

        var result = _guard.Evaluate(watch);

        Assert.Equal(RegressionStatus.Confirmed, result.Status);
    }

    [Fact]
    public void Evaluate_StablePerformance_ReturnsHealthy()
    {
        // FPS estable o mejorado
        var baseline = MakeMeasurement(120.0, onePercentLow: 90.0);
        var post1 = MakeMeasurement(122.0, onePercentLow: 92.0);
        var post2 = MakeMeasurement(121.0, onePercentLow: 91.0);

        var watch = new RegressionWatch(
            Guid.NewGuid(),
            "opt-stable",
            "Stable Watch",
            RestoreEntryId: null,
            baseline,
            new[] { post1, post2 });

        var result = _guard.Evaluate(watch);

        Assert.Equal(RegressionStatus.Healthy, result.Status);
    }
}

public class CpuTopologyInfoTests
{
    [Fact]
    public void IntelAlderLakeI31220P_Topology_CalculatesProperAffinityMasks()
    {
        // 10 cores, 2 P-Cores (HT=4 threads, mask 0x00F), 8 E-Cores (mask 0xFF0), total 12 threads
        var cores = new List<EvoOptimizer.Core.Hardware.CoreDescription>
        {
            new(0, EvoOptimizer.Core.Hardware.CoreType.Performance, 0x3, 2, true),
            new(1, EvoOptimizer.Core.Hardware.CoreType.Performance, 0xC, 2, true),
            new(2, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x10, 1, false),
            new(3, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x20, 1, false),
            new(4, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x40, 1, false),
            new(5, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x80, 1, false),
            new(6, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x100, 1, false),
            new(7, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x200, 1, false),
            new(8, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x400, 1, false),
            new(9, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x800, 1, false),
        };

        var topology = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "12th Gen Intel(R) Core(TM) i3-1220P",
            TotalPhysicalCores: 10,
            LogicalProcessors: 12,
            PerformanceCores: 2,
            EfficientCores: 8,
            PerformanceCoresMask: 0x00F,
            EfficientCoresMask: 0xFF0,
            AllCoresMask: 0xFFF,
            IsHybrid: true,
            Cores: cores);

        Assert.True(topology.IsHybrid);
        Assert.Equal(10, topology.TotalPhysicalCores);
        Assert.Equal(12, topology.LogicalProcessors);
        Assert.Equal(2, topology.PerformanceCores);
        Assert.Equal(8, topology.EfficientCores);

        // Performance cores only mask (para CS2 1% low stability)
        ulong pCoresOnly = topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceCoresOnly);
        Assert.Equal(0x00FUL, pCoresOnly);

        // Performance + 2 E-Cores (0x00F | 0x010 | 0x020 = 0x03F)
        ulong balanced = topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceAndLightEfficiency);
        Assert.Equal(0x03FUL, balanced);

        // Exclude Core 0 mask: excluye la máscara completa de Core 0 (0x3 en P-Core con HyperThreading, resultando en 0xFFC)
        ulong excludeCore0 = topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.ExcludeCore0);
        Assert.Equal(0xFFCUL, excludeCore0);

        Assert.True(topology.IsTopologyReliable);
        Assert.Equal(4, topology.PerformanceLogicalProcessors);
        Assert.Equal(8, topology.EfficientLogicalProcessors);
    }

    [Fact]
    public void HeterogeneousCpu_WithArbitraryCoreLayout_CalculatesDynamicMaskCorrectly()
    {
        // Supongamos un procesador con 4 P-Cores intercalados o en orden arbitrario
        var cores = new List<EvoOptimizer.Core.Hardware.CoreDescription>
        {
            new(0, EvoOptimizer.Core.Hardware.CoreType.Performance, 0x0001, 1, false),
            new(1, EvoOptimizer.Core.Hardware.CoreType.Performance, 0x0002, 1, false),
            new(2, EvoOptimizer.Core.Hardware.CoreType.Efficient,   0x0004, 1, false),
            new(3, EvoOptimizer.Core.Hardware.CoreType.Efficient,   0x0008, 1, false),
            new(4, EvoOptimizer.Core.Hardware.CoreType.Performance, 0x0010, 1, false),
            new(5, EvoOptimizer.Core.Hardware.CoreType.Efficient,   0x0020, 1, false),
        };

        var topology = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Custom Heterogeneous CPU",
            TotalPhysicalCores: 6,
            LogicalProcessors: 6,
            PerformanceCores: 3,
            EfficientCores: 3,
            PerformanceCoresMask: 0x0013,
            EfficientCoresMask: 0x002C,
            AllCoresMask: 0x003F,
            IsHybrid: true,
            Cores: cores);

        Assert.True(topology.IsTopologyReliable);

        // P-cores dinámicos calculados directamente de los núcleos marcados como Performance
        ulong pMask = topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceCoresOnly);
        Assert.Equal(0x0013UL, pMask);

        // Performance + 2 primeros E-Cores (0x0013 | 0x0004 | 0x0008 = 0x001F)
        ulong balancedMask = topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceAndLightEfficiency);
        Assert.Equal(0x001FUL, balancedMask);
    }

    [Fact]
    public void IsTopologyReliable_ValidatesCorruptedOrIncompleteTopology()
    {
        // Caso 1: 0 núcleos físicos reportados
        var invalidCores0 = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Invalid CPU",
            TotalPhysicalCores: 0,
            LogicalProcessors: 0,
            PerformanceCores: 0,
            EfficientCores: 0,
            PerformanceCoresMask: 0,
            EfficientCoresMask: 0,
            AllCoresMask: 0,
            IsHybrid: false,
            Cores: Array.Empty<EvoOptimizer.Core.Hardware.CoreDescription>());

        Assert.False(invalidCores0.IsTopologyReliable);

        // Caso 2: Reportado híbrido pero sin P-Cores ni máscara
        var invalidHybrid = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Invalid Hybrid",
            TotalPhysicalCores: 4,
            LogicalProcessors: 4,
            PerformanceCores: 0,
            EfficientCores: 4,
            PerformanceCoresMask: 0,
            EfficientCoresMask: 0xF,
            AllCoresMask: 0xF,
            IsHybrid: true,
            Cores: new List<EvoOptimizer.Core.Hardware.CoreDescription>
            {
                new(0, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x1, 1, false),
                new(1, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x2, 1, false),
                new(2, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x4, 1, false),
                new(3, EvoOptimizer.Core.Hardware.CoreType.Efficient, 0x8, 1, false),
            });

        Assert.False(invalidHybrid.IsTopologyReliable);
    }

    [Fact]
    public void Default_StandardCpu_ProducesReliableNonHybridTopology()
    {
        var topology = EvoOptimizer.Core.Hardware.CpuTopologyInfo.Default(8, "Generic x64 CPU");

        Assert.False(topology.IsHybrid);
        Assert.True(topology.IsTopologyReliable);
        Assert.Equal(8, topology.TotalPhysicalCores);
        Assert.Equal(8, topology.LogicalProcessors);
        Assert.Equal(8, topology.PerformanceCores);
        Assert.Equal(0, topology.EfficientCores);
        Assert.Equal(0xFFUL, topology.AllCoresMask);
        Assert.Equal(8, topology.PerformanceLogicalProcessors);
        Assert.Equal(0, topology.EfficientLogicalProcessors);
        Assert.All(topology.Cores, c => Assert.Equal(EvoOptimizer.Core.Hardware.CoreType.Standard, c.Type));

        Assert.Equal(0xFFUL, topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.AllCores));
        Assert.Equal(0xFFUL, topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceCoresOnly));
        Assert.Equal(0xFFUL, topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceAndLightEfficiency));
        Assert.Equal(0xFEUL, topology.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.ExcludeCore0));
    }

    [Fact]
    public void Default_ClampsLogicalProcessorCountToMaskWidth()
    {
        var empty = EvoOptimizer.Core.Hardware.CpuTopologyInfo.Default(0);
        Assert.Equal(0UL, empty.AllCoresMask);
        Assert.False(empty.IsTopologyReliable);
        Assert.Equal(0UL, empty.GetAffinityMask(EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceCoresOnly));

        var max = EvoOptimizer.Core.Hardware.CpuTopologyInfo.Default(128);
        Assert.Equal(64, max.TotalPhysicalCores);
        Assert.Equal(ulong.MaxValue, max.AllCoresMask);
        Assert.True(max.IsTopologyReliable);
        Assert.Equal(64, max.Cores.Select(c => c.CoreIndex).Distinct().Count());
        Assert.Equal(64, max.Cores.Select(c => c.AffinityMask).Distinct().Count());
    }

    [Fact]
    public void IsTopologyReliable_RejectsInconsistentCoreMasks()
    {
        // Máscara de núcleo fuera de AllCoresMask.
        var outsideMask = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Outside Mask",
            TotalPhysicalCores: 2,
            LogicalProcessors: 2,
            PerformanceCores: 2,
            EfficientCores: 0,
            PerformanceCoresMask: 0x3,
            EfficientCoresMask: 0,
            AllCoresMask: 0x3,
            IsHybrid: false,
            Cores: new List<EvoOptimizer.Core.Hardware.CoreDescription>
            {
                new(0, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x1, 1, false),
                new(1, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x8, 1, false),
            });

        Assert.False(outsideMask.IsTopologyReliable);

        // Máscaras duplicadas entre dos núcleos.
        var duplicated = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Duplicated Mask",
            TotalPhysicalCores: 2,
            LogicalProcessors: 2,
            PerformanceCores: 2,
            EfficientCores: 0,
            PerformanceCoresMask: 0x1,
            EfficientCoresMask: 0,
            AllCoresMask: 0x1,
            IsHybrid: false,
            Cores: new List<EvoOptimizer.Core.Hardware.CoreDescription>
            {
                new(0, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x1, 1, false),
                new(1, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x1, 1, false),
            });

        Assert.False(duplicated.IsTopologyReliable);

        // Cantidad de hilos por núcleo incoherente con la máscara.
        var badThreadCount = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Bad Thread Count",
            TotalPhysicalCores: 1,
            LogicalProcessors: 2,
            PerformanceCores: 1,
            EfficientCores: 0,
            PerformanceCoresMask: 0x3,
            EfficientCoresMask: 0,
            AllCoresMask: 0x3,
            IsHybrid: false,
            Cores: new List<EvoOptimizer.Core.Hardware.CoreDescription>
            {
                new(0, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x3, 1, true),
            });

        Assert.False(badThreadCount.IsTopologyReliable);

        // Cantidad de núcleos declarada distinta de la lista real.
        var wrongCoreCount = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Wrong Core Count",
            TotalPhysicalCores: 4,
            LogicalProcessors: 2,
            PerformanceCores: 2,
            EfficientCores: 0,
            PerformanceCoresMask: 0x3,
            EfficientCoresMask: 0,
            AllCoresMask: 0x3,
            IsHybrid: false,
            Cores: new List<EvoOptimizer.Core.Hardware.CoreDescription>
            {
                new(0, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x1, 1, false),
                new(1, EvoOptimizer.Core.Hardware.CoreType.Standard, 0x2, 1, false),
            });

        Assert.False(wrongCoreCount.IsTopologyReliable);
    }

    [Fact]
    public void GetAffinityMask_AlwaysStaysWithinDetectedCores()
    {
        var topology = EvoOptimizer.Core.Hardware.CpuTopologyInfo.Default(6, "Six Thread CPU");

        foreach (EvoOptimizer.Core.Hardware.GamingAffinityMode mode in Enum.GetValues<EvoOptimizer.Core.Hardware.GamingAffinityMode>())
        {
            ulong mask = topology.GetAffinityMask(mode);
            Assert.NotEqual(0UL, mask);
            Assert.Equal(mask, mask & topology.AllCoresMask);
        }
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class Cs2AffinityOptimizerSafetyTests
{
    private readonly EvoOptimizer.Infrastructure.Optimization.Cs2AffinityOptimizer _optimizer = new();

    [Fact]
    public void Apply_WithUnreliableTopology_FailsSafelyWithoutThrowing()
    {
        var corruptedTopology = new EvoOptimizer.Core.Hardware.CpuTopologyInfo(
            ProcessorName: "Corrupted Topology",
            TotalPhysicalCores: 0,
            LogicalProcessors: 0,
            PerformanceCores: 0,
            EfficientCores: 0,
            PerformanceCoresMask: 0,
            EfficientCoresMask: 0,
            AllCoresMask: 0,
            IsHybrid: true,
            Cores: Array.Empty<EvoOptimizer.Core.Hardware.CoreDescription>());

        var result = _optimizer.Apply(corruptedTopology, EvoOptimizer.Core.Hardware.GamingAffinityMode.PerformanceCoresOnly);

        Assert.Equal(EvoOptimizer.Infrastructure.Optimization.AffinityOptimizationStatus.Failed, result.Status);
        Assert.Contains("no es confiable", result.Message);
        Assert.Null(result.Token);
    }
}

public class HardwareBottleneckAnalyzerTests
{
    private readonly EvoOptimizer.Core.Analysis.HardwareBottleneckAnalyzer _analyzer = new();

    private static EvoOptimizer.Core.Analysis.HardwarePerformanceSample MakeSample(
        double cpuUsage, double gpuUsage, double memUsage = 60.0, double availMb = 6000.0)
        => new(
            CpuUsagePercent: cpuUsage,
            GpuUsagePercent: gpuUsage,
            MemoryUsagePercent: memUsage,
            AvailableMemoryMb: availMb,
            DiskActivePercent: 5.0,
            DiskResponseTimeMs: 2.0,
            CpuTemperatureCelsius: 0,
            GpuTemperatureCelsius: 0,
            CpuFrequencyMhz: 0,
            GpuFrequencyMhz: 0,
            CpuPowerWatts: 0,
            GpuPowerWatts: 0,
            FrametimeMs: 12.0,
            GpuMemoryUsageMb: 800.0,
            TotalGpuMemoryMb: 2048.0);

    [Fact]
    public void Analyze_IntelUhdMaxedOut_DetectsGpuBottleneck()
    {
        // 10 muestras con GPU integrada al 98% y CPU moderada (40%)
        var samples = Enumerable.Range(0, 10)
            .Select(_ => MakeSample(cpuUsage: 40.0, gpuUsage: 98.0))
            .ToArray();

        var report = _analyzer.Analyze(samples);

        Assert.Equal(EvoOptimizer.Core.Analysis.BottleneckType.Gpu, report.PrimaryBottleneck);
        Assert.Contains(report.Findings, f => f.Type == EvoOptimizer.Core.Analysis.BottleneckType.Gpu);
    }

    [Fact]
    public void Analyze_CpuSaturated_DetectsCpuBottleneck()
    {
        // 10 muestras con CPU al 95% y GPU al 50%
        var samples = Enumerable.Range(0, 10)
            .Select(_ => MakeSample(cpuUsage: 95.0, gpuUsage: 50.0))
            .ToArray();

        var report = _analyzer.Analyze(samples);

        Assert.Equal(EvoOptimizer.Core.Analysis.BottleneckType.Cpu, report.PrimaryBottleneck);
        Assert.Contains(report.Findings, f => f.Type == EvoOptimizer.Core.Analysis.BottleneckType.Cpu);
    }

    [Fact]
    public void Analyze_MemoryPressure_DetectsMemoryBottleneck()
    {
        // 10 muestras con RAM al 95% y poca memoria libre
        var samples = Enumerable.Range(0, 10)
            .Select(_ => MakeSample(cpuUsage: 50.0, gpuUsage: 50.0, memUsage: 95.0, availMb: 500.0))
            .ToArray();

        var report = _analyzer.Analyze(samples);

        Assert.Contains(report.Findings, f => f.Type == EvoOptimizer.Core.Analysis.BottleneckType.Memory);
    }
}

public class Cs2ExperimentOrchestratorTests
{
    private readonly EvoOptimizer.Infrastructure.Benchmark.Cs2ExperimentOrchestrator _orchestrator = new();

    private static EvoOptimizer.Infrastructure.Benchmark.Cs2BenchmarkMetrics MakeMetrics(
        string name, double avgFps, double oneLow, double p99Ms, double gpuUsage = 70.0)
        => new(
            Timestamp: DateTimeOffset.UtcNow,
            ConfigurationName: name,
            AverageFps: avgFps,
            OnePercentLowFps: oneLow,
            PointOnePercentLowFps: oneLow * 0.75,
            P95FrametimeMs: p99Ms * 0.85,
            P99FrametimeMs: p99Ms,
            StutterRatePercent: 1.0,
            FrameCount: 3000,
            DurationSeconds: 40.0,
            SystemCpuPercent: 45.0,
            Cs2CpuPercent: 35.0,
            GpuUsagePercent: gpuUsage,
            GpuSharedMemoryMb: 900.0,
            MemoryUsagePercent: 65.0,
            CommitUsedMb: 8500.0);

    [Fact]
    public void Compare_ImprovementInOnePercentLow_ReturnsImprovementVerified()
    {
        var baseline = MakeMetrics("Config A (Stock)", avgFps: 65.0, oneLow: 40.0, p99Ms: 25.0);
        var candidate = MakeMetrics("Config B (P-Cores)", avgFps: 66.0, oneLow: 45.0, p99Ms: 22.0); // +12.5% en 1% low, -12% p99

        var comparison = _orchestrator.Compare(baseline, candidate);

        Assert.Equal(EvoOptimizer.Infrastructure.Benchmark.ExperimentVerdict.ImprovementVerified, comparison.Verdict);
        Assert.True(comparison.ShouldKeepOptimization);
        Assert.False(comparison.RequiresRollback);
    }

    [Fact]
    public void Compare_RegressionInOnePercentLow_ReturnsRegressionDetectedAndRequiresRollback()
    {
        var baseline = MakeMetrics("Config A (Stock)", avgFps: 65.0, oneLow: 40.0, p99Ms: 25.0);
        var candidate = MakeMetrics("Config B (Bad Affinity)", avgFps: 60.0, oneLow: 32.0, p99Ms: 31.0); // -20% en 1% low

        var comparison = _orchestrator.Compare(baseline, candidate);

        Assert.Equal(EvoOptimizer.Infrastructure.Benchmark.ExperimentVerdict.RegressionDetected, comparison.Verdict);
        Assert.False(comparison.ShouldKeepOptimization);
        Assert.True(comparison.RequiresRollback);
    }

    [Fact]
    public void Compare_SmallDifference_ReturnsInconclusive()
    {
        var baseline = MakeMetrics("Config A (Stock)", avgFps: 65.0, oneLow: 40.0, p99Ms: 25.0);
        var candidate = MakeMetrics("Config B (Noise)", avgFps: 65.2, oneLow: 40.2, p99Ms: 24.9);

        var comparison = _orchestrator.Compare(baseline, candidate);

        Assert.Equal(EvoOptimizer.Infrastructure.Benchmark.ExperimentVerdict.Inconclusive, comparison.Verdict);
        Assert.False(comparison.ShouldKeepOptimization);
    }

    [Fact]
    public void Compare_HighGpuUsage_ReportsGpuUsageObservation()
    {
        var baseline = MakeMetrics("Config A (Stock)", avgFps: 62.0, oneLow: 38.0, p99Ms: 26.0, gpuUsage: 97.0);
        var candidate = MakeMetrics("Config B (Priority)", avgFps: 62.5, oneLow: 39.0, p99Ms: 25.5, gpuUsage: 97.0);

        var comparison = _orchestrator.Compare(baseline, candidate);

        Assert.Equal(EvoOptimizer.Infrastructure.Benchmark.ExperimentVerdict.GpuBottleneckDominant, comparison.Verdict);
        Assert.Contains(comparison.EvidentiaryNotes, n => n.Contains("Uso de GPU"));
    }
}

public class WindowsOptimizationPlannerTests
{
    private static WindowsOptimizationSnapshot MakeSnapshot(
        bool gameMode = false,
        bool gameDvr = false,
        bool historicalCapture = false,
        string powerPlan = "Equilibrado",
        bool balanced = true,
        int startupCount = 2,
        bool? acPower = true,
        bool hasHighPerformancePlan = true) =>
        new(gameMode, gameDvr, historicalCapture, powerPlan, balanced, startupCount,
            IsOnAcPower: acPower,
            HasHighPerformancePlan: hasHighPerformancePlan);

    [Fact]
    public void Build_ReturnsSevenItemsInPriorityOrder()
    {
        var items = WindowsOptimizationPlanner.Build(MakeSnapshot());

        Assert.Equal(7, items.Count);
        Assert.Equal(WindowsOptimizationTarget.GameMode, items[0].Target);
        Assert.Equal(WindowsOptimizationTarget.GameDvr, items[1].Target);
        Assert.Equal(WindowsOptimizationTarget.PowerPlan, items[2].Target);
        Assert.Equal(WindowsOptimizationTarget.StartupApp, items[3].Target);
        Assert.Equal(WindowsOptimizationTarget.UwpBackground, items[4].Target);
        Assert.Equal(WindowsOptimizationTarget.EdgeBackground, items[5].Target);
        Assert.Equal(WindowsOptimizationTarget.WindowsSuggestions, items[6].Target);
    }

    [Fact]
    public void Build_UwpBackgroundDisabled_IsNotApplicable()
    {
        var snapshot = new WindowsOptimizationSnapshot(
            false, false, false, "Equilibrado", true, 0,
            UwpBackgroundDisabled: true);

        var item = WindowsOptimizationPlanner
            .Build(snapshot)
            .Single(x => x.Target == WindowsOptimizationTarget.UwpBackground);

        Assert.False(item.CanApply);
        Assert.Equal(WindowsOptimizationRisk.Low, item.RiskLevel);
        Assert.NotNull(item.BlockedReason);
    }

    [Fact]
    public void Build_EdgeBackgroundEnabled_ProposesDisable()
    {
        var snapshot = new WindowsOptimizationSnapshot(
            false, false, false, "Equilibrado", true, 0,
            UwpBackgroundDisabled: false,
            EdgeBackgroundDisabled: false,
            WindowsSuggestionsDisabled: false);

        var item = WindowsOptimizationPlanner
            .Build(snapshot)
            .Single(x => x.Target == WindowsOptimizationTarget.EdgeBackground);

        Assert.True(item.CanApply);
        Assert.Contains("BackgroundModeEnabled", item.ProposedState);
    }

    [Fact]
    public void Build_SuggestionsEnabled_ProposesDisable()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot())
            .Single(x => x.Target == WindowsOptimizationTarget.WindowsSuggestions);

        Assert.True(item.CanApply);
        Assert.Contains("sugerencias", item.ProposedState,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_GameModeDisabled_ProposesEnableAndCanApply()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(gameMode: false))
            .Single(x => x.Target == WindowsOptimizationTarget.GameMode);

        Assert.True(item.CanApply);
        Assert.Equal(WindowsOptimizationRisk.Low, item.RiskLevel);
        Assert.Contains("AllowAutoGameMode", item.ProposedState);
        Assert.Equal("Riesgo bajo", item.RiskLabel);
        Assert.Null(item.BlockedReason);
    }

    [Fact]
    public void Build_GameModeEnabled_IsNotApplicable()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(gameMode: true))
            .Single(x => x.Target == WindowsOptimizationTarget.GameMode);

        Assert.False(item.CanApply);
        Assert.NotNull(item.BlockedReason);
        Assert.Equal("Activado", item.CurrentState);
    }

    [Fact]
    public void Build_GameDvrEnabled_ProposesDisableBothValues()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(gameDvr: true))
            .Single(x => x.Target == WindowsOptimizationTarget.GameDvr);

        Assert.True(item.CanApply);
        Assert.Contains("AppCaptureEnabled", item.ProposedState);
        Assert.Contains("HistoricalCaptureEnabled", item.ProposedState);
        Assert.Equal(WindowsOptimizationRisk.Low, item.RiskLevel);
    }

    [Fact]
    public void Build_GameDvrDisabled_IsNotApplicable()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(gameDvr: false, historicalCapture: false))
            .Single(x => x.Target == WindowsOptimizationTarget.GameDvr);

        Assert.False(item.CanApply);
        Assert.Contains("ya está desactivada", item.BlockedReason);
    }

    [Fact]
    public void Build_BalancedPlan_ProposesHighPerformanceWithMediumRisk()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(powerPlan: "Equilibrado", balanced: true,
                acPower: true, hasHighPerformancePlan: true))
            .Single(x => x.Target == WindowsOptimizationTarget.PowerPlan);

        Assert.True(item.CanApply);
        Assert.Equal(WindowsOptimizationRisk.Medium, item.RiskLevel);
        Assert.Contains("Alto rendimiento", item.ProposedState);
        Assert.Equal("Riesgo medio", item.RiskLabel);
    }

    [Fact]
    public void Build_BalancedPlanOnBattery_IsBlockedForAcSafety()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(powerPlan: "Equilibrado", balanced: true,
                acPower: false, hasHighPerformancePlan: true))
            .Single(x => x.Target == WindowsOptimizationTarget.PowerPlan);

        Assert.False(item.CanApply);
        Assert.NotNull(item.BlockedReason);
        Assert.Contains("AC", item.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_BalancedPlanWithUnknownPowerSource_IsBlocked()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(powerPlan: "Equilibrado", balanced: true,
                acPower: null, hasHighPerformancePlan: true))
            .Single(x => x.Target == WindowsOptimizationTarget.PowerPlan);

        Assert.False(item.CanApply);
        Assert.NotNull(item.BlockedReason);
    }

    [Fact]
    public void Build_BalancedPlanWithoutHighPerformancePlan_IsBlocked()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(powerPlan: "Equilibrado", balanced: true,
                acPower: true, hasHighPerformancePlan: false))
            .Single(x => x.Target == WindowsOptimizationTarget.PowerPlan);

        Assert.False(item.CanApply);
        Assert.NotNull(item.BlockedReason);
        Assert.Contains("Alto rendimiento", item.BlockedReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_NonBalancedPlan_IsNotApplicable()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(powerPlan: "Alto rendimiento", balanced: false))
            .Single(x => x.Target == WindowsOptimizationTarget.PowerPlan);

        Assert.False(item.CanApply);
        Assert.NotNull(item.BlockedReason);
    }

    [Fact]
    public void Build_StartupItems_AreHandledIndividually()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(startupCount: 3))
            .Single(x => x.Target == WindowsOptimizationTarget.StartupApp);

        Assert.False(item.CanApply);
        Assert.Contains("3 aplicaciones", item.CurrentState);
        Assert.Contains("una por vez", item.ProposedState);
    }

    [Fact]
    public void Build_NoStartupItems_ReportsEmptyState()
    {
        var item = WindowsOptimizationPlanner
            .Build(MakeSnapshot(startupCount: 0))
            .Single(x => x.Target == WindowsOptimizationTarget.StartupApp);

        Assert.False(item.CanApply);
        Assert.Contains("Sin aplicaciones", item.CurrentState);
    }
}

public class WindowsChangeJournalTests
{
    private static WindowsOptimizationChange MakeChange(
        WindowsOptimizationTarget target,
        WindowsOptimizationStatus status,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            target,
            $"{target}:setting",
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
            status,
            DateTimeOffset.UtcNow,
            status is WindowsOptimizationStatus.Verified
                ? DateTimeOffset.UtcNow
                : null,
            null);

    private static string TempJournalPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"evo-windows-changes-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task AppendAndGetAll_PersistsAcrossInstances()
    {
        string path = TempJournalPath();

        try
        {
            var change = MakeChange(
                WindowsOptimizationTarget.GameMode,
                WindowsOptimizationStatus.Verified);

            var first = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);
            await first.AppendAsync(change);

            var second = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);
            var entries = await second.GetAllAsync();

            var stored = Assert.Single(entries);
            Assert.Equal(change.Id, stored.Id);
            Assert.Equal(WindowsOptimizationTarget.GameMode, stored.Target);
            Assert.Equal(WindowsOptimizationStatus.Verified, stored.Status);
            Assert.Single(stored.OriginalOperations);
            Assert.Equal("0", stored.OriginalOperations[0].Value);
            Assert.Equal("1", stored.AppliedOperations[0].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Journal_SerializesEnumsAsNames()
    {
        string path = TempJournalPath();

        try
        {
            var journal = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.PowerPlan,
                WindowsOptimizationStatus.Applied));

            string raw = await File.ReadAllTextAsync(path);

            Assert.Contains("\"PowerPlan\"", raw);
            Assert.Contains("\"Applied\"", raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateAsync_ReplacesExistingEntry()
    {
        string path = TempJournalPath();

        try
        {
            var journal = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);
            var change = MakeChange(
                WindowsOptimizationTarget.GameDvr,
                WindowsOptimizationStatus.Applied);

            await journal.AppendAsync(change);
            await journal.UpdateAsync(change with
            {
                Status = WindowsOptimizationStatus.RolledBack
            });

            var stored = Assert.Single(await journal.GetAllAsync());
            Assert.Equal(WindowsOptimizationStatus.RolledBack, stored.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_Throws()
    {
        string path = TempJournalPath();

        try
        {
            var journal = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => journal.UpdateAsync(MakeChange(
                    WindowsOptimizationTarget.GameMode,
                    WindowsOptimizationStatus.Applied)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsPendingAppliedOrVerified()
    {
        string path = TempJournalPath();

        try
        {
            var journal = new EvoOptimizer.Infrastructure.Optimization.WindowsChangeJournal(path);

            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.GameMode,
                WindowsOptimizationStatus.Verified));
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.GameDvr,
                WindowsOptimizationStatus.Applied));
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.PowerPlan,
                WindowsOptimizationStatus.RolledBack));
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.StartupApp,
                WindowsOptimizationStatus.Failed));
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.GameMode,
                WindowsOptimizationStatus.Pending));
            await journal.AppendAsync(MakeChange(
                WindowsOptimizationTarget.GameDvr,
                WindowsOptimizationStatus.RollbackFailed));

            var active = await journal.GetActiveAsync();

            Assert.Equal(4, active.Count);
            Assert.All(active, x => Assert.True(
                x.Status is WindowsOptimizationStatus.Pending
                    or WindowsOptimizationStatus.Applied
                    or WindowsOptimizationStatus.Verified
                    or WindowsOptimizationStatus.RollbackFailed));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Regla global: ninguna modificación se conserva sin evidencia antes/después.
/// </summary>
public class OptimizationEvidenceEvaluatorTests
{
    private readonly OptimizationEvidenceEvaluator _evaluator = new();

    [Fact]
    public void StartupBootSessionGuard_RequiresDifferentBootSession()
    {
        string baseline = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero)
            .ToString("O");
        string sameSession = new DateTimeOffset(2026, 11, 7, 10, 0, 2, TimeSpan.Zero)
            .ToString("O");
        string afterReboot = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero)
            .ToString("O");

        Assert.False(StartupBootSessionGuard.HasRebooted(baseline, sameSession));
        Assert.True(StartupBootSessionGuard.HasRebooted(baseline, afterReboot));
        Assert.False(StartupBootSessionGuard.HasRebooted(null, afterReboot));
        Assert.Equal(
            "NO VERIFICABLE",
            new OptimizationEvidenceResult(
                Guid.NewGuid(),
                "startup:example",
                "Example",
                OptimizationEvidenceVerdict.NotVerifiable,
                false,
                false,
                false,
                Array.Empty<OptimizationMetricComparison>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                DateTimeOffset.UtcNow).VerdictLabel);
    }

    [Fact]
    public void Evaluate_ImprovementWithoutRegressions_KeepsChange()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2));

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(110, 88, 66, 11, 15, 1.5));

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.ImprovementVerified,
            result.Verdict);
        Assert.True(result.ShouldKeep);
        Assert.False(result.ShouldRollback);
        Assert.False(result.RequiresUserDecision);
        Assert.Contains(result.Metrics, x => x.IsPrimary && x.IsImprovement);
    }

    [Fact]
    public void Evaluate_PrimaryRegression_RequiresRollback()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2));

        // Los FPS promedio suben, pero el 1% Low cae 12.5%: regresión primaria.
        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(110, 70, 60, 12, 16, 2));

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.RegressionDetected,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.True(result.ShouldRollback);
    }

    [Fact]
    public void Evaluate_FpsImprovesButTemperatureWorsens_IsTradeOff()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2) with
            {
                CpuTemperatureCelsius = 70
            });

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(110, 88, 66, 11, 15, 1.5) with
            {
                CpuTemperatureCelsius = 78
            });

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(OptimizationEvidenceVerdict.TradeOff, result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.True(result.ShouldRollback);
        Assert.True(result.RequiresUserDecision);
        Assert.Contains(result.Metrics, x =>
            x.Name.Contains("Temperatura") && x.IsRegression);
    }

    [Fact]
    public void Evaluate_MissingFpsEvidence_IsInconclusiveAndNotKept()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            new OptimizationMetricValues(
                CpuUsagePercent: 40,
                MemoryUsagePercent: 50,
                CommitUsedMb: 8000,
                ActiveProcessCount: 200,
                SampleCount: 30,
                DurationSeconds: 30));

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            new OptimizationMetricValues(
                CpuUsagePercent: 30,
                MemoryUsagePercent: 45,
                CommitUsedMb: 7000,
                ActiveProcessCount: 190,
                SampleCount: 30,
                DurationSeconds: 30));

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(OptimizationEvidenceVerdict.Inconclusive, result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.False(result.ShouldRollback);
        Assert.True(result.RequiresUserDecision);
        Assert.Contains(result.Warnings, x =>
            x.Contains("FPS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ProcessKind_UsesSystemMetricsAsPrimary()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Process,
            new OptimizationMetricValues(
                CpuUsagePercent: 40,
                MemoryUsagePercent: 60,
                CommitUsedMb: 10000,
                ActiveProcessCount: 250,
                SampleCount: 30,
                DurationSeconds: 30));

        var candidate = Measurement(
            OptimizationModificationKind.Process,
            new OptimizationMetricValues(
                CpuUsagePercent: 30,
                MemoryUsagePercent: 50,
                CommitUsedMb: 8000,
                ActiveProcessCount: 230,
                SampleCount: 30,
                DurationSeconds: 30));

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.ImprovementVerified,
            result.Verdict);
        Assert.True(result.ShouldKeep);
        Assert.Contains(result.Metrics, x =>
            x.Name == "CPU" && x.IsPrimary && x.IsImprovement);
        Assert.Contains(result.Metrics, x =>
            x.Name == "Procesos activos" && x.IsPrimary && x.IsImprovement);
    }

    [Fact]
    public void Evaluate_DifferentContext_IsInsufficientData()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2),
            scenario: "WindowsOptimization");

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(120, 95, 75, 10, 13, 1),
            scenario: "OtroEscenario");

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.InsufficientData,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.True(result.RequiresUserDecision);
    }

    [Fact]
    public void Evaluate_DifferentOptimization_IsInsufficientData()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2));
        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(120, 95, 75, 10, 13, 1),
            modificationKey: "windows:GameDvr");

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.InsufficientData,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.False(result.ShouldRollback);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("misma optimización", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_CandidateCapturedBeforeBaseline_IsInsufficientData()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2));
        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(120, 95, 75, 10, 13, 1)) with
        {
            CapturedAt = baseline.CapturedAt.AddSeconds(-1)
        };

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.InsufficientData,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.False(result.ShouldRollback);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("después de la línea base", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_TooFewSamples_IsInsufficientData()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2, samples: 3, duration: 3));

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(120, 95, 75, 10, 13, 1, samples: 3, duration: 3));

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.Equal(
            OptimizationEvidenceVerdict.InsufficientData,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.True(result.RequiresUserDecision);
    }

    [Fact]
    public void Evaluate_GpuUsageChangeAlone_DoesNotVerifyImprovement()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2) with
            {
                GpuUsagePercent = 50
            });

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2) with
            {
                GpuUsagePercent = 80
            });

        var result = _evaluator.Evaluate(baseline, candidate);

        Assert.NotEqual(
            OptimizationEvidenceVerdict.ImprovementVerified,
            result.Verdict);
        Assert.False(result.ShouldKeep);
        Assert.Contains(result.Metrics, x =>
            x.Name == "GPU" && x.IsObservational);
    }

    [Fact]
    public void Evidence_RoundTripsThroughJson()
    {
        var baseline = Measurement(
            OptimizationModificationKind.Windows,
            Fps(100, 80, 60, 12, 16, 2));

        var candidate = Measurement(
            OptimizationModificationKind.Windows,
            Fps(110, 88, 66, 11, 15, 1.5));

        var result = _evaluator.Evaluate(baseline, candidate);

        string json = JsonSerializer.Serialize(result);
        var restored =
            JsonSerializer.Deserialize<OptimizationEvidenceResult>(json);

        Assert.NotNull(restored);
        Assert.Equal(result.Verdict, restored!.Verdict);
        Assert.Equal(result.ModificationKey, restored.ModificationKey);
        Assert.Equal(result.Metrics.Count, restored.Metrics.Count);
    }

    private static OptimizationMeasurement Measurement(
        OptimizationModificationKind kind,
        OptimizationMetricValues metrics,
        string scenario = "WindowsOptimization",
        string hardwareId = "hw-test",
        string environmentId = "env-test",
        string modificationKey = "windows:GameMode")
    {
        var context = new OptimizationMeasurementContext(
            Guid.Parse("a86e724e-9dcf-4f3d-af44-3d8e6383818c"),
            modificationKey,
            "Modo Juego",
            kind,
            "cs2",
            scenario,
            "unknown",
            "PresentMon+LivePerformanceMonitor",
            hardwareId,
            environmentId);

        return new OptimizationMeasurement(
            Guid.NewGuid(),
            context,
            DateTimeOffset.UtcNow,
            metrics,
            Array.Empty<string>());
    }

    private static OptimizationMetricValues Fps(
        double average,
        double onePercentLow,
        double pointOnePercentLow,
        double p95,
        double p99,
        double stutter,
        int samples = 30,
        double duration = 30,
        int frames = 2000) =>
        new(
            AverageFps: average,
            OnePercentLowFps: onePercentLow,
            PointOnePercentLowFps: pointOnePercentLow,
            P95FrametimeMs: p95,
            P99FrametimeMs: p99,
            StutterRatePercent: stutter,
            CpuUsagePercent: 40,
            GpuUsagePercent: 60,
            MemoryUsagePercent: 50,
            CommitUsedMb: 8000,
            ActiveProcessCount: 200,
            FrameCount: frames,
            SampleCount: samples,
            DurationSeconds: duration);
}
