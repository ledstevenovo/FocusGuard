using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FocusGuard.Core;

namespace FocusGuard.App;

/// <summary>
/// GitHub 式每日专注热力图：列 = 周（近 <see cref="Weeks"/> 周含本周，默认 53 周一年），
/// 行 = 周一…周日（周一顶行）。
/// 刻意用元素化布局（Border 格子）而非 OnRender —— 只有元素才能给 ≥8h 的第 5 档
/// 挂 Storyboard 流光动画（专注统计方案 §5.2）；格子总量 = 53×7，对 WPF 毫无压力。
/// </summary>
public sealed class HeatmapControl : Grid
{
    /// <summary>第 0–4 档的固定颜色；第 5 档（≥8h）用流光画笔。</summary>
    private static readonly Brush[] LevelBrushes =
    {
        Hex("#E5E7EB"), Hex("#C6DDF2"), Hex("#8FBBE4"), Hex("#4D8BCB"), Hex("#185FA5"),
    };

    /// <summary>流光盘面的高饱和色环：每个 GradientStop 沿色环循环滚动，形成持续流动的效果。</summary>
    private static readonly Color[] Rainbow =
    {
        Color.FromRgb(0xFF, 0x52, 0x52), Color.FromRgb(0xFF, 0x98, 0x00),
        Color.FromRgb(0xFF, 0xD6, 0x00), Color.FromRgb(0x66, 0xBB, 0x6A),
        Color.FromRgb(0x29, 0xB6, 0xF6), Color.FromRgb(0xAB, 0x47, 0xBC),
    };

    private const double CellSize = 13;
    private const double CellPitch = 17;   // 格子 + 间隙

    /// <summary>悬停提示里的周几（没有左侧星期标注，星期信息放提示里）。</summary>
    private static readonly string[] WeekdayNames = { "日", "一", "二", "三", "四", "五", "六" };

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(IReadOnlyDictionary<DateOnly, TimeSpan>), typeof(HeatmapControl),
        new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty WeeksProperty = DependencyProperty.Register(
        nameof(Weeks), typeof(int), typeof(HeatmapControl),
        new PropertyMetadata(53, OnDataChanged));

    public HeatmapControl()
    {
        // Data 在挂到可视树之前就可能已赋值；Loaded 再补一次，保证初始渲染
        Loaded += (_, _) => RebuildInstance();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HeatmapControl)d).RebuildInstance();

    /// <summary>日期 → 当日已完成专注总时长；热力图以此判定档位（未来日期画半透明空格）。</summary>
    public IReadOnlyDictionary<DateOnly, TimeSpan>? Data
    {
        get => (IReadOnlyDictionary<DateOnly, TimeSpan>?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public int Weeks
    {
        get => (int)GetValue(WeeksProperty);
        set => SetValue(WeeksProperty, value);
    }

    private void RebuildInstance()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-((today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek) - 1));
        var firstMonday = monday.AddDays(-7 * (Weeks - 1));
        var data = Data ?? new Dictionary<DateOnly, TimeSpan>();

        // 月份行与格子行都是定宽（周数×节距）并各自水平居中 → 列天然对齐、整体居中
        Children.Add(BuildMonthLabels(firstMonday));
        Children.Add(BuildCells(firstMonday, monday, today, data));
    }

    /// <summary>
    /// 顶部月份标注：每当前一列的周一跨入新月份时在该列标"10月"（首列不标）。
    /// 用 Canvas 定位而非定宽槽位：标签左缘严格对齐列起点，而"10月"这类两位数月份
    /// 比一列（17px）宽，固定槽位宽会把"月"字裁掉；Canvas 不裁剪子元素，允许向右溢出。
    /// </summary>
    private UIElement BuildMonthLabels(DateOnly firstMonday)
    {
        var canvas = new Canvas
        {
            Width = Weeks * CellPitch,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var previousMonth = -1;
        for (var i = 0; i < Weeks; i++)
        {
            var weekStart = firstMonday.AddDays(7 * i);
            if (i > 0 && weekStart.Month != previousMonth)
            {
                var label = new TextBlock
                {
                    Text = weekStart.Month + "月",
                    FontSize = 11,
                    Foreground = Hex("#9CA3AF"),
                };
                Canvas.SetLeft(label, i * CellPitch);
                canvas.Children.Add(label);
            }
            previousMonth = weekStart.Month;
        }

        Grid.SetColumn(canvas, 0);
        Grid.SetRow(canvas, 0);
        return canvas;
    }

    private UIElement BuildCells(DateOnly firstMonday, DateOnly lastMonday, DateOnly today, IReadOnlyDictionary<DateOnly, TimeSpan> data)
    {
        var grid = new UniformGrid
        {
            Columns = Weeks,
            Rows = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = Weeks * CellPitch,
        };
        for (var week = 0; week < Weeks; week++)
        {
            var weekStart = firstMonday.AddDays(7 * week);
            for (var day = 0; day < 7; day++)
            {
                var date = weekStart.AddDays(day);
                var total = data.TryGetValue(date, out var value) ? value : TimeSpan.Zero;

                Border cell;
                if (date > today)
                {
                    // 本周内尚未到来的日子：空格但半透明，与"0 档"区分
                    cell = new Border
                    {
                        Width = CellSize,
                        Height = CellSize,
                        CornerRadius = new CornerRadius(2),
                        Margin = new Thickness((CellPitch - CellSize) / 2),
                        Background = LevelBrushes[0],
                        Opacity = 0.4,
                    };
                }
                else if (FocusHistory.HeatLevel(total) >= 5)
                {
                    cell = BuildRainbowCell(CellSize);
                }
                else
                {
                    cell = new Border
                    {
                        Width = CellSize,
                        Height = CellSize,
                        CornerRadius = new CornerRadius(2),
                        Margin = new Thickness((CellPitch - CellSize) / 2),
                        Background = LevelBrushes[FocusHistory.HeatLevel(total)],
                    };
                }

                cell.ToolTip = $"{date.Month}月{date.Day}日 周{WeekdayNames[(int)date.DayOfWeek]} · {(total > TimeSpan.Zero ? FocusHistory.FormatDuration(total) : "无专注记录")}";
                grid.Children.Add(cell);
            }
        }

        Grid.SetColumn(grid, 0);
        Grid.SetRow(grid, 1);
        return grid;
    }

    /// <summary>
    /// ≥8h 档的流光格子：线性渐变的每个 GradientStop 沿色环匀速循环（整圈回到起点，无缝），
    /// Storyboard 挂在 Loaded 上随元素的存活启停 —— 窗口关闭元素销毁，动画自然停，不耗后台。
    /// </summary>
    private static Border BuildRainbowCell(double size)
    {
        var brush = new LinearGradientBrush();
        for (var i = 0; i < Rainbow.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(Rainbow[i], i / (double)(Rainbow.Length - 1)));
        }

        var cell = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(2),
            Background = brush,
        };
        if (size >= CellSize)
        {
            cell.Margin = new Thickness((CellPitch - CellSize) / 2);
        }

        var loop = TimeSpan.FromSeconds(Rainbow.Length * 0.9);
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        for (var stop = 0; stop < brush.GradientStops.Count; stop++)
        {
            var frames = new ColorAnimationUsingKeyFrames { Duration = loop };
            for (var step = 0; step <= Rainbow.Length; step++)
            {
                // 关键帧占比 0→1，最后一帧回到初始色，循环无缝
                frames.KeyFrames.Add(new LinearColorKeyFrame(
                    Rainbow[(stop + step) % Rainbow.Length], KeyTime.FromPercent(step / (double)Rainbow.Length)));
            }
            Storyboard.SetTarget(frames, cell);
            Storyboard.SetTargetProperty(frames,
                new PropertyPath($"(Border.Background).(LinearGradientBrush.GradientStops)[{stop}].(GradientStop.Color)"));
            storyboard.Children.Add(frames);
        }

        cell.Loaded += (_, _) => storyboard.Begin(cell);
        return cell;
    }

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
