using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusGuard.Core;

namespace FocusGuard.App;

public enum TrendChartMode
{
    Bars,
    Line,
}

/// <summary>趋势图的一个数据点：X 轴标签、数值、完整悬停文案、是否当前（今天/本周，高亮）。</summary>
public sealed record TrendPoint(string Label, TimeSpan Value, string Tip, bool IsCurrent);

/// <summary>
/// 柱状 / 折线两用的趋势图（专注统计方案 §5.3）。纯 OnRender 自绘、零外部依赖；
/// Y 轴用 1-2-5 良序刻度向上取整，全部为 0 时以 1h 画基准线，不画"0 高度的柱"。
/// 悬停：MouseMove 找最近数据点更新常驻 ToolTip 的内容。
/// </summary>
public sealed class TrendChartControl : FrameworkElement
{
    private static readonly Brush BarBrush = Hex("#185FA5");
    private static readonly Brush CurrentBrush = Hex("#0C447C");
    private static readonly Brush LabelBrush = Hex("#9CA3AF");
    private static readonly Pen GridPen = new(Hex("#F1F2F4"), 1);

    private readonly ToolTip _tip = new();
    private Rect _plot;
    private IReadOnlyList<TrendPoint> _hitPoints = Array.Empty<TrendPoint>();

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<TrendPoint>), typeof(TrendChartControl),
        new PropertyMetadata(Array.Empty<TrendPoint>(), OnDataChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(TrendChartMode), typeof(TrendChartControl),
        new PropertyMetadata(TrendChartMode.Bars, OnDataChanged));

    public static readonly DependencyProperty AxisFloorHoursProperty = DependencyProperty.Register(
        nameof(AxisFloorHours), typeof(double), typeof(TrendChartControl),
        new PropertyMetadata(2.0, OnDataChanged));

    static TrendChartControl() => GridPen.Freeze();

    public TrendChartControl()
    {
        ToolTip = _tip;
        MouseMove += OnMouseMove;
    }

    public IReadOnlyList<TrendPoint> Points
    {
        get => (IReadOnlyList<TrendPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public TrendChartMode Mode
    {
        get => (TrendChartMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// Y 轴量程下限（小时）。日图默认 2h；周图应设为 14h —— 周汇总天然是日汇总的约 7 倍，
    /// 空数据或数据很少时周图不该和日图共用 2h 的基准量程。
    /// </summary>
    public double AxisFloorHours
    {
        get => (double)GetValue(AxisFloorHoursProperty);
        set => SetValue(AxisFloorHoursProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChartControl)d;
        chart._hitPoints = chart.Points ?? Array.Empty<TrendPoint>();
        chart.InvalidateVisual();
    }

    // 无子元素的 FrameworkElement 默认不参与命中测试，鼠标事件拿不到 —— 认领整个渲染区
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new PointHitTestResult(this, parameters.HitPoint);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_hitPoints.Count == 0 || _plot.Width <= 0)
        {
            return;
        }

        var slot = _plot.Width / _hitPoints.Count;
        var index = (int)((e.GetPosition(this).X - _plot.X) / slot);
        if (index < 0)
        {
            index = 0;
        }
        if (index > _hitPoints.Count - 1)
        {
            index = _hitPoints.Count - 1;
        }
        _tip.Content = _hitPoints[index].Tip;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points ?? Array.Empty<TrendPoint>();
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // 边距：左给 Y 刻度、下给 X 标签
        _plot = new Rect(38, 8, Math.Max(0, RenderSize.Width - 38 - 10), Math.Max(0, RenderSize.Height - 8 - 22));

        var maxHours = points.Count == 0 ? 0 : points.Max(p => p.Value).TotalHours;
        var yMaxHours = AxisMax(Math.Max(maxHours, AxisFloorHours), out var stepHours);

        // 基线 + 整小时步长的水平网格线（2h / 4h / 6h / …）
        dc.DrawLine(GridPen, new Point(_plot.X, _plot.Bottom), new Point(_plot.Right, _plot.Bottom));
        for (var value = stepHours; value <= yMaxHours + 0.001; value += stepHours)
        {
            var y = _plot.Bottom - _plot.Height * (value / yMaxHours);
            dc.DrawLine(GridPen, new Point(_plot.X, y), new Point(_plot.Right, y));
            var label = (int)value + "h";
            dc.DrawText(MakeText(label, 11, LabelBrush, typeface, dpi),
                new Point(_plot.X - 6 - TextWidth(label, 11, typeface, dpi), y - 7));
        }

        if (points.Count == 0)
        {
            return;
        }

        var slot = _plot.Width / points.Count;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var centerX = _plot.X + slot * (i + 0.5);

            // X 轴标签
            var label = MakeText(point.Label, 11, LabelBrush, typeface, dpi);
            dc.DrawText(label, new Point(centerX - label.Width / 2, _plot.Bottom + 5));

            if (point.Value <= TimeSpan.Zero)
            {
                continue;   // 空数据不画 0 高度的柱 / 点
            }

            var valueRatio = point.Value.TotalHours / yMaxHours;
            var brush = point.IsCurrent ? CurrentBrush : BarBrush;

            if (Mode == TrendChartMode.Bars)
            {
                var width = Math.Max(6, slot * 0.55);
                var height = Math.Max(2, _plot.Height * valueRatio);
                var geometry = new RectangleGeometry(
                    new Rect(centerX - width / 2, _plot.Bottom - height, width, height), 2, 2);
                dc.DrawGeometry(brush, null, geometry);
            }
            else
            {
                if (i > 0 && points[i - 1].Value > TimeSpan.Zero)
                {
                    var previous = points[i - 1];
                    var prevY = _plot.Bottom - _plot.Height * (previous.Value.TotalHours / yMaxHours);
                    var currentY = _plot.Bottom - _plot.Height * valueRatio;
                    var line = new StreamGeometry();
                    using (var context = line.Open())
                    {
                        context.BeginFigure(new Point(_plot.X + slot * (i - 0.5), prevY), false, false);
                        context.LineTo(new Point(centerX, currentY), true, false);
                    }
                    line.Freeze();
                    dc.DrawGeometry(null, new Pen(brush, 1.5), line);
                }

                var radius = point.IsCurrent ? 4.5 : 3.5;
                dc.DrawEllipse(brush, null, new Point(centerX, _plot.Bottom - _plot.Height * valueRatio), radius, radius);
            }
        }
    }

    /// <summary>
    /// Y 轴满量程：以 2h 步长向上取整（2h/4h/6h/8h/10h…）；数值很大时步长加倍
    /// （超过 12h 用 4h、超过 24h 用 8h），保证刻度数量不超过 6 条。
    /// </summary>
    private static double AxisMax(double scaleHours, out double stepHours)
    {
        stepHours = scaleHours <= 12 ? 2 : scaleHours <= 24 ? 4 : 8;
        return Math.Ceiling(scaleHours / stepHours) * stepHours;
    }

    private static FormattedText MakeText(string text, double size, Brush brush, Typeface typeface, DpiScale dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi.PixelsPerDip);

    private static double TextWidth(string text, double size, Typeface typeface, DpiScale dpi) =>
        MakeText(text, size, LabelBrush, typeface, dpi).Width;

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
