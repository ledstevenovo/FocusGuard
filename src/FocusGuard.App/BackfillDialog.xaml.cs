using System.Windows;
using System.Windows.Input;
using FocusGuard.Core;

namespace FocusGuard.App;

/// <summary>
/// 时长补记弹窗（专注统计方案 §5.6）：漏开专注的时长手动补进当天。
/// 这里只管输入与校验文案，写盘统一走 <see cref="FocusHistory.TryBackfill"/>；
/// 校验不通过或写入失败都不关窗，原因留在红字里。
/// </summary>
public partial class BackfillDialog : Window
{
    private readonly FocusHistory _history;
    private readonly DateOnly _today;

    public BackfillDialog(FocusHistory history)
    {
        InitializeComponent();
        _history = history;
        _today = DateOnly.FromDateTime(DateTime.Now);

        var today = _history.TotalOn(_today);
        TodayTotalText.Text = today > TimeSpan.Zero
            ? "今天已记录 " + FocusHistory.FormatDuration(today)
            : "今天还没有记录";

        Loaded += (_, _) =>
        {
            HoursBox.Focus();
            HoursBox.SelectAll();
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void TryConfirm()
    {
        var hours = Parse(HoursBox.Text, "小时");
        var minutes = Parse(MinutesBox.Text, "分钟");
        if (hours is null || minutes is null)
        {
            return;
        }

        var duration = TimeSpan.FromMinutes(hours.Value * 60 + minutes.Value);
        if (duration <= TimeSpan.Zero)
        {
            ShowError("请先填入要补记的时长。");
            return;
        }

        if (duration > FocusHistory.MaxBackfillDuration)
        {
            ShowError("一天的专注不可能超过 24 小时，请改小一点。");
            return;
        }

        if (_history.TryBackfill(duration, DateTimeOffset.Now))
        {
            DialogResult = true;
        }
        else
        {
            ShowError("写入失败，请确认数据目录可写后再试。");
        }
    }

    /// <summary>空输入按 0 计；非数字返回 null 并把红字摆出来。</summary>
    private int? Parse(string text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (!int.TryParse(text.Trim(), out var value))
        {
            ShowError(fieldName + "请填整数。");
            return null;
        }

        return value;
    }

    private void ShowError(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
}
