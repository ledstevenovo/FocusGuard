using System.Threading;
using System.Windows;
using FocusGuard.Core;

namespace FocusGuard.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\FocusGuard.SingleInstance";

    /// <summary>刻意持有到进程结束，保证单实例判断覆盖整个生命周期。</summary>
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 只读自检不需要独占，允许与应用同时运行
        if (HasFlag(e.Args, "--diag"))
        {
            Diagnostics.WriteReport();
            Shutdown(0);
            return;
        }

        // 其余模式都会修改系统状态：先拿单实例锁，避免两个进程同时改 hosts / 状态文件
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "FocusGuard 已经在运行了。\r\n请使用已经打开的窗口；如果是残留进程，请先在任务管理器里结束它。",
                "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (HasFlag(e.Args, "--unlock"))
        {
            RunManualUnlock(ValueAfter(e.Args, "--unlock"));
            Shutdown(0);
            return;
        }

        if (HasFlag(e.Args, "--remove-legacy-deny"))
        {
            RunLegacyDenyRemoval();
            Shutdown(0);
            return;
        }

        if (HasFlag(e.Args, "--discard-record"))
        {
            RunDiscardRecord();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log("未处理异常: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "FocusGuard 出错",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow().Show();
    }

    /// <summary>命令行兜底：不看配置，把能探测到的残留限制全部清掉。</summary>
    private static void RunManualUnlock(string? directory)
    {
        var config = FocusConfig.LoadOrCreate(AppPaths.ConfigPath);
        var controller = new FocusController(config, AppPaths.DataDirectory);

        var report = new List<string>();
        var result = controller.Stop();
        report.Add("解除残留限制：" + (result.Success ? "成功" : "失败"));
        if (!result.Success)
        {
            report.Add(result.FailureText);
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            var extra = controller.UnlockInDirectory(directory);
            report.Add($"按指定目录恢复（{directory}）：" + (extra.Success ? "成功" : "失败"));
            if (!extra.Success)
            {
                report.Add(extra.FailureText);
            }
            result = new OperationResult(result.Success && extra.Success, result.Steps.Concat(extra.Steps).ToList());
        }

        report.Add(string.Empty);
        report.AddRange(controller.EnforcementSummary());

        if (result.Warnings.Any())
        {
            report.Add(string.Empty);
            report.Add("提示：");
            report.Add(result.WarningText);
        }

        AppPaths.Log("手动解除：" + (result.Success ? "成功" : "失败"));

        try
        {
            MessageBox.Show(string.Join("\r\n", report), "FocusGuard --unlock", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch
        {
            // 无交互环境（脚本调用）时忽略弹窗，结果已经写进日志
        }
    }

    /// <summary>
    /// 显式移除旧版本留下的 icacls 拒绝规则。旧版本没保存原始 ACL，无法精确还原，
    /// 因此这一步只在用户明确要求时执行。
    /// </summary>
    private static void RunLegacyDenyRemoval()
    {
        var config = FocusConfig.LoadOrCreate(AppPaths.ConfigPath);
        var controller = new FocusController(config, AppPaths.DataDirectory);
        controller.RefreshLegacyAclFindings();

        var result = controller.RemoveLegacyDenyRules();
        var report = new List<string>
        {
            "移除旧版拒绝规则：" + (result.Success ? "完成" : "失败"),
        };
        report.AddRange(result.Steps.Select(s => "· " + s.Name + "：" + (s.Detail ?? string.Empty)));
        report.Add(string.Empty);
        report.Add("提示：该操作会删除当前用户在这些文件上的全部拒绝规则。");

        AppPaths.Log("移除旧版拒绝规则：" + (result.Success ? "完成" : "失败"));

        try
        {
            MessageBox.Show(string.Join("\r\n", report), "FocusGuard --remove-legacy-deny",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch
        {
            // 无交互环境时忽略
        }
    }

    /// <summary>
    /// 显式放弃一条未能定位的记录线索。存在的意义：跨目录恢复时同名不代表同一个文件，
    /// 需要用户核对后手工确认，否则这条线索会一直挡着新的开始。
    /// </summary>
    private static void RunDiscardRecord()
    {
        var config = FocusConfig.LoadOrCreate(AppPaths.ConfigPath);
        var controller = new FocusController(config, AppPaths.DataDirectory);
        var pending = controller.UnlocatedRecord;

        try
        {
            if (pending is null)
            {
                MessageBox.Show("当前没有未能定位的记录。", "FocusGuard --discard-record",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                "将要放弃这条未能定位的记录：\r\n\r\n    " + pending +
                "\r\n\r\n放弃后 FocusGuard 不再追踪它。若该文件其实仍处于锁定状态，" +
                "需要你手工把文件名改回。\r\n\r\n确定放弃吗？",
                "FocusGuard --discard-record", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var ok = controller.DiscardUnlocatedRecord("命令行确认放弃");
            AppPaths.Log("命令行放弃记录：" + (ok ? "成功" : "失败"));

            MessageBox.Show(
                ok
                    ? "已放弃该记录。"
                    : "放弃失败：状态文件写入失败，记录仍然保留。\r\n请确认数据目录可写后重试。",
                "FocusGuard --discard-record", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch
        {
            // 无交互环境（脚本调用）时忽略弹窗，结果已写进日志
        }
    }

    private static bool HasFlag(IEnumerable<string> args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
            }
        }
        return null;
    }
}
