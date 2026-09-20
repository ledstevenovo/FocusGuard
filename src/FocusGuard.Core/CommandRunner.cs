using System.Diagnostics;

namespace FocusGuard.Core;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// 同步执行外部命令。只依赖退出码做判断（工具输出在中文系统上是 GBK，不作为业务依据）。
/// 两条输出流并发异步读取（顺序同步读有经典死锁：stderr 写满 4KB 管道缓冲区而
/// stdout 尚未 EOF 时双向互等）；整体有超时，超时杀进程并按失败返回，绝不挂住调用方。
/// </summary>
public static class CommandRunner
{
    /// <summary>子进程整体超时。whoami/icacls/ipconfig 正常都是毫秒级返回，15 秒绰绰有余。</summary>
    private const int DefaultTimeoutMs = 15_000;

    /// <summary>
    /// 把命令名解析成可执行文件的绝对路径。
    /// 应用以管理员身份运行，而 CreateProcess 在 UseShellExecute=false 时的搜索顺序包含<b>当前目录</b>：
    /// 若允许裸名启动，攻击者只要能在启动目录放置同名 exe，就能借 UAC 批准获得管理员执行（提权劫持）。
    /// 因此裸名一律固定解析到系统目录，找不到就抛异常 —— 绝不落回 PATH / 当前目录搜索（fail-closed）。
    /// </summary>
    public static string Resolve(string fileName)
    {
        if (fileName.Contains('\\') || fileName.Contains('/'))
        {
            return fileName;   // 调用方显式给了路径，按原样使用
        }

        var exeName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".exe";
        var fullPath = Path.Combine(Environment.SystemDirectory, exeName);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                "未在系统目录找到工具 " + exeName + "（出于提权安全考虑，拒绝按 PATH 或当前目录搜索）：" + fullPath);
        }
        return fullPath;
    }

    public static CommandResult Run(string fileName, params string[] arguments) =>
        Run(fileName, DefaultTimeoutMs, arguments);

    /// <param name="timeoutMs">超时上限；超时即杀进程树并按失败（退出码 -1）返回。</param>
    public static CommandResult Run(string fileName, int timeoutMs, params string[] arguments)
    {
        var psi = new ProcessStartInfo(Resolve(fileName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动进程: " + fileName);

        // 两条流必须并发读取：先同步 ReadToEnd stdout 再读 stderr 的写法，
        // 在子进程 stderr 写满管道缓冲区而 stdout 尚未 EOF 时双方互等，永久死锁。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            // 超时即失败：杀掉整棵进程树，退出码固定 -1（Ok==false），绝不谎报成功。
            // 极端竞态（恰好在此刻自行退出）会被记为失败 —— 宁可误报失败，也不谎报成功。
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退出，忽略
            }

            process.WaitForExit();
            return new CommandResult(-1,
                stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult() +
                $"\r\n[FocusGuard] 命令超过 {timeoutMs}ms 未退出，已强制结束");
        }

        // 带超时的 WaitForExit 不等异步输出收尾，必须再无参等一次，否则可能读到不完整输出
        process.WaitForExit();
        return new CommandResult(
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }
}
