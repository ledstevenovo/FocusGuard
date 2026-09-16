using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FocusGuard.Core;

namespace FocusGuard.App;

public partial class MainWindow : Window
{
    private static readonly Brush IdleBg = Hex("#FAFAFA");
    private static readonly Brush IdleBorder = Hex("#E5E7EB");
    private static readonly Brush IdleButtonBg = Hex("#185FA5");
    private static readonly Brush FocusBg = Hex("#0C447C");
    private static readonly Brush FocusBorder = Hex("#0C447C");
    private static readonly Brush AccentText = Hex("#85B7EB");
    private static readonly Brush MutedText = Hex("#9CA3AF");

    private readonly FocusController _controller;
    private readonly DispatcherTimer _timer;

    /// <summary>从点击处理一开始就置位，覆盖"结束进程 → 锁定/解除"的整段过程。</summary>
    private bool _busy;

    private string? _pendingNotice;

    public MainWindow()
    {
        InitializeComponent();

        var config = FocusConfig.LoadOrCreate(AppPaths.ConfigPath);
        _controller = new FocusController(config, AppPaths.DataDirectory);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AdminCheck.IsElevated())
        {
            MessageBox.Show(this,
                "FocusGuard 需要管理员权限才能修改 hosts 文件。\r\n请关闭后以管理员身份重新运行。",
                "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_controller.NeedsAttention)
        {
            _pendingNotice = _controller.UnresolvedItems.Count > 0
                ? "上次解除未完成，请重试"
                : "检测到限制仍在生效（上次可能异常退出）";
        }
        else if (_controller.UnlocatedRecord is not null)
        {
            _pendingNotice = "有一条记录未能定位，详见下方";
        }

        ApplyVisualState();
        _timer.Start();

        // 旧版拒绝规则的检测要跑 icacls，放到后台，避免开窗卡顿
        await Task.Run(() => _controller.RefreshLegacyAclFindings());
        ApplyVisualState();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // 互斥范围从点击这一瞬间开始：下面弹窗询问、结束进程的整段时间窗口也不能再点第二次
        BeginBusy("正在准备…");
        try
        {
            if (_controller.IsLockedNow)
            {
                ActionButton.Content = "正在解除…";
                Report(await Task.Run(() => _controller.Stop()));
                return;
            }

            // 有未能定位的旧记录时绝不静默覆盖：让用户决定怎么处理
            if (_controller.UnlocatedRecord is not null)
            {
                var answer = MessageBox.Show(this,
                    "上一次有一条记录未能定位：\r\n\r\n    " + _controller.UnlocatedRecord +
                    "\r\n\r\n【是】再试一次恢复\r\n【否】放弃这条记录并开始新会话\r\n【取消】什么都不做",
                    "FocusGuard", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (answer == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (answer == MessageBoxResult.Yes)
                {
                    ActionButton.Content = "正在重试恢复…";
                    Report(await Task.Run(() => _controller.Stop()));
                    return;
                }

                if (!_controller.DiscardUnlocatedRecord("界面选择放弃"))
                {
                    MessageBox.Show(this,
                        "放弃记录失败：状态文件写入失败，这条记录仍然保留。\r\n" +
                        "请确认数据目录可写后重试（也可用 --discard-record 处理）。",
                        "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            var running = _controller.RunningBlockedProcesses();
            if (running.Count > 0)
            {
                ActionButton.Content = "等待确认…";
                var answer = MessageBox.Show(this,
                    "检测到以下程序正在运行：\r\n\r\n    " + string.Join("、", running) +
                    "\r\n\r\n开始专注需要先结束它们，是否继续？",
                    "FocusGuard", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                ActionButton.Content = "正在结束进程…";
                await Task.Run(() => _controller.KillBlockedProcesses());
            }

            ActionButton.Content = "正在锁定…";
            Report(await Task.Run(() => _controller.Start()));
        }
        catch (Exception ex)
        {
            AppPaths.Log("操作异常: " + ex);
            _pendingNotice = ex.Message;
            MessageBox.Show(this, ex.Message, "FocusGuard 操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private void BeginBusy(string text)
    {
        _busy = true;
        _pendingNotice = null;
        ActionButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        // 期间会弹以本窗口为 owner 的 MessageBox，而 owner 最小化时弹窗可能被压在后面看不到
        MinimizeButton.IsEnabled = false;
        ActionButton.Content = text;
    }

    private void EndBusy()
    {
        _busy = false;
        ActionButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        MinimizeButton.IsEnabled = true;
        ApplyVisualState();
    }

    private void Report(OperationResult result)
    {
        if (!result.Success)
        {
            // 解除失败时绝不显示"专注结束"：界面保持锁定态，把没恢复成功的项摆出来
            _pendingNotice = _controller.UnresolvedItems.Count > 0
                ? "部分解除失败：" + string.Join("、", _controller.UnresolvedItems)
                : "有步骤失败，限制可能仍然生效";

            MessageBox.Show(this,
                "以下步骤失败：\r\n\r\n" + result.FailureText +
                "\r\n\r\n界面会保持锁定状态，处理完原因后可以直接重试。",
                "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (result.Warnings.Any())
        {
            _pendingNotice = "提示：" + result.WarningText;
        }
    }

    private void OnTick()
    {
        UpdateElapsed();

        // 底部状态每秒跟着真实探测刷新：游戏更新重新生成 fm.exe 后能立刻看到冲突
        if (_busy)
        {
            return;
        }

        var text = BuildHintText();
        if (!string.Equals(HintText.Text, text, StringComparison.Ordinal))
        {
            HintText.Text = text;
        }
    }

    private string BuildHintText()
    {
        var lines = new List<string>();
        if (_pendingNotice is not null)
        {
            lines.Add(_pendingNotice);
        }
        lines.AddRange(_controller.EnforcementSummary());
        return string.Join("\n", lines);
    }

    private void ApplyVisualState()
    {
        var locked = _controller.IsLockedNow;

        Root.Background = locked ? FocusBg : IdleBg;
        Root.BorderBrush = locked ? FocusBorder : IdleBorder;

        TitleText.Text = locked ? "FocusGuard · 专注中" : "FocusGuard";
        TitleText.Foreground = locked ? AccentText : MutedText;

        ActionButton.Background = locked ? Brushes.White : IdleButtonBg;
        ActionButton.Foreground = locked ? FocusBg : Brushes.White;
        ActionButton.Content = locked ? "结束专注" : "开始专注";

        ElapsedText.Foreground = locked ? AccentText : MutedText;
        ElapsedText.Visibility = _controller.Config.ShowElapsed ? Visibility.Visible : Visibility.Collapsed;

        HintText.Foreground = locked ? AccentText : Hex("#B4B2A9");
        HintText.Text = BuildHintText();

        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        if (!_controller.IsLockedNow)
        {
            ElapsedText.Text = string.Empty;
            return;
        }

        var started = _controller.StartedAt;
        if (started is null)
        {
            ElapsedText.Text = "限制生效中";
            return;
        }

        var span = DateTimeOffset.Now - started.Value;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        ElapsedText.Text = $"已专注 {(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 正在锁定 / 解除（含结束进程阶段）时不允许关闭，否则流程会交错留下部分限制
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "正在执行操作，请等它完成后再关闭。",
                "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_controller.IsLockedNow && _controller.UnlocatedRecord is null)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            "当前正在专注中。\r\n关闭 FocusGuard 会结束专注并解除全部限制。\r\n\r\n确定要关闭吗？",
            "FocusGuard", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        var result = _controller.Stop();
        if (!result.Success)
        {
            // 没清干净就不退出，把窗口留给用户重试
            e.Cancel = true;
            _pendingNotice = "关闭前解除失败，仍未退出";
            ApplyVisualState();
            MessageBox.Show(this,
                "解除失败，为避免留下残留，本次未退出：\r\n\r\n" + result.FailureText,
                "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 最小化到任务栏。不影响任何强制手段（hosts / 改名 / 结束进程都在窗口之外生效），
    /// 因此专注中照常可用；操作进行中会被禁用，理由见 BeginBusy。
    /// </summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // —— 任务栏点击最小化 ——
    // 无边框窗口（WindowStyle=None + NoResize）没有 WS_MINIMIZEBOX，外壳会把本窗口
    // 当成“不可最小化”（与对话框同款判定），点击任务栏按钮只剩激活、不发 SC_MINIMIZE。
    // 补上该样式后，任务栏点击经 SC_MINIMIZE 由 WPF 转成 WindowState.Minimized，
    // 与上面的最小化键汇合在同一条原生命令路径。
    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlStyle, GetWindowLong(hwnd, GwlStyle) | WsMinimizeBox);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    /// <summary>busy 时拦下任务栏 / 系统菜单发来的 SC_MINIMIZE，与 BeginBusy 禁用最小化键同一规则。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 钩子收到的是所有消息：非 WM_SYSCOMMAND 消息的 wParam 可能是 64 位指针值，
        // 必须用截断转换——ToInt32() 会抛 OverflowException（实测弹过错）。
        // 命令值只在 WM_SYSCOMMAND 下有意义且必在 int 范围内，截断不影响判定；
        // 其余消息被策略函数按 msg 短路丢弃。
        if (WindowCommandPolicy.ShouldSwallowMinimize(msg, unchecked((int)wParam), _busy))
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
