using System.Text;

namespace FocusGuard.Core;

/// <summary>
/// 读取文件的真实安全描述符。用 <c>icacls /save</c> 而不是 <c>icacls</c> 的显示输出：
/// 导出的 DACL 是本地化无关的文本（ACE 类型用 A / D 表示、SID 是纯 ASCII），
/// 在中文系统上不会因为"允许/拒绝"的译法变化而失效。
/// </summary>
public static class AclReader
{
    /// <summary>返回完整 DACL 文本（形如 <c>D:AI(A;OICIID;0x1301bf;;;S-1-5-21-...)</c>）；读不到返回 null。</summary>
    public static string? ReadDacl(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var temp = Path.Combine(Path.GetTempPath(), "focusguard-acl-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var save = CommandRunner.Run("icacls", filePath, "/save", temp);
            if (!save.Ok || !File.Exists(temp))
            {
                return null;
            }
            return ParseDacl(File.ReadAllBytes(temp));
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>icacls /save 写出的是 UTF-16LE 文本，其中以 D: 开头的那一行就是 DACL。</summary>
    internal static string? ParseDacl(byte[] savedFileBytes)
    {
        if (savedFileBytes.Length == 0)
        {
            return null;
        }

        // UTF-16LE 的 ASCII 内容每隔一个字节是 0x00，用这个特征判断编码
        var text = savedFileBytes.Length > 1 && savedFileBytes[1] == 0
            ? Encoding.Unicode.GetString(savedFileBytes)
            : Encoding.UTF8.GetString(savedFileBytes);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.StartsWith("D:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }
}
