$ErrorActionPreference = "Stop"
$ProjectRoot = Join-Path $env:USERPROFILE "Desktop\Evo_optimizer"
if (-not (Test-Path $ProjectRoot)) { throw "No existe la carpeta del proyecto: $ProjectRoot" }
Set-Location $ProjectRoot
Write-Host "Integrando EVO FPS OPTIMIZER - Fase 1..." -ForegroundColor Cyan

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Telemetry\TelemetrySnapshot.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
namespace EvoOptimizer.Core.Telemetry;

/// <summary>
/// Muestra puntual. Todo valor es nullable: null significa "no medido / no disponible",
/// nunca "cero". Ningún consumidor debe asumir un valor por defecto.
/// </summary>
public sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    double? CpuUtilizationPercent,
    double? MemoryLoadPercent,
    ulong? MemoryTotalBytes,
    ulong? MemoryAvailableBytes,
    ulong? CommitUsedBytes,
    ulong? CommitLimitBytes);
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Telemetry/TelemetrySnapshot.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Telemetry\ITelemetryProvider.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
namespace EvoOptimizer.Core.Telemetry;

public interface ITelemetryProvider
{
    /// <summary>Toma una muestra. Debe ser barata; proveedores costosos deben cachear internamente.</summary>
    TelemetrySnapshot Sample();
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Telemetry/ITelemetryProvider.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Hardware\HardwareProfile.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
namespace EvoOptimizer.Core.Hardware;

public sealed record GpuInfo(string Name, ulong? DedicatedMemoryBytes);

/// <summary>Identidad del equipo. Null = no se pudo determinar.</summary>
public sealed record HardwareProfile(
    string? OsDescription,
    string? CpuName,
    int LogicalProcessors,
    uint? CpuNominalMhz,
    ulong? TotalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus);
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Hardware/HardwareProfile.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Hardware\IHardwareProfileProvider.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
namespace EvoOptimizer.Core.Hardware;

public interface IHardwareProfileProvider
{
    HardwareProfile Capture();
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Hardware/IHardwareProfileProvider.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Decisions\DecisionModels.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
namespace EvoOptimizer.Core.Decisions;

public enum OptimizationGoal { Everyday, Gaming, MaximumPerformance }
public enum RiskLevel { None, Low, Medium, High }
public enum Severity { Info, Warning, Critical }

/// <summary>Observación concreta que respalda un hallazgo.</summary>
public sealed record Evidence(string Metric, string Observation, string Source);

public sealed record Finding(
    string Id,
    string Title,
    Severity Severity,
    double Confidence,
    string TechnicalExplanation,
    IReadOnlyList<Evidence> Evidence,
    string? Limitations);

public sealed record Recommendation(
    string Id,
    string Title,
    string Rationale,
    double Confidence,
    RiskLevel Risk,
    string ExpectedBenefit,
    string RevertStrategy,
    string? CausalityCaveat);
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Decisions/DecisionModels.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Decisions\IDecisionEngine.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Core.Telemetry;

namespace EvoOptimizer.Core.Decisions;

public sealed record DecisionContext(
    HardwareProfile Hardware,
    IReadOnlyList<TelemetrySnapshot> RecentTelemetry,
    OptimizationGoal Goal);

public sealed record DecisionReport(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Recommendation> Recommendations);

/// <summary>Punto de integración del Adaptive Decision Engine. Solo analiza; nunca aplica cambios.</summary>
public interface IDecisionEngine
{
    DecisionReport Evaluate(DecisionContext context);
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Decisions/IDecisionEngine.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Core\Scoring\PerformanceScore.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using EvoOptimizer.Core.Decisions;

namespace EvoOptimizer.Core.Scoring;

public enum ScoreDimension { Stability, Performance, Efficiency, Health }

/// <param name="Value">0..100, o null si la dimensión no se pudo medir.</param>
/// <param name="Confidence">0..1</param>
/// <param name="Basis">Qué datos sustentan el valor, o qué falta para medirlo.</param>
public sealed record DimensionScore(ScoreDimension Dimension, double? Value, double Confidence, string Basis);

/// <summary>
/// Multidimensional a propósito: no existe un número global. Solo es comparable con otro score
/// que tenga el mismo Goal y WorkloadContext.
/// </summary>
public sealed record PerformanceScore(
    OptimizationGoal Goal,
    string WorkloadContext,
    IReadOnlyList<DimensionScore> Dimensions);
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Core/Scoring/PerformanceScore.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Infrastructure\Native\NativeMethods.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using System.Runtime.InteropServices;

namespace EvoOptimizer.Infrastructure.Native;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    internal static bool TryGetMemoryStatus(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status);
    }
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Infrastructure/Native/NativeMethods.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Infrastructure\Telemetry\WindowsTelemetryProvider.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using System.Runtime.Versioning;
using EvoOptimizer.Core.Telemetry;
using EvoOptimizer.Infrastructure.Native;

namespace EvoOptimizer.Infrastructure.Telemetry;

/// <summary>
/// CPU: delta de GetSystemTimes entre muestras (utilización global, 0..100).
/// La primera muestra devuelve null porque no existe delta todavía.
/// Memoria: GlobalMemoryStatusEx (física y commit).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTelemetryProvider : ITelemetryProvider
{
    private bool _hasPrevious;
    private ulong _prevIdle, _prevKernel, _prevUser;

    public TelemetrySnapshot Sample()
    {
        double? memLoad = null;
        ulong? total = null, avail = null, commitUsed = null, commitLimit = null;

        if (NativeMethods.TryGetMemoryStatus(out var m))
        {
            memLoad = m.dwMemoryLoad;
            total = m.ullTotalPhys;
            avail = m.ullAvailPhys;
            commitLimit = m.ullTotalPageFile;
            commitUsed = m.ullTotalPageFile >= m.ullAvailPageFile
                ? m.ullTotalPageFile - m.ullAvailPageFile
                : null;
        }

        return new TelemetrySnapshot(
            DateTimeOffset.Now, ReadCpuPercent(), memLoad, total, avail, commitUsed, commitLimit);
    }

    private double? ReadCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        ulong i = idle.Value, k = kernel.Value, u = user.Value;
        double? result = null;

        if (_hasPrevious && i >= _prevIdle && k >= _prevKernel && u >= _prevUser)
        {
            ulong dIdle = i - _prevIdle;
            ulong dTotal = (k - _prevKernel) + (u - _prevUser);
            if (dTotal > 0 && dTotal >= dIdle)
                result = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
        }

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;
        _hasPrevious = true;
        return result;
    }
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Infrastructure/Telemetry/WindowsTelemetryProvider.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.Infrastructure\Hardware\RegistryHardwareProfileProvider.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using EvoOptimizer.Core.Hardware;
using EvoOptimizer.Infrastructure.Native;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Hardware;

/// <summary>
/// Perfil básico desde el registro y APIs nativas, sin elevación.
/// El registro puede incluir adaptadores inactivos; la VRAM depende del dato reportado por el driver.
/// No distingue iGPU/dGPU ni determina núcleos físicos en esta fase.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryHardwareProfileProvider : IHardwareProfileProvider
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11cd-bfc1-08002be10318}";

    public HardwareProfile Capture()
    {
        string? cpuName = null;
        uint? mhz = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
            cpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            if (key?.GetValue("~MHz") is int m && m > 0)
                mhz = (uint)m;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        ulong? totalRam = NativeMethods.TryGetMemoryStatus(out var ms) ? ms.ullTotalPhys : null;

        return new HardwareProfile(
            RuntimeInformation.OSDescription,
            string.IsNullOrWhiteSpace(cpuName) ? null : cpuName,
            Environment.ProcessorCount,
            mhz,
            totalRam,
            ReadGpus());
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return gpus;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;

                using var key = cls.OpenSubKey(sub);
                if (key is null) continue;

                var name = (key.GetValue("DriverDesc") as string)?.Trim();
                if (string.IsNullOrEmpty(name) || gpus.Any(g => g.Name == name)) continue;

                ulong? vram = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long value when value > 0 => (ulong)value,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                    _ => null
                };
                if (vram == 0) vram = null;

                gpus.Add(new GpuInfo(name, vram));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        return gpus;
    }
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.Infrastructure/Hardware/RegistryHardwareProfileProvider.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\Mvvm\ObservableObject.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EvoOptimizer.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/Mvvm/ObservableObject.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\Controls\Sparkline.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
using System.Windows;
using System.Windows.Media;

namespace EvoOptimizer.App.Controls;

/// <summary>Gráfico de línea en tiempo real. Escala fija 0..Maximum; el dato más reciente queda a la derecha.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxPointsProperty = DependencyProperty.Register(
        nameof(MaxPoints), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int MaxPoints { get => (int)GetValue(MaxPointsProperty); set => SetValue(MaxPointsProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), 1);
        for (int q = 1; q < 4; q++)
        {
            double y = Math.Round(height * q / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        var values = Values;
        if (values is null || values.Count < 2 || Stroke is null) return;

        int slots = Math.Max(2, MaxPoints);
        double top = Maximum > 0 ? Maximum : 100;
        double step = width / (slots - 1);
        double x0 = width - (values.Count - 1) * step;

        Point PointAt(int index) =>
            new(x0 + index * step, height - Math.Clamp(values[index] / top, 0, 1) * height);

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(PointAt(0), false, false);
            for (int i = 1; i < values.Count; i++)
                context.LineTo(PointAt(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(x0, height), true, true);
            for (int i = 0; i < values.Count; i++)
                context.LineTo(PointAt(i), false, false);
            context.LineTo(new Point(width, height), false, false);
        }
        area.Freeze();

        var fill = Stroke.CloneCurrentValue();
        fill.Opacity = 0.15;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/Controls/Sparkline.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\Themes\EvoDark.xaml"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <SolidColorBrush x:Key="BgBrush" Color="#0B0D10"/>
    <SolidColorBrush x:Key="PanelBrush" Color="#14171C"/>
    <SolidColorBrush x:Key="PanelAltBrush" Color="#1B1F26"/>
    <SolidColorBrush x:Key="LineBrush" Color="#262B33"/>
    <SolidColorBrush x:Key="TextBrush" Color="#E6E9EE"/>
    <SolidColorBrush x:Key="DimBrush" Color="#8A93A0"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#2979FF"/>
    <SolidColorBrush x:Key="OkBrush" Color="#2ECC71"/>
    <SolidColorBrush x:Key="WarnBrush" Color="#FFB020"/>
    <SolidColorBrush x:Key="CritBrush" Color="#FF4D4F"/>
    <SolidColorBrush x:Key="OffBrush" Color="#4A515C"/>

    <Style x:Key="PanelStyle" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource PanelBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="6"/>
        <Setter Property="Padding" Value="14"/>
        <Setter Property="Margin" Value="0,0,0,12"/>
    </Style>

    <Style x:Key="SectionTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{StaticResource AccentBrush}"/>
        <Setter Property="Margin" Value="0,0,0,10"/>
    </Style>

    <Style x:Key="DimText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Foreground" Value="{StaticResource DimBrush}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>

    <Style x:Key="BodyText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
        <Setter Property="Margin" Value="0,0,0,10"/>
    </Style>

    <Style x:Key="BigValue" TargetType="TextBlock">
        <Setter Property="FontSize" Value="30"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="FontFamily" Value="Consolas"/>
    </Style>

    <Style x:Key="GoalItemStyle" TargetType="ListBoxItem">
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bd" Background="{StaticResource PanelAltBrush}"
                            BorderBrush="{StaticResource LineBrush}" BorderThickness="1"
                            CornerRadius="4" Padding="18,8" Margin="0,0,6,0">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentBrush}"/>
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="EmptyWhenZero" TargetType="TextBlock" BasedOn="{StaticResource DimText}">
        <Setter Property="Visibility" Value="Collapsed"/>
        <Style.Triggers>
            <DataTrigger Binding="{Binding Tag.Count, RelativeSource={RelativeSource Self}}" Value="0">
                <Setter Property="Visibility" Value="Visible"/>
            </DataTrigger>
        </Style.Triggers>
    </Style>
</ResourceDictionary>
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/Themes/EvoDark.xaml" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\ViewModels\DashboardViewModel.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
@'
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
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/ViewModels/DashboardViewModel.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\MainWindow.xaml.cs"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
if (Test-Path $target) {
    $backup = "$target.pre-evo-phase1-$(Get-Date -Format yyyyMMdd-HHmmss).bak"
    Copy-Item $target $backup -Force
    Write-Host "Respaldo creado: $backup" -ForegroundColor Yellow
}
@'
using System.Windows;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Composition root. Fases futuras: sustituir por DI y pasar un IDecisionEngine real.
        var hardware = new RegistryHardwareProfileProvider().Capture();
        _viewModel = new DashboardViewModel(new WindowsTelemetryProvider(), hardware);
        DataContext = _viewModel;

        Closed += (_, _) => _viewModel.Dispose();
    }
}
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/MainWindow.xaml.cs" -ForegroundColor Green

$target = Join-Path $ProjectRoot "src\EvoOptimizer.App\MainWindow.xaml"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
if (Test-Path $target) {
    $backup = "$target.pre-evo-phase1-$(Get-Date -Format yyyyMMdd-HHmmss).bak"
    Copy-Item $target $backup -Force
    Write-Host "Respaldo creado: $backup" -ForegroundColor Yellow
}
@'
<Window x:Class="EvoOptimizer.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ctl="clr-namespace:EvoOptimizer.App.Controls"
        Title="EVO FPS Optimizer — Adaptive Intelligence Platform"
        Width="1380" Height="840" MinWidth="1150" MinHeight="700"
        Background="#0B0D10" WindowStartupLocation="CenterScreen">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/EvoDark.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>

    <Grid Background="{StaticResource BgBrush}" TextElement.Foreground="{StaticResource TextBrush}"
          TextElement.FontFamily="Segoe UI" Margin="0">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Border Grid.Row="0" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource LineBrush}"
                BorderThickness="0,0,0,1" Padding="20,12">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0" Margin="0,0,40,0" VerticalAlignment="Center">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="EVO" FontSize="24" FontWeight="Bold" Foreground="{StaticResource AccentBrush}"/>
                        <TextBlock Text=" FPS OPTIMIZER" FontSize="24" FontWeight="Light"/>
                    </StackPanel>
                    <TextBlock Text="Adaptive Intelligence Platform" Style="{StaticResource DimText}"/>
                </StackPanel>
                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                    <TextBlock Text="OBJETIVO" Style="{StaticResource SectionTitle}" Margin="0,0,0,6"/>
                    <ListBox ItemsSource="{Binding Goals}" SelectedItem="{Binding SelectedGoal}"
                             ItemContainerStyle="{StaticResource GoalItemStyle}"
                             Background="Transparent" BorderThickness="0" HorizontalAlignment="Left">
                        <ListBox.ItemsPanel>
                            <ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate>
                        </ListBox.ItemsPanel>
                        <ListBox.ItemTemplate>
                            <DataTemplate><TextBlock Text="{Binding Label}" FontWeight="SemiBold"/></DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                    <TextBlock Text="{Binding SelectedGoalDescription}" Style="{StaticResource DimText}" Margin="0,6,0,0"/>
                    <TextBlock Style="{StaticResource DimText}" Foreground="{StaticResource WarnBrush}" Margin="0,2,0,0"
                               Text="Fase actual: el objetivo solo se registra. EVO no aplica ningún cambio al sistema."/>
                </StackPanel>
            </Grid>
        </Border>

        <Grid Grid.Row="1" Margin="16,16,16,4">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="300"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="420"/>
            </Grid.ColumnDefinitions>

            <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto" Margin="0,0,12,0">
                <StackPanel>
                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="PERFIL DE HARDWARE" Style="{StaticResource SectionTitle}"/>
                            <TextBlock Text="Sistema operativo" Style="{StaticResource DimText}"/>
                            <TextBlock Style="{StaticResource BodyText}"
                                       Text="{Binding Hardware.OsDescription, TargetNullValue='No disponible'}"/>
                            <TextBlock Text="Procesador" Style="{StaticResource DimText}"/>
                            <TextBlock Style="{StaticResource BodyText}" Margin="0,0,0,2"
                                       Text="{Binding Hardware.CpuName, TargetNullValue='No disponible'}"/>
                            <TextBlock Style="{StaticResource DimText}" Margin="0,0,0,10">
                                <Run Text="{Binding Hardware.LogicalProcessors, Mode=OneWay}"/>
                                <Run Text=" procesadores lógicos disponibles · "/>
                                <Run Text="{Binding CpuNominalText, Mode=OneWay}"/>
                            </TextBlock>
                            <TextBlock Text="Memoria instalada" Style="{StaticResource DimText}"/>
                            <TextBlock Style="{StaticResource BodyText}" Text="{Binding RamText}"/>
                            <TextBlock Text="Adaptadores gráficos" Style="{StaticResource DimText}"/>
                            <ItemsControl ItemsSource="{Binding GpuLines}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate><TextBlock Style="{StaticResource BodyText}" Margin="0,0,0,4" Text="{Binding}"/></DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <TextBlock Style="{StaticResource DimText}" Margin="0,8,0,0"
                                       Text="Fuente: registro de Windows. No se distingue iGPU/dGPU ni núcleos físicos todavía; el registro puede listar adaptadores inactivos."/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="COBERTURA DE SENSORES" Style="{StaticResource SectionTitle}"/>
                            <ItemsControl ItemsSource="{Binding Sensors}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Grid Margin="0,0,0,8">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="Auto"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>
                                            <Ellipse Width="8" Height="8" Margin="0,5,10,0" VerticalAlignment="Top">
                                                <Ellipse.Style>
                                                    <Style TargetType="Ellipse">
                                                        <Setter Property="Fill" Value="{StaticResource OffBrush}"/>
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsActive}" Value="True">
                                                                <Setter Property="Fill" Value="{StaticResource OkBrush}"/>
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </Ellipse.Style>
                                            </Ellipse>
                                            <StackPanel Grid.Column="1">
                                                <TextBlock Text="{Binding Name}" FontSize="12"/>
                                                <TextBlock Text="{Binding Source}" Style="{StaticResource DimText}"/>
                                            </StackPanel>
                                        </Grid>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>

            <ScrollViewer Grid.Column="1" VerticalScrollBarVisibility="Auto" Margin="0,0,12,0">
                <StackPanel>
                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <Grid>
                                <TextBlock Text="CPU · UTILIZACIÓN GLOBAL" Style="{StaticResource SectionTitle}"/>
                                <TextBlock Text="{Binding CpuText}" Style="{StaticResource BigValue}" HorizontalAlignment="Right"
                                           Foreground="{StaticResource AccentBrush}"/>
                            </Grid>
                            <ctl:Sparkline Height="130" Values="{Binding CpuHistory}" Stroke="{StaticResource AccentBrush}"/>
                            <TextBlock Style="{StaticResource DimText}" Margin="0,8,0,0"
                                       Text="Fuente: GetSystemTimes · escala fija 0–100 % · últimos 120 s. Es utilización global, no distingue cuellos de botella por hilo."/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <Grid>
                                <TextBlock Text="MEMORIA · CARGA FÍSICA" Style="{StaticResource SectionTitle}"/>
                                <TextBlock Text="{Binding MemoryText}" Style="{StaticResource BigValue}" HorizontalAlignment="Right"
                                           Foreground="{StaticResource AccentBrush}"/>
                            </Grid>
                            <ctl:Sparkline Height="130" Values="{Binding MemoryHistory}" Stroke="{StaticResource AccentBrush}"/>
                            <TextBlock Text="{Binding MemoryDetail}" Style="{StaticResource DimText}" Margin="0,8,0,0"/>
                            <TextBlock Text="{Binding CommitDetail}" Style="{StaticResource DimText}"/>
                            <TextBlock Style="{StaticResource DimText}" Margin="0,4,0,0"
                                       Text="Una carga alta no implica problema: Windows usa RAM libre como caché. La presión real requiere commit y fallos de página (pendiente)."/>
                        </StackPanel>
                    </Border>

                    <UniformGrid Columns="3" Margin="0,0,-12,0">
                        <Border Style="{StaticResource PanelStyle}" Margin="0,0,12,12">
                            <StackPanel>
                                <TextBlock Text="GPU" Style="{StaticResource SectionTitle}"/>
                                <TextBlock Text="No disponible" FontSize="16" Foreground="{StaticResource DimBrush}"/>
                                <TextBlock Text="Sin fuente de uso/VRAM aún." Style="{StaticResource DimText}"/>
                            </StackPanel>
                        </Border>
                        <Border Style="{StaticResource PanelStyle}" Margin="0,0,12,12">
                            <StackPanel>
                                <TextBlock Text="TEMPERATURA" Style="{StaticResource SectionTitle}"/>
                                <TextBlock Text="No disponible" FontSize="16" Foreground="{StaticResource DimBrush}"/>
                                <TextBlock Text="Requiere driver de sensores." Style="{StaticResource DimText}"/>
                            </StackPanel>
                        </Border>
                        <Border Style="{StaticResource PanelStyle}" Margin="0,0,12,12">
                            <StackPanel>
                                <TextBlock Text="FRAMETIME" Style="{StaticResource SectionTitle}"/>
                                <TextBlock Text="No disponible" FontSize="16" Foreground="{StaticResource DimBrush}"/>
                                <TextBlock Text="Requiere captura ETW." Style="{StaticResource DimText}"/>
                            </StackPanel>
                        </Border>
                    </UniformGrid>

                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="HISTORIAL DE LA SESIÓN" Style="{StaticResource SectionTitle}"/>
                            <TextBlock Text="CPU" Style="{StaticResource DimText}"/>
                            <TextBlock Text="{Binding CpuStatsText}" FontFamily="Consolas" Margin="0,0,0,8"/>
                            <TextBlock Text="Memoria" Style="{StaticResource DimText}"/>
                            <TextBlock Text="{Binding MemStatsText}" FontFamily="Consolas" Margin="0,0,0,8"/>
                            <TextBlock Text="{Binding SessionText}" Style="{StaticResource DimText}"/>
                            <TextBlock Style="{StaticResource DimText}" Margin="0,4,0,0"
                                       Text="Solo sesión actual en memoria. La persistencia y comparación entre sesiones corresponde al Historical Analysis Engine (pendiente)."/>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>

            <ScrollViewer Grid.Column="2" VerticalScrollBarVisibility="Auto">
                <StackPanel>
                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="EVO PERFORMANCE SCORE" Style="{StaticResource SectionTitle}"/>
                            <ItemsControl ItemsSource="{Binding Score}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border Background="{StaticResource PanelAltBrush}" CornerRadius="4" Padding="10" Margin="0,0,0,8">
                                            <StackPanel>
                                                <Grid>
                                                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                                                    <TextBlock Text="{Binding ValueText}" HorizontalAlignment="Right"
                                                               Foreground="{StaticResource DimBrush}" FontFamily="Consolas"/>
                                                </Grid>
                                                <TextBlock Text="{Binding Basis}" Style="{StaticResource DimText}" Margin="0,4,0,0"/>
                                            </StackPanel>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <TextBlock Style="{StaticResource DimText}"
                                       Text="Sin número global por diseño: cada dimensión se reporta con su confianza y solo es comparable en el mismo contexto de carga."/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="DIAGNÓSTICO EXPLICABLE" Style="{StaticResource SectionTitle}"/>
                            <ItemsControl ItemsSource="{Binding Findings}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border Background="{StaticResource PanelAltBrush}" CornerRadius="4" Padding="10" Margin="0,0,0,8">
                                            <StackPanel>
                                                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextWrapping="Wrap"/>
                                                <TextBlock Style="{StaticResource DimText}" Margin="0,2,0,6">
                                                    <Run Text="{Binding Severity, Mode=OneWay}"/>
                                                    <Run Text=" · "/>
                                                    <Run Text="{Binding Confidence, Mode=OneWay, StringFormat='Confianza {0:P0}'}"/>
                                                </TextBlock>
                                                <TextBlock Text="{Binding TechnicalExplanation}" TextWrapping="Wrap" FontSize="12"/>
                                                <TextBlock Text="{Binding Limitations, StringFormat='Limitaciones: {0}'}"
                                                           Style="{StaticResource DimText}" Margin="0,6,0,0"/>
                                            </StackPanel>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <TextBlock Style="{StaticResource EmptyWhenZero}" Tag="{Binding Findings}"
                                       Text="Sin hallazgos. El motor de diagnóstico y detección de cuellos de botella aún no está implementado; EVO no emite conclusiones sin evidencia."/>
                        </StackPanel>
                    </Border>

                    <Border Style="{StaticResource PanelStyle}">
                        <StackPanel>
                            <TextBlock Text="DECISIONES ADAPTATIVAS" Style="{StaticResource SectionTitle}"/>
                            <ItemsControl ItemsSource="{Binding Recommendations}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border Background="{StaticResource PanelAltBrush}" CornerRadius="4" Padding="10" Margin="0,0,0,8">
                                            <StackPanel>
                                                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextWrapping="Wrap"/>
                                                <TextBlock Style="{StaticResource DimText}" Margin="0,2,0,6">
                                                    <Run Text="{Binding Confidence, Mode=OneWay, StringFormat='Confianza {0:P0}'}"/>
                                                    <Run Text=" · Riesgo "/>
                                                    <Run Text="{Binding Risk, Mode=OneWay}"/>
                                                </TextBlock>
                                                <TextBlock Text="{Binding Rationale}" TextWrapping="Wrap" FontSize="12"/>
                                                <TextBlock Text="{Binding ExpectedBenefit, StringFormat='Beneficio esperado: {0}'}"
                                                           Style="{StaticResource DimText}" Margin="0,6,0,0"/>
                                                <TextBlock Text="{Binding RevertStrategy, StringFormat='Reversión: {0}'}"
                                                           Style="{StaticResource DimText}"/>
                                                <TextBlock Text="{Binding CausalityCaveat, StringFormat='Advertencia: {0}'}"
                                                           Style="{StaticResource DimText}"/>
                                            </StackPanel>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <TextBlock Style="{StaticResource EmptyWhenZero}" Tag="{Binding Recommendations}"
                                       Text="Sin decisiones propuestas. El Adaptive Decision Engine no está conectado (IDecisionEngine sin implementación). No se propone ni aplica ningún ajuste."/>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </ScrollViewer>
        </Grid>

        <Border Grid.Row="2" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource LineBrush}"
                BorderThickness="0,1,0,0" Padding="20,6">
            <TextBlock Text="{Binding StatusText}" Style="{StaticResource DimText}"/>
        </Border>
    </Grid>
</Window>
'@ | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "Creado: src/EvoOptimizer.App/MainWindow.xaml" -ForegroundColor Green

Write-Host ""
Write-Host "Integración de archivos terminada. No se modificaron .csproj ni App.xaml." -ForegroundColor Green
Write-Host "Ahora ejecuta: dotnet build .\EVO-FPS-OPTIMIZER.slnx" -ForegroundColor Cyan
