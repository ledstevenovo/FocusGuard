namespace FocusGuard.Core;

/// <summary>
/// 窗口系统命令层面的最小化拦截决策（纯逻辑，供 WndProc 与单元测试共用）。
/// 背景：busy 期间标题栏最小化键被禁用（BeginBusy：owner 被最小化时弹窗可能被压住看不见），
/// 而任务栏点击产生的 SC_MINIMIZE 不经过那个按钮，须用同一规则在 WndProc 里拦下。
/// </summary>
public static class WindowCommandPolicy
{
    public const int WmSysCommand = 0x0112;
    public const int ScMinimize = 0xF020;

    /// <summary>仅 busy 时拦下 SC_MINIMIZE（低 4 位是系统内部标志，按 0xFFF0 掩码比较），其余一律放行。</summary>
    public static bool ShouldSwallowMinimize(int msg, int wParam, bool busy) =>
        busy && msg == WmSysCommand && (wParam & 0xFFF0) == ScMinimize;
}
