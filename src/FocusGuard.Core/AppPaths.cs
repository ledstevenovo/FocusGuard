namespace FocusGuard.Core;

/// <summary>数据目录与日志。日志失败永不抛异常。</summary>
public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusGuard");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string StatePath => Path.Combine(DataDirectory, "state.json");

    public static string LogPath => Path.Combine(DataDirectory, "focusguard.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n");
        }
        catch
        {
            // 日志写不进去也不能影响主流程
        }
    }
}
