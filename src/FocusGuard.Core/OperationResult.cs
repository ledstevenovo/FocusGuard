namespace FocusGuard.Core;

public enum StepStatus
{
    /// <summary>成功执行。</summary>
    Ok,

    /// <summary>按配置或环境判断不需要执行（不是错误）。</summary>
    Skipped,

    /// <summary>执行了但结果不理想，不影响整体成功。</summary>
    Warning,

    /// <summary>失败，必须如实上报，不能当作成功。</summary>
    Failed,
}

public sealed record StepOutcome(string Name, StepStatus Status, string? Detail = null);

/// <summary>
/// 一次开始 / 结束操作的完整结果。
/// 存在的意义：<b>失败必须能被上报</b>，不能再出现"解除失败却显示成功"。
/// </summary>
public sealed record OperationResult(bool Success, IReadOnlyList<StepOutcome> Steps)
{
    public IEnumerable<StepOutcome> Failures => Steps.Where(s => s.Status == StepStatus.Failed);

    public IEnumerable<StepOutcome> Warnings => Steps.Where(s => s.Status == StepStatus.Warning);

    public string FailureText => string.Join("\r\n", Failures.Select(s => "· " + s.Name + "：" + s.Detail));

    public string WarningText => string.Join("\r\n", Warnings.Select(s => "· " + s.Name + "：" + s.Detail));

    /// <summary>一行摘要，用于界面副标题。</summary>
    public string Summary => string.Join(" · ", Steps.Select(s => s.Name + "：" + Describe(s.Status)));

    private static string Describe(StepStatus status) => status switch
    {
        StepStatus.Ok => "成功",
        StepStatus.Skipped => "已跳过",
        StepStatus.Warning => "注意",
        _ => "失败",
    };
}
