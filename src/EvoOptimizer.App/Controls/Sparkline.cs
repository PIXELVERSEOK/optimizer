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
