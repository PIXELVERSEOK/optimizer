using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EvoOptimizer.Infrastructure.Analysis;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App.ViewModels;

public sealed class EvoControlCenterViewModel :
    INotifyPropertyChanged, IAsyncDisposable
{
    private readonly LivePerformanceMonitor _monitor;
    private readonly LiveAnomalyDetector _anomalyDetector;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private bool _isMonitoring;
    private bool _disposed;

    private double _systemCpuPercent;
    private double? _cs2CpuPercent;
    private double _memoryUsagePercent;
    private double _availableMemoryMb;
    private double _cs2MemoryMb;
    private int _activeProcessCount;
    private int _cs2ProcessCount;
    private DateTimeOffset? _lastUpdated;
    private string _monitorStatus = "DETENIDO";
    private string _systemHealth = "SIN DATOS";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LiveAnomaly> Anomalies { get; } = new();

    public ObservableCollection<string> Alerts { get; } = new();

    public double SystemCpuPercent
    {
        get => _systemCpuPercent;
        private set => SetField(ref _systemCpuPercent, value);
    }

    public double? Cs2CpuPercent
    {
        get => _cs2CpuPercent;
        private set => SetField(ref _cs2CpuPercent, value);
    }

    public double MemoryUsagePercent
    {
        get => _memoryUsagePercent;
        private set => SetField(ref _memoryUsagePercent, value);
    }

    public double AvailableMemoryMb
    {
        get => _availableMemoryMb;
        private set => SetField(ref _availableMemoryMb, value);
    }

    public double Cs2MemoryMb
    {
        get => _cs2MemoryMb;
        private set => SetField(ref _cs2MemoryMb, value);
    }

    public int ActiveProcessCount
    {
        get => _activeProcessCount;
        private set => SetField(ref _activeProcessCount, value);
    }

    public int Cs2ProcessCount
    {
        get => _cs2ProcessCount;
        private set => SetField(ref _cs2ProcessCount, value);
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set => SetField(ref _lastUpdated, value);
    }

    public string MonitorStatus
    {
        get => _monitorStatus;
        private set => SetField(ref _monitorStatus, value);
    }

    public string SystemHealth
    {
        get => _systemHealth;
        private set => SetField(ref _systemHealth, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetField(ref _isMonitoring, value))
                OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool CanStart => !IsMonitoring && !_disposed;

    public EvoControlCenterViewModel(
        LivePerformanceMonitor? monitor = null,
        LiveAnomalyDetector? anomalyDetector = null,
        Dispatcher? dispatcher = null)
    {
        _monitor = monitor ?? new LivePerformanceMonitor();
        _anomalyDetector = anomalyDetector ?? new LiveAnomalyDetector();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        _monitor.SnapshotCaptured += OnSnapshotCaptured;
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsMonitoring)
            return Task.CompletedTask;

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        var token = _cancellation.Token;

        IsMonitoring = true;
        MonitorStatus = "INICIANDO";
        SystemHealth = "RECOPILANDO DATOS";

        _monitorTask = Task.Run(
            () => _monitor.StartAsync(token),
            token);

        _ = ObserveMonitorAsync(_monitorTask);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsMonitoring)
            return;

        MonitorStatus = "DETENIENDO";
        _cancellation?.Cancel();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Detención solicitada por el usuario.
            }
        }

        IsMonitoring = false;
        MonitorStatus = "DETENIDO";
    }

    private async Task ObserveMonitorAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Finalización normal del monitor.
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                MonitorStatus = "ERROR";
                SystemHealth = "ERROR DE MONITORIZACIÓN";
                Alerts.Insert(0, ex.Message);
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsMonitoring = false;

                if (MonitorStatus != "ERROR")
                    MonitorStatus = "DETENIDO";
            });
        }
    }

    private void OnSnapshotCaptured(LivePerformanceSnapshot snapshot)
    {
        _ = _dispatcher.InvokeAsync(() => UpdateDashboard(snapshot));
    }

    private void UpdateDashboard(LivePerformanceSnapshot snapshot)
    {
        if (_disposed)
            return;

        SystemCpuPercent = snapshot.SystemCpuPercent;
        Cs2CpuPercent = snapshot.Cs2CpuPercent;
        MemoryUsagePercent = snapshot.MemoryUsagePercent;
        AvailableMemoryMb = snapshot.AvailableMemoryMb;
        Cs2MemoryMb = snapshot.Cs2MemoryMb;
        ActiveProcessCount = snapshot.ActiveProcessCount;
        Cs2ProcessCount = snapshot.Cs2ProcessCount;
        LastUpdated = snapshot.Timestamp;

        var report = _anomalyDetector.Analyze(_monitor.GetHistory());

        Anomalies.Clear();

        foreach (var anomaly in report.Anomalies)
            Anomalies.Add(anomaly);

        Alerts.Clear();

        foreach (var alert in snapshot.Alerts)
            Alerts.Add(alert);

        foreach (var anomaly in report.Anomalies)
        {
            if (!Alerts.Contains(anomaly.Code))
                Alerts.Add($"{anomaly.Title}: {anomaly.Evidence}");
        }

        SystemHealth = !report.HasEnoughData
            ? "RECOPILANDO DATOS"
            : report.Anomalies.Any(x =>
                x.Severity == LiveAnomalySeverity.Critical)
                ? "ATENCIÓN CRÍTICA"
                : report.Anomalies.Any(x =>
                    x.Severity == LiveAnomalySeverity.Warning)
                    ? "ATENCIÓN"
                    : "ESTABLE";

        MonitorStatus = "MONITOREANDO";
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (IsMonitoring)
            await StopAsync();

        _disposed = true;
        _monitor.SnapshotCaptured -= OnSnapshotCaptured;
        _cancellation?.Dispose();
        _monitor.Dispose();
    }
}
