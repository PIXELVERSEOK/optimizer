using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using EvoOptimizer.App.Mvvm;
using EvoOptimizer.Core.Decisions;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Scoring;
using EvoOptimizer.Core.Telemetry;

namespace EvoOptimizer.App.ViewModels;

public sealed record GoalOption(OptimizationGoal Goal, string Label, string Description);
public sealed record SensorStatus(string Name, string Source, bool IsActive);
public sealed record ScoreDimensionView(string Name, string ValueText, string Basis);

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 120;

    private readonly ITelemetryProvider _telemetry;
    private readonly IDecisionEngine? _decisionEngine;
    private readonly DispatcherTimer _timer;
    private readonly List<TelemetrySnapshot> _window = new(HistoryLength);
    private readonly List<double> _cpuSeries = new(HistoryLength);
    private readonly List<double> _memSeries = new(HistoryLength);
    private readonly RunningStats _cpuStats = new();
    private readonly RunningStats _memStats = new();
    private readonly DateTimeOffset _sessionStart = DateTimeOffset.Now;
    private int _ticks;

    private GoalOption _selectedGoal;
    private string _cpuText = "N/D";
    private string _memoryText = "N/D";
    private string _memoryDetail = "No disponible";
    private string _commitDetail = "Commit: no disponible";
    private double[] _cpuHistory = Array.Empty<double>();
    private double[] _memoryHistory = Array.Empty<double>();
    private string _cpuStatsText = "Sin muestras";
    private string _memStatsText = "Sin muestras";
    private string _sessionText = "";
    private string _statusText = "Iniciando telemetría…";

    public DashboardViewModel(ITelemetryProvider telemetry, HardwareProfile hardware, IDecisionEngine? decisionEngine = null)
    {
        _telemetry = telemetry;
        _decisionEngine = decisionEngine;
        Hardware = hardware;

        Goals = new[]
        {
            new GoalOption(OptimizationGoal.Everyday, "Uso cotidiano",
                "Prioriza estabilidad, respuesta del sistema y bajo consumo. Sin ajustes agresivos."),
            new GoalOption(OptimizationGoal.Gaming, "Gaming",
                "Prioriza estabilidad de frametime (1% lows) y latencia. Validarlo requiere captura de frametime (aún no disponible)."),
            new GoalOption(OptimizationGoal.MaximumPerformance, "Máximo rendimiento",
                "Prioriza rendimiento sostenido admitiendo más consumo y temperatura, siempre dentro de límites térmicos seguros.")
        };
        _selectedGoal = Goals[0];

        GpuLines = hardware.Gpus.Count == 0
            ? new[] { "No detectada" }
            : hardware.Gpus.Select(g => g.DedicatedMemoryBytes is { } bytes
                ? $"{g.Name} · {FormatGb(bytes)} (reportado por driver)"
                : g.Name).ToArray();

        RamText = hardware.TotalMemoryBytes is { } ram ? FormatGb(ram) : "No disponible";
        CpuNominalText = hardware.CpuNominalMhz is { } mhz
            ? $"{mhz / 1000.0:F2} GHz nominal"
            : "No disponible";

        Sensors = new[]
        {
            new SensorStatus("Utilización global de CPU", "GetSystemTimes (kernel32)", true),
            new SensorStatus("Memoria física y commit", "GlobalMemoryStatusEx", true),
            new SensorStatus("Perfil de hardware", "Registro de Windows", true),
            new SensorStatus("Utilización por proceso", "Pendiente", false),
            new SensorStatus("GPU (uso, VRAM)", "Pendiente", false),
            new SensorStatus("Temperaturas / throttling", "Pendiente (requiere driver de sensores)", false),
            new SensorStatus("Disco (latencia, cola)", "Pendiente", false),
            new SensorStatus("Frametime", "Pendiente (ETW)", false),
        };

        Score = new[]
        {
            Unmeasured(ScoreDimension.Stability, "Estabilidad", "Requiere captura de frametime (1% lows, varianza)."),
            Unmeasured(ScoreDimension.Performance, "Rendimiento", "Requiere FPS o un benchmark reproducible."),
            Unmeasured(ScoreDimension.Efficiency, "Eficiencia", "Requiere consumo y utilización por componente."),
            Unmeasured(ScoreDimension.Health, "Salud", "Requiere temperaturas, throttling y registro de errores."),
        };

        _telemetry.Sample(); // cebar el delta de CPU
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public HardwareProfile Hardware { get; }
    public string RamText { get; }
    public string CpuNominalText { get; }
    public IReadOnlyList<string> GpuLines { get; }
    public IReadOnlyList<GoalOption> Goals { get; }
    public IReadOnlyList<SensorStatus> Sensors { get; }
    public IReadOnlyList<ScoreDimensionView> Score { get; }
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<Recommendation> Recommendations { get; } = new();

    public GoalOption SelectedGoal
    {
        get => _selectedGoal;
        set { if (value is not null && Set(ref _selectedGoal, value)) Raise(nameof(SelectedGoalDescription)); }
    }

    public string SelectedGoalDescription => _selectedGoal.Description;
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string MemoryDetail { get => _memoryDetail; private set => Set(ref _memoryDetail, value); }
    public string CommitDetail { get => _commitDetail; private set => Set(ref _commitDetail, value); }
    public double[] CpuHistory { get => _cpuHistory; private set => Set(ref _cpuHistory, value); }
    public double[] MemoryHistory { get => _memoryHistory; private set => Set(ref _memoryHistory, value); }
    public string CpuStatsText { get => _cpuStatsText; private set => Set(ref _cpuStatsText, value); }
    public string MemStatsText { get => _memStatsText; private set => Set(ref _memStatsText, value); }
    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        var sample = _telemetry.Sample();

        _window.Add(sample);
        if (_window.Count > HistoryLength) _window.RemoveAt(0);

        if (sample.CpuUtilizationPercent is { } cpu)
        {
            Push(_cpuSeries, cpu);
            _cpuStats.Add(cpu);
            CpuText = $"{cpu:F0} %";
            CpuHistory = _cpuSeries.ToArray();
            CpuStatsText = _cpuStats.Describe("%");
        }
        else
        {
            CpuText = "N/D";
        }

        if (sample.MemoryLoadPercent is { } memory)
        {
            Push(_memSeries, memory);
            _memStats.Add(memory);
            MemoryText = $"{memory:F0} %";
            MemoryHistory = _memSeries.ToArray();
            MemStatsText = _memStats.Describe("%");
        }
        else
        {
            MemoryText = "N/D";
        }

        MemoryDetail = sample.MemoryTotalBytes is { } total
            && sample.MemoryAvailableBytes is { } available
            && total >= available
                ? $"{FormatGb(total - available)} en uso de {FormatGb(total)} · {FormatGb(available)} disponibles"
                : "Memoria física: no disponible";

        CommitDetail = sample.CommitUsedBytes is { } used && sample.CommitLimitBytes is { } limit
            ? $"Commit: {FormatGb(used)} / {FormatGb(limit)}"
            : "Commit: no disponible";

        var elapsed = DateTimeOffset.Now - _sessionStart;
        SessionText = $"Sesión: {elapsed:hh\\:mm\\:ss} · {_cpuStats.Count} muestras de CPU · {_memStats.Count} de memoria";
        StatusText = $"Telemetría activa · 1 Hz · ventana de {_window.Count} s";

        if (_decisionEngine is not null && _ticks % 5 == 0)
            RunDecisionEngine();
    }

    private void RunDecisionEngine()
    {
        try
        {
            var report = _decisionEngine!.Evaluate(
                new DecisionContext(Hardware, _window.ToArray(), _selectedGoal.Goal));
            Replace(Findings, report.Findings);
            Replace(Recommendations, report.Recommendations);
        }
        catch (Exception ex)
        {
            StatusText = $"Error en el motor de decisión: {ex.Message}";
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static void Push(List<double> series, double value)
    {
        series.Add(value);
        if (series.Count > HistoryLength) series.RemoveAt(0);
    }

    private static ScoreDimensionView Unmeasured(ScoreDimension dimension, string name, string requirement)
    {
        var score = new DimensionScore(
            dimension, null, 0, requirement + " Fuente aún no implementada.");
        return new ScoreDimensionView(
            name, score.Value is { } value ? $"{value:F0}" : "Sin medir", score.Basis);
    }

    private static string FormatGb(ulong bytes)
        => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1", CultureInfo.CurrentCulture) + " GB";

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private sealed class RunningStats
    {
        private double _sum, _min = double.MaxValue, _max = double.MinValue;
        public int Count { get; private set; }

        public void Add(double value)
        {
            Count++;
            _sum += value;
            if (value < _min) _min = value;
            if (value > _max) _max = value;
        }

        public string Describe(string unit)
            => Count == 0 ? "Sin muestras"
                : $"mín {_min:F0}{unit} · prom {_sum / Count:F0}{unit} · máx {_max:F0}{unit}";
    }
}
