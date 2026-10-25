using System.Text.Encodings.Web;
using System.Text.Json;

namespace FocusGuard.Core;

/// <summary>用户可编辑的配置，默认值即开箱可用。</summary>
public sealed class FocusConfig
{
    public List<string> BlockedDomains { get; set; } = DefaultDomains();

    public List<string> BlockedProcessNames { get; set; } = new() { "fm", "Berkelium" };

    /// <summary>
    /// 设置页里选择的 exe 全路径：开始专注时结束其进程，并在专注期间改名锁定文件防止再次启动。
    /// null（JSON 里没有该键）= 从未用过新版本；空列表 = 用户显式清空，二者语义不同，
    /// 因此这里保持可空，由 <see cref="LoadOrCreate"/> 完成旧配置迁移后再补成空列表。
    /// </summary>
    public List<string>? BlockedExePaths { get; set; }

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
                    SeedLegacyTargetIntoList(loaded);
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
        SeedLegacyTargetIntoList(fresh);
        try { fresh.Save(path); } catch { }
        return fresh;
    }

    /// <summary>
    /// 旧配置迁移：从未用过"屏蔽程序列表"（键缺失，BlockedExePaths 为 null）且旧单目标文件
    /// 真实存在时，把它并入列表 —— 升级前后锁定行为完全一致。
    /// 只要键存在（哪怕是空列表）就是新版本写过的：空列表 = 用户显式清空，绝不回填。
    /// </summary>
    private static void SeedLegacyTargetIntoList(FocusConfig config)
    {
        if (config.BlockedExePaths is not null)
        {
            return;
        }
        if (!config.LockTargetExe || string.IsNullOrWhiteSpace(config.TargetExe))
        {
            return;
        }
        if (File.Exists(config.TargetExe))
        {
            config.BlockedExePaths = new List<string> { config.TargetExe };
        }
    }

    public void Normalize()
    {
        // 只在字段缺失（JSON 里没有该键或显式 null）时才回填默认值；
        // 空列表是设置页里"用户删光了"的合法选择，不得重启后被默认值覆盖。
        if (BlockedDomains is null)
        {
            BlockedDomains = DefaultDomains();
        }
        if (BlockedProcessNames is null)
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
        // 迁移（SeedLegacyTargetIntoList）之后补齐空列表，之后一律非空
        BlockedExePaths ??= new List<string>();
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
