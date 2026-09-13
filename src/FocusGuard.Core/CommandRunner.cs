using System.Diagnostics;

namespace FocusGuard.Core;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>同步执行外部命令。只依赖退出码做判断（工具输出在中文系统上是 GBK，不作为业务依据）。</summary>
public static class CommandRunner
{
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

    public static CommandResult Run(string fileName, params string[] arguments)
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

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CommandResult(process.ExitCode, stdout, stderr);
    }
}
