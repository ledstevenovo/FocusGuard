using System.Diagnostics;

namespace FocusGuard.Core;

/// <summary>
/// 只做「开始专注时把已在运行的目标进程关掉」这一件事。
/// 不做常驻监控 —— 因为 fm.exe 已被改名为 fm.exe.focusguard-locked
/// （<see cref="TargetLocker"/>），Windows 不允许运行不存在的程序。
/// </summary>
public static class ProcessGuard
{
    /// <summary>
    /// <see cref="Process.GetProcessesByName"/> 要的是"不含扩展名的可执行名"：
    /// 配置里写了 "fm.exe" 会静默匹配不到任何进程（实测返回空数组），检测与结束双双失效。
    /// 因此统一剥掉 .exe 后缀（大小写不限）；只剥 .exe，其他点号（如 my.tool）保持原样。
    /// </summary>
    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    /// <summary>
    /// 屏蔽进程的有效名单 = 用户显式名单 + 屏蔽 exe 列表的进程名（总是并入：
    /// 结束进程是"屏蔽程序"的基础动作，与是否启用文件锁定无关）
    /// + 目标锁定程序的进程名（<paramref name="lockTargetExe"/> 开启才并入：
    /// 锁定靠改名，文件被占用时改名必败，不先结束它锁定必失败）。
    /// 配置里只存用户显式写的名单，这一份在运行时派生，换目标后无需手工同步。
    /// </summary>
    public static IReadOnlyList<string> EffectiveBlockedNames(
        IEnumerable<string> processNames,
        IEnumerable<string>? blockedExePaths,
        string? targetExe,
        bool lockTargetExe)
    {
        var names = new List<string>();
        void Add(string name)
        {
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        foreach (var raw in processNames)
        {
            Add((raw ?? string.Empty).Trim());
        }

        foreach (var path in blockedExePaths ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                Add(NormalizeName(Path.GetFileName(path)));
            }
        }

        if (lockTargetExe && !string.IsNullOrWhiteSpace(targetExe))
        {
            Add(NormalizeName(Path.GetFileName(targetExe)));
        }

        return names;
    }

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
                processes = Process.GetProcessesByName(NormalizeName(name));
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

    /// <summary>
    /// 返回结束结果：<b>只有确认退出的才计入 ConfirmedKilled</b>；3 秒内未确认退出的
    /// 进程进 <see cref="ProcessKillResult.NotConfirmed"/> 如实上报，绝不谎报成功。
    /// 任何一个进程失败都不抛异常。
    /// </summary>
    public static ProcessKillResult KillAll(IEnumerable<string> processNames)
    {
        var killed = 0;
        var notConfirmed = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(NormalizeName(name));
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
                    if (process.WaitForExit(3000))
                    {
                        killed++;
                    }
                    else
                    {
                        // 超时不等于失败，但也绝不能当成成功：上报给调用方如实提示
                        notConfirmed.Add(NormalizeName(name) + "（PID " + process.Id + "，超时未确认退出）");
                    }
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
        return new ProcessKillResult(killed, notConfirmed);
    }
}

/// <summary>一次批量结束进程的结果。NotConfirmed 非空 = 有进程没能确认退出，调用方必须如实提示。</summary>
public sealed record ProcessKillResult(int ConfirmedKilled, IReadOnlyList<string> NotConfirmed);
