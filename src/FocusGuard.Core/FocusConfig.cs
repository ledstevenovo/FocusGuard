using System.Text.Encodings.Web;
using System.Text.Json;

namespace FocusGuard.Core;

/// <summary>用户可编辑的配置，默认值即开箱可用。</summary>
public sealed class FocusConfig
{
    public List<string> BlockedDomains { get; set; } = DefaultDomains();

    public List<string> BlockedProcessNames { get; set; } = new() { "fm", "Berkelium" };

    public string TargetExe { get; set; } = @"D:\11Player-FM2012_1204\fm.exe";

    /// <summary>false 时只写 hosts，完全不动那个程序文件。</summary>
    public bool LockTargetExe { get; set; } = true;

    /// <summary>
    /// 补充的恢复搜索目录。正常情况下不需要它 —— 恢复以 state 里记录的原路径为准；
    /// 只有在状态文件丢失、且目标路径又被改过时才用得上（配合 <c>--unlock &lt;目录&gt;</c>）。
    /// </summary>
    public List<string> RecoveryDirectories { get; set; } = new();

    public bool ShowElapsed { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<string> DefaultDomains() => new()
    {
        "zhihu.com", "www.zhihu.com", "zhuanlan.zhihu.com", "m.zhihu.com",
        "hupu.com", "www.hupu.com", "m.hupu.com", "bbs.hupu.com",
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be",
        "bilibili.com", "www.bilibili.com", "m.bilibili.com", "space.bilibili.com",
        "t.bilibili.com", "live.bilibili.com", "search.bilibili.com", "b23.tv",
    };

    public static FocusConfig LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<FocusConfig>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不要让程序起不来
        }

        var fresh = new FocusConfig();
        try { fresh.Save(path); } catch { }
        return fresh;
    }

    public void Normalize()
    {
        if (BlockedDomains is null || BlockedDomains.Count == 0)
        {
            BlockedDomains = DefaultDomains();
        }
        if (BlockedProcessNames is null || BlockedProcessNames.Count == 0)
        {
            BlockedProcessNames = new List<string> { "fm" };
        }
        if (string.IsNullOrWhiteSpace(TargetExe))
        {
            TargetExe = @"D:\11Player-FM2012_1204\fm.exe";
        }
        if (RecoveryDirectories is null)
        {
            RecoveryDirectories = new List<string>();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
