using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FocusGuard.Core;

namespace FocusGuard.App;

/// <summary>无副作用的自检报告。<c>FocusGuard.exe --diag</c> 只读，不改任何系统配置。</summary>
internal static class Diagnostics
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static string ReportPath => Path.Combine(AppPaths.DataDirectory, "diag-report.txt");

    public static void WriteReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FocusGuard 自检报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("-----------------------------------------------------------");

        var config = FocusConfig.LoadOrCreate(AppPaths.ConfigPath);
        var controller = new FocusController(config, AppPaths.DataDirectory);
        controller.RefreshLegacyAclFindings();

        foreach (var line in controller.Inspect())
        {
            sb.AppendLine(line);
        }

        sb.AppendLine();
        sb.AppendLine("真实强制状态：");
        foreach (var line in controller.EnforcementSummary())
        {
            sb.AppendLine("    " + line);
        }

        sb.AppendLine();
        sb.AppendLine($"配置路径：{AppPaths.ConfigPath}");
        sb.AppendLine($"日志路径：{AppPaths.LogPath}");
        sb.AppendLine($"状态路径：{AppPaths.StatePath}");
        sb.AppendLine($"屏蔽进程：{string.Join(", ", config.BlockedProcessNames)}");
        sb.AppendLine($"补充恢复目录：{(config.RecoveryDirectories.Count == 0 ? "无" : string.Join(", ", config.RecoveryDirectories))}");
        sb.AppendLine($"屏蔽域名（{HostsFile.Normalize(config.BlockedDomains).Count} 个）：");
        foreach (var domain in HostsFile.Normalize(config.BlockedDomains))
        {
            sb.AppendLine("    " + domain);
        }

        sb.AppendLine();
        sb.AppendLine("常用命令：");
        sb.AppendLine("    FocusGuard.exe --unlock              解除可探测到的全部限制");
        sb.AppendLine("    FocusGuard.exe --unlock <目录>       在指定目录里查找并还原被改名的文件");
        sb.AppendLine("    FocusGuard.exe --remove-legacy-deny  移除旧版本留下的 icacls 拒绝规则");

        var text = sb.ToString();

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(ReportPath, text, Encoding.UTF8);
        }
        catch
        {
            // 报告写不出来也不影响
        }

        // 从终端调用时顺便打印；从资源管理器双击则忽略
        try
        {
            if (AttachConsole(-1))
            {
                var writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(writer);
                Console.Write(text);
            }
        }
        catch
        {
            // 没有可附着的控制台，报告文件已生成
        }
    }
}
