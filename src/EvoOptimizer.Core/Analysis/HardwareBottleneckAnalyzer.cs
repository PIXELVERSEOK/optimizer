namespace EvoOptimizer.Core.Analysis;

public enum BottleneckType
{
    Cpu,
    Gpu,
    Memory,
    Storage,
    Thermal,
    Power,
    Unknown
}

public enum BottleneckSeverity
{
    None,
    Low,
    Moderate,
    High,
    Critical
}

public sealed record HardwarePerformanceSample(
    double CpuUsagePercent,
    double GpuUsagePercent,
    double MemoryUsagePercent,
    double AvailableMemoryMb,
    double DiskActivePercent,
    double DiskResponseTimeMs,
    double CpuTemperatureCelsius,
    double GpuTemperatureCelsius,
    double CpuFrequencyMhz,
    double GpuFrequencyMhz,
    double CpuPowerWatts,
    double GpuPowerWatts,
    double FrametimeMs,
    double GpuMemoryUsageMb,
    double TotalGpuMemoryMb);

public sealed record BottleneckFinding(
    BottleneckType Type,
    BottleneckSeverity Severity,
    double ConfidencePercent,
    string Title,
    string Explanation,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RecommendedInvestigations);

public sealed record HardwareBottleneckReport(
    DateTimeOffset AnalyzedAt,
    int SamplesAnalyzed,
    BottleneckSeverity OverallSeverity,
    BottleneckType? PrimaryBottleneck,
    IReadOnlyList<BottleneckFinding> Findings,
    IReadOnlyList<string> Limitations);

public sealed class HardwareBottleneckAnalyzer
{
    public HardwareBottleneckReport Analyze(
        IEnumerable<HardwarePerformanceSample> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var samples = input
            .Where(IsValid)
            .ToArray();

        if (samples.Length < 5)
        {
            return new HardwareBottleneckReport(
                DateTimeOffset.UtcNow,
                samples.Length,
                BottleneckSeverity.None,
                null,
                Array.Empty<BottleneckFinding>(),
                new[]
                {
                    "Se necesitan al menos cinco muestras válidas.",
                    "Sin datos temporales suficientes no se puede determinar un cuello de botella."
                });
        }

        var findings = new List<BottleneckFinding>();

        AnalyzeCpu(samples, findings);
        AnalyzeGpu(samples, findings);
        AnalyzeMemory(samples, findings);
        AnalyzeStorage(samples, findings);
        AnalyzeThermal(samples, findings);
        AnalyzePower(samples, findings);

        var ordered = findings
            .OrderByDescending(x => x.Severity)
            .ThenByDescending(x => x.ConfidencePercent)
            .ToArray();

        var primary = ordered.FirstOrDefault();

        var limitations = new List<string>
        {
            "La utilización elevada no demuestra por sí sola que un componente sea el cuello de botella.",
            "Se necesita correlación temporal con frametimes para aumentar la confianza.",
            "Las temperaturas y frecuencias requieren sensores fiables.",
            "La memoria GPU compartida de una iGPU no equivale a VRAM dedicada."
        };

        if (samples.All(x => x.FrametimeMs <= 0))
        {
            limitations.Add(
                "No hay frametimes válidos: no se puede vincular la carga del hardware con caídas de FPS.");
        }

        return new HardwareBottleneckReport(
            DateTimeOffset.UtcNow,
            samples.Length,
            primary?.Severity ?? BottleneckSeverity.None,
            primary?.Type,
            ordered,
            limitations);
    }

    private static bool IsValid(HardwarePerformanceSample x)
    {
        return ValidPercent(x.CpuUsagePercent) &&
               ValidPercent(x.GpuUsagePercent) &&
               ValidPercent(x.MemoryUsagePercent) &&
               ValidPercent(x.DiskActivePercent) &&
               ValidNonNegative(x.AvailableMemoryMb) &&
               ValidNonNegative(x.DiskResponseTimeMs) &&
               ValidOptional(x.CpuTemperatureCelsius) &&
               ValidOptional(x.GpuTemperatureCelsius) &&
               ValidNonNegative(x.CpuFrequencyMhz) &&
               ValidNonNegative(x.GpuFrequencyMhz) &&
               ValidNonNegative(x.CpuPowerWatts) &&
               ValidNonNegative(x.GpuPowerWatts) &&
               ValidNonNegative(x.FrametimeMs) &&
               ValidNonNegative(x.GpuMemoryUsageMb) &&
               ValidNonNegative(x.TotalGpuMemoryMb);
    }

    private static bool ValidPercent(double x) =>
        double.IsFinite(x) && x is >= 0 and <= 100;

    private static bool ValidNonNegative(double x) =>
        double.IsFinite(x) && x >= 0;

    private static bool ValidOptional(double x) =>
        double.IsFinite(x) && (x == 0 || x is >= 0 and <= 150);

    private static double Average(
        HardwarePerformanceSample[] samples,
        Func<HardwarePerformanceSample, double> selector) =>
        samples.Average(selector);

    private static double Ratio(
        HardwarePerformanceSample[] samples,
        Func<HardwarePerformanceSample, bool> predicate) =>
        samples.Count(predicate) * 100d / samples.Length;

    private static void AnalyzeCpu(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var average = Average(samples, x => x.CpuUsagePercent);
        var highRatio = Ratio(samples, x => x.CpuUsagePercent >= 90);
        var lowGpuRatio = Ratio(samples, x =>
            x.CpuUsagePercent >= 90 && x.GpuUsagePercent < 75);

        if (highRatio < 40)
            return;

        var evidence = new List<string>
        {
            $"CPU media: {average:F1}%.",
            $"Muestras con CPU >= 90%: {highRatio:F1}%."
        };

        if (lowGpuRatio >= 30)
        {
            evidence.Add(
                $"Muestras con CPU alta y GPU por debajo de 75%: {lowGpuRatio:F1}%.");
        }

        findings.Add(new BottleneckFinding(
            BottleneckType.Cpu,
            highRatio >= 80
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(90, 50 + highRatio / 4),
            "Posible presión de CPU",
            "La CPU mantiene una utilización elevada durante una parte importante de la captura.",
            evidence,
            new[]
            {
                "Identificar procesos que consumen CPU.",
                "Examinar carga por procesador lógico.",
                "Comprobar si los picos coinciden con frametimes altos.",
                "Revisar temperaturas y frecuencia sostenida."
            }));
    }

    private static void AnalyzeGpu(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var average = Average(samples, x => x.GpuUsagePercent);
        var highRatio = Ratio(samples, x => x.GpuUsagePercent >= 95);

        if (highRatio < 40)
            return;

        findings.Add(new BottleneckFinding(
            BottleneckType.Gpu,
            highRatio >= 80
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(90, 50 + highRatio / 4),
            "Posible límite de GPU",
            "La GPU mantiene una utilización cercana a su capacidad máxima.",
            new[]
            {
                $"GPU media: {average:F1}%.",
                $"Muestras con GPU >= 95%: {highRatio:F1}%."
            },
            new[]
            {
                "Comparar resolución y carga gráfica.",
                "Revisar frecuencia y temperatura.",
                "Comparar FPS y frametimes con otra resolución.",
                "Distinguir límite de GPU de límite de memoria compartida."
            }));
    }

    private static void AnalyzeMemory(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var average = Average(samples, x => x.MemoryUsagePercent);
        var lowAvailable = Ratio(samples, x => x.AvailableMemoryMb <= 1024);
        var highUsage = Ratio(samples, x => x.MemoryUsagePercent >= 90);

        if (lowAvailable < 30 && highUsage < 40)
            return;

        findings.Add(new BottleneckFinding(
            BottleneckType.Memory,
            lowAvailable >= 70 || highUsage >= 80
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(90, 50 + Math.Max(lowAvailable, highUsage) / 4),
            "Posible presión de memoria RAM",
            "La memoria disponible o el porcentaje de utilización indican poco margen para nuevas cargas.",
            new[]
            {
                $"Uso medio de RAM: {average:F1}%.",
                $"Muestras con <= 1 GB disponible: {lowAvailable:F1}%.",
                $"Muestras con >= 90% de uso: {highUsage:F1}%."
            },
            new[]
            {
                "Identificar procesos con mayor consumo de memoria.",
                "Examinar memoria comprometida y paginación.",
                "Comprobar memoria compartida utilizada por la iGPU.",
                "No cerrar procesos automáticamente."
            }));
    }

    private static void AnalyzeStorage(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var active = Ratio(samples, x => x.DiskActivePercent >= 90);
        var latency = Ratio(samples, x => x.DiskResponseTimeMs >= 50);

        if (active < 40 && latency < 30)
            return;

        findings.Add(new BottleneckFinding(
            BottleneckType.Storage,
            active >= 80 || latency >= 70
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(85, 50 + Math.Max(active, latency) / 4),
            "Posible presión de almacenamiento",
            "Se observan períodos de actividad o latencia elevada en el disco.",
            new[]
            {
                $"Muestras con actividad >= 90%: {active:F1}%.",
                $"Muestras con respuesta >= 50 ms: {latency:F1}%."
            },
            new[]
            {
                "Identificar procesos con actividad de disco.",
                "Revisar latencia de lectura y escritura.",
                "Correlacionar las operaciones con los stutters.",
                "No desactivar servicios de almacenamiento."
            }));
    }

    private static void AnalyzeThermal(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var cpuHot = Ratio(samples, x =>
            x.CpuTemperatureCelsius >= 90);

        var gpuHot = Ratio(samples, x =>
            x.GpuTemperatureCelsius >= 90);

        if (cpuHot < 20 && gpuHot < 20)
            return;

        findings.Add(new BottleneckFinding(
            BottleneckType.Thermal,
            cpuHot >= 70 || gpuHot >= 70
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(90, 55 + Math.Max(cpuHot, gpuHot) / 4),
            "Posible presión térmica",
            "Se registraron temperaturas elevadas. Para confirmar throttling se necesitan datos de frecuencia y límites térmicos del fabricante.",
            new[]
            {
                $"Muestras con CPU >= 90 °C: {cpuHot:F1}%.",
                $"Muestras con GPU >= 90 °C: {gpuHot:F1}%."
            },
            new[]
            {
                "Comprobar si cae la frecuencia durante los picos térmicos.",
                "Revisar ventilación y refrigeración.",
                "Comparar temperatura con límites oficiales del hardware."
            }));
    }

    private static void AnalyzePower(
        HardwarePerformanceSample[] samples,
        List<BottleneckFinding> findings)
    {
        var lowCpuFrequency = Ratio(samples, x =>
            x.CpuUsagePercent >= 85 &&
            x.CpuFrequencyMhz > 0 &&
            x.CpuFrequencyMhz < 1000);

        if (lowCpuFrequency < 40)
            return;

        findings.Add(new BottleneckFinding(
            BottleneckType.Power,
            lowCpuFrequency >= 80
                ? BottleneckSeverity.High
                : BottleneckSeverity.Moderate,
            Math.Min(80, 45 + lowCpuFrequency / 3),
            "Posible limitación de frecuencia o potencia",
            "La CPU aparece muy cargada mientras registra frecuencias inferiores a 1 GHz. Se necesita identificar si la causa es térmica, energética o de medición.",
            new[]
            {
                $"Muestras con CPU >= 85% y frecuencia < 1000 MHz: {lowCpuFrequency:F1}%."
            },
            new[]
            {
                "Comprobar frecuencia efectiva por núcleo.",
                "Revisar límites de potencia configurados por el fabricante.",
                "Correlacionar temperatura, alimentación y frecuencia.",
                "No modificar PL1, PL2 ni voltajes automáticamente."
            }));
    }
}
