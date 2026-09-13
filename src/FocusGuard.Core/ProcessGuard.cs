using System.Diagnostics;

namespace FocusGuard.Core;

/// <summary>
/// 只做「开始专注时把已在运行的目标进程关掉」这一件事。
/// 不做常驻监控 —— 因为 fm.exe 的 Deny ACE 已经保证它启动不了。
/// </summary>
public static class ProcessGuard
{
    public static IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
    {
        var found = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name.Trim());
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    found.Add(name.Trim());
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }
        return found;
    }

    /// <summary>返回成功结束的进程数。任何一个进程失败都不抛异常。</summary>
    public static int KillAll(IEnumerable<string> processNames)
    {
        var killed = 0;
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name.Trim());
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    killed++;
                }
                catch
                {
                    // 进程可能已经退出，或权限不足；忽略
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return killed;
    }
}
