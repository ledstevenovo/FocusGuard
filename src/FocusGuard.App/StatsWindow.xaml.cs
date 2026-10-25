using System.Windows;
using System.Windows.Input;
using FocusGuard.Core;

namespace FocusGuard.App;

/// <summary>
/// 统计页（专注统计设计方案 §5）：打开时对 <see cref="FocusHistory"/> 做一次快照，
/// 模态期间不实时刷新、进行中的会话不计入（与首页实时显示的口径差见方案 §2.3）。
/// 时间筛选（全部/30天/7天）只作用于卡片与脚注；热力图与两张趋势图固定各自的时间尺度。
/// </summary>
public partial class StatsWindow : Window
{
    private static readonly string[] WeekdayNames = { "一", "二", "三", "四", "五", "六", "日" };

    private readonly FocusHistory _history;
    private readonly DateOnly _today;

    public StatsWindow(FocusHistory history)
    {
        InitializeComponent();
        _history = history;
        _today = DateOnly.FromDateTime(DateTime.Now);

        BuildHeatmap();
        BuildDailyChart();
        BuildWeeklyChart();
        RefreshCards();
    }

    // ---- 数据装配 ----

    private void BuildHeatmap()
    {
        var monday = _today.AddDays(-((int)(_today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)_today.DayOfWeek) - 1));
        var firstMonday = monday.AddDays(-7 * (Heatmap.Weeks - 1));
        Heatmap.Data = _history.DailyTotals(firstMonday, _today)
            .ToDictionary(t => t.Day, t => t.Total);
    }

    private void BuildDailyChart()
    {
        DailyChart.Points = _history.DailyTotals(_today.AddDays(-6), _today)
            .Select(t => new TrendPoint(
                Label: t.Day == _today ? "今天" : WeekdayNames[((int)t.Day.DayOfWeek + 6) % 7],
                Value: t.Total,
                Tip: $"{t.Day.Month}/{t.Day.Day} 周{WeekdayNames[((int)t.Day.DayOfWeek + 6) % 7]} · {DurationOrNone(t.Total)}",
                IsCurrent: t.Day == _today))
            .ToList();
    }

    private void BuildWeeklyChart()
    {
        WeeklyChart.Points = _history.WeeklyTotals(8, _today)
            .Select((week, index) => new TrendPoint(
                Label: $"{week.WeekStart.Month}/{week.WeekStart.Day}",
                Value: week.Total,
                Tip: $"{week.WeekStart.Month}/{week.WeekStart.Day}–{week.WeekStart.AddDays(6).Month}/{week.WeekStart.AddDays(6).Day} · {DurationOrNone(week.Total)}",
                IsCurrent: index == 7))
            .ToList();
    }

    private static string DurationOrNone(TimeSpan value) =>
        value > TimeSpan.Zero ? FocusHistory.FormatDuration(value) : "无专注记录";

    // ---- 卡片与脚注（随筛选重算） ----

    private StatsRange SelectedRange =>
        Range30.IsChecked == true ? StatsRange.Last30Days :
        Range7.IsChecked == true ? StatsRange.Last7Days : StatsRange.All;

    private void RefreshCards()
    {
        var start = _history.RangeStart(SelectedRange, _today);
        var days = Math.Max(1, _today.DayNumber - start.DayNumber + 1);
        var totals = _history.DailyTotals(start, _today);
        var total = totals.Aggregate(TimeSpan.Zero, (acc, t) => acc + t.Total);
        var peak = totals.Count == 0 ? TimeSpan.Zero : totals.Max(t => t.Total);

        CardTotal.Text = FocusHistory.FormatDuration(total);
        CardDaily.Text = FocusHistory.FormatDuration(TimeSpan.FromTicks(total.Ticks / days)) + "/天";
        CardPeak.Text = FocusHistory.FormatDuration(peak);
        CardActive.Text = _history.ActiveDayCount(start, _today) + " 天";
        CardLongest.Text = _history.LongestStreak(start, _today) + " 天";
        CardCurrent.Text = _history.CurrentStreak(_today) + " 天";

        // 趣味脚注：2 小时一部电影，纯装饰换算（方案 §5.4）
        if (_history.Sessions.Count == 0)
        {
            FooterText.Text = "还没有专注记录，完成第一次专注后这里会亮起来";
        }
        else if (total >= TimeSpan.FromHours(2))
        {
            FooterText.Text = $"相当于看完 {(int)(total.TotalHours / 2)} 部电影";
        }
        else
        {
            FooterText.Text = $"还差 {FocusHistory.FormatDuration(TimeSpan.FromHours(2) - total)} 凑满一部电影";
        }
    }

    // ---- 交互 ----

    // XAML 里 IsChecked="True" 在解析中就会触发 Checked，此时卡片/图表尚未创建；
    // 初始值由构造函数统一装配，这里的刷新只服务用户点击。
    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }
        RefreshCards();
    }

    private void ChartMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }
        var mode = ModeLine.IsChecked == true ? TrendChartMode.Line : TrendChartMode.Bars;
        DailyChart.Mode = mode;
        WeeklyChart.Mode = mode;
    }

    private void BackfillButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackfillDialog(_history) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // 补记只追加历史记录，页面其余部分仍是打开时的快照，整体重算一遍
            BuildHeatmap();
            BuildDailyChart();
            BuildWeeklyChart();
            RefreshCards();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
