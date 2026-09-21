using System.Text;

namespace FocusGuard.Core;

/// <summary>
/// 数据目录与日志。日志失败永不抛异常；日志有界 —— 超过上限时只保留尾部，
/// 不做无限累积（系统事件日志同理，反正真正需要排障的永远是最近的一段）。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusGuard");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string StatePath => Path.Combine(DataDirectory, "state.json");

    public static string LogPath => Path.Combine(DataDirectory, "focusguard.log");

    private const long MaxLogBytes = 1024 * 1024;      // 1 MB 触发截断
    private const long KeepTailBytes = 512 * 1024;     // 保留最近 512 KB

    /// <summary>
    /// 异常脱敏摘要：只保留类型名与 Message，绝不写堆栈。
    /// 堆栈和内部异常链会把实现细节与本机目录结构写进日志文件（S1）。
    /// </summary>
    public static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n");
            PruneLogIfOversized();
        }
        catch
        {
            // 日志写不进去也不能影响主流程
        }
    }

    /// <summary>超限就只留尾部并插一行说明，避免多年后日志文件失控。</summary>
    private static void PruneLogIfOversized()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length <= MaxLogBytes)
        {
            return;
        }

        var note = Encoding.UTF8.GetBytes(
            $"[FocusGuard] 日志超过 {MaxLogBytes / 1024}KB，已截断并仅保留尾部\r\n");

        byte[] tail;
        using (var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var start = reader.Length - KeepTailBytes;
            reader.Position = start;
            tail = new byte[KeepTailBytes];
            var offset = 0;
            while (offset < tail.Length)
            {
                var read = reader.Read(tail, offset, tail.Length - offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            if (offset < tail.Length)
            {
                Array.Resize(ref tail, offset);
            }
        }

        using (var writer = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.Write(note, 0, note.Length);
            writer.Write(tail, 0, tail.Length);
        }
    }
}
