using System.Text.Encodings.Web;
using System.Text.Json;

namespace FocusGuard.Core;

public static class FocusPhases
{
    public const string Idle = "Idle";
    public const string Locking = "Locking";
    public const string Locked = "Locked";
    public const string Unlocking = "Unlocking";
    public const string UnlockIncomplete = "UnlockIncomplete";
}

/// <summary>
/// 落盘的状态。三个关键设计：
/// <list type="number">
/// <item><b>记录实际改了什么</b>：解锁以这些记录为第一依据，而不是当前配置 —— 否则用户改了
/// 目标路径、关了锁定开关，或配置损坏退回默认值后就再也解不开原先那把锁。</item>
/// <item><b>先记意图再动作</b>：改名之前就把 <c>Phase=Locking</c> 和计划路径写进文件，
/// 这样即使在改名中途崩溃，下次启动仍有恢复线索。</item>
/// <item><b>失败项逐条保留</b>：不用单个布尔值表达失败，而是把没恢复成功的项列出来，
/// 界面据此显示"部分解除失败"并允许重试。</item>
/// </list>
/// </summary>
public sealed class FocusState
{
    public bool IsFocusing { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Locking / Locked / Unlocking / UnlockIncomplete / Idle。</summary>
    public string Phase { get; set; } = FocusPhases.Idle;

    public string? RecordedTargetPath { get; set; }

    public string? RecordedLockedPath { get; set; }

    public string? RecordedHostsPath { get; set; }

    public bool AppliedHostsBlock { get; set; }

    public bool AppliedTargetLock { get; set; }

    /// <summary>未能恢复成功的项。为空表示干净。</summary>
    public List<string> UnresolvedItems { get; set; } = new();

    public string? LastOperation { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static FocusState Load(string path) => TryLoad(path, out _);

    /// <summary><paramref name="usable"/> 为 false 表示文件不存在或无法解析 —— 此时"未发现残留"不能当结论。</summary>
    public static FocusState TryLoad(string path, out bool usable)
    {
        usable = false;
        try
        {
            if (!File.Exists(path))
            {
                return new FocusState();
            }

            var loaded = JsonSerializer.Deserialize<FocusState>(File.ReadAllText(path), JsonOptions);
            if (loaded is null)
            {
                return new FocusState();
            }

            loaded.Phase ??= FocusPhases.Idle;
            loaded.UnresolvedItems ??= new List<string>();
            usable = true;
            return loaded;
        }
        catch
        {
            // 状态文件损坏 → 当作新会话；残留仍会在 Stop 时被实际探测发现
            return new FocusState();
        }
    }

    /// <summary>
    /// 原子落盘：先写临时文件再整体替换，避免中途退出把已有记录写坏。
    /// 写入失败会抛异常 —— 调用方必须据此中止后续操作，绝不能"记不下来还照做"。
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
