using System.Security.Principal;
using System.Text.RegularExpressions;

namespace FocusGuard.Core;

public sealed record LegacyAclFinding(string FilePath, string Detail);

/// <summary>
/// 检测 <b>v1.0 及更早版本</b>用 <c>icacls /deny</c> 施加的拒绝规则残留。
///
/// 为什么必须单独报告而不是顺手清掉：那些版本<b>没有保存原始 ACL</b>，而移除只能靠
/// <c>/remove:d</c> —— 它会删掉该用户在这个文件上的全部拒绝规则。在不知道原状的前提下静默执行
/// 并宣称"已恢复原状"是不诚实的：正确做法是检测 + 明确提示 + 由用户决定。
/// </summary>
public static class LegacyAclCheck
{
    private static readonly Regex AcePattern = new(@"\([AD];[^)]*\)", RegexOptions.Compiled);

    public static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<LegacyAclFinding> Inspect(IEnumerable<string> filePaths)
    {
        var sid = CurrentUserSid();
        if (sid is null)
        {
            return Array.Empty<LegacyAclFinding>();
        }

        var findings = new List<LegacyAclFinding>();
        foreach (var path in filePaths
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dacl = AclReader.ReadDacl(path);
            if (dacl is null)
            {
                continue;
            }

            var denyCount = CountDenyAcesForSid(dacl, sid);
            if (denyCount > 0)
            {
                findings.Add(new LegacyAclFinding(path,
                    $"存在 {denyCount} 条针对当前用户的拒绝规则，可能来自 FocusGuard 旧版本（旧版本未保存原始 ACL，无法精确还原）。" +
                    $"如确认需要移除，请手工执行：icacls \"{path}\" /remove:d {Environment.UserName}"));
            }
        }

        return findings;
    }

    internal static int CountDenyAcesForSid(string dacl, string sid)
    {
        // 同一个账户在 SDDL 里可能有两种写法：
        //   - 完整 SID 字符串（普通账户，如本地标准管理员）
        //   - 已知别名 —— 当账户是内置 Administrator（RID-500）时，icacls /save 会写成 LA，
        //     CI 的 runner 恰好就是这种账户（实测：S-1-5-21-...-500 在 /save 输出里是 (D;;...;;;LA)）
        var identities = new List<string> { sid };
        if (sid.EndsWith("-500", StringComparison.Ordinal))
        {
            identities.Add("LA");
        }

        var count = 0;
        foreach (Match match in AcePattern.Matches(dacl))
        {
            var ace = match.Value;
            if (ace.StartsWith("(D;", StringComparison.OrdinalIgnoreCase)
                && identities.Any(i => ace.Contains(i, StringComparison.OrdinalIgnoreCase)))
            {
                count++;
            }
        }
        return count;
    }
}
