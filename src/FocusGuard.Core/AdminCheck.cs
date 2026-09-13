namespace FocusGuard.Core;

/// <summary>
/// 管理员权限检测。走 <c>whoami /groups</c> 而不是 WindowsIdentity API：
/// 输出里的 SID（S-1-16-12288 = 高完整性级别）是纯 ASCII，不受中文系统 GBK 输出影响。
/// </summary>
public static class AdminCheck
{
    private const string HighIntegritySid = "S-1-16-12288";

    public static bool IsElevated()
    {
        try
        {
            var result = CommandRunner.Run("whoami", "/groups");
            return result.Ok && result.StdOut.Contains(HighIntegritySid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
