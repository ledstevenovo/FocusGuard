using System.Globalization;
using System.Text;

namespace FocusGuard.Core;

/// <summary>
/// hosts 文件内容的纯字节操作：只在文件里追加/删除一段被标记包裹的区块，
/// 其余字节一个都不碰。因为是字节级处理，所以原文件是 ANSI / GBK / UTF-8 都不会被破坏。
/// 无 IO、无副作用，可直接单元测试。
/// </summary>
public static class HostsFile
{
    public const string BeginMarker = "# >>> FocusGuard BEGIN >>>";
    public const string EndMarker = "# <<< FocusGuard END <<<";
    public const string NoteLine = "# FocusGuard: blocked while focusing. This block is managed automatically.";

    private static readonly byte[] BeginBytes = Encoding.ASCII.GetBytes(BeginMarker);
    private static readonly byte[] EndBytes = Encoding.ASCII.GetBytes(EndMarker);
    private static readonly byte[] NoteBytes = Encoding.ASCII.GetBytes(NoteLine);
    private static readonly byte[] Ipv4PrefixBytes = Encoding.ASCII.GetBytes("0.0.0.0 ");
    private static readonly byte[] Ipv6PrefixBytes = Encoding.ASCII.GetBytes("::1 ");
    private const byte Lf = (byte)'\n';
    private const byte Cr = (byte)'\r';

    public static bool ContainsBlock(byte[] content) => FindBlock(content) is not null;

    /// <summary>是否存在任何 BEGIN 标记 —— 完整区块或"只有 BEGIN 的残留"都算。</summary>
    public static bool HasAnyMarker(byte[] content) => IndexOf(content, BeginBytes, 0) >= 0;

    /// <summary>
    /// 清掉已有区块后重新追加，因此重复调用是幂等的。
    /// "只有 BEGIN 没有 END"的残留（外部编辑削除 END 行、写入中途崩溃等造成）
    /// 会先被 <see cref="RemoveBlock"/> 剥离再追加，不会叠加成两个 BEGIN 的畸形。
    /// </summary>
    public static byte[] AddBlock(byte[] content, IEnumerable<string> domains)
    {
        var head = RemoveBlock(content);

        // 保证区块从新的一行开始
        if (head.Length > 0 && head[^1] != Lf)
        {
            head = Concat(head, Encoding.ASCII.GetBytes("\r\n"));
        }

        return Concat(head, BuildBlock(domains));
    }

    /// <summary>
    /// 删除完整区块；对"只有 BEGIN 没有 END"的残留也同样剥离 ——
    /// 残留里的 0.0.0.0 规则行虽然不构成完整区块，却仍在真实生效，不能装作看不见。
    /// </summary>
    public static byte[] RemoveBlock(byte[] content)
    {
        // 极端情况下完整区块与残留可能同时存在多段，循环清到干净为止（上限兜底防死循环）
        for (var guard = 0; guard < 8; guard++)
        {
            var hit = FindBlock(content);
            if (hit is not null)
            {
                content = RemoveRange(content, hit.Value.Start, hit.Value.End);
                continue;
            }

            var residue = FindResidue(content);
            if (residue is null)
            {
                return content;
            }

            content = RemoveRange(content, residue.Value.Start, residue.Value.End);
        }
        return content;
    }

    /// <summary>
    /// 生成屏蔽区块。<b>每个域名同时写 IPv4 和 IPv6 两条</b>：
    /// 只写 <c>0.0.0.0</c> 时，浏览器仍会从上游 DNS 拿到真实 AAAA 记录并优先走 IPv6，屏蔽会失效。
    /// </summary>
    public static byte[] BuildBlock(IEnumerable<string> domains)
    {
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append("\r\n");
        sb.Append(NoteLine).Append("\r\n");
        foreach (var domain in Normalize(domains))
        {
            sb.Append("0.0.0.0 ").Append(domain).Append("\r\n");
            sb.Append("::1 ").Append(domain).Append("\r\n");
        }
        sb.Append(EndMarker).Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// 域名规范化：去空白、转小写、丢弃含空格或 # 的非法项、去重、稳定排序。
    /// 非 ASCII 域名（如中文 IDN）转 punycode —— 区块最终用 ASCII 编码写出，
    /// 不转的话会被编码器毁成 "0.0.0.0 ??.??" 这种无效行（F5）；转换失败（非法 IDN）则丢弃。
    /// 纯 ASCII 条目原样保留，不做额外校验，保持既有行为不变。
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> domains)
    {
        return domains
            .Select(d => (d ?? string.Empty).Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.Contains(' ') && !d.Contains('\t') && !d.Contains('#'))
            .Select(ToAsciiDomain)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把 IDN 域名转成 hosts 能写的 ASCII 形式；本就是 ASCII 的原样返回；非法 IDN 返回空串（由调用方丢弃）。</summary>
    private static string ToAsciiDomain(string domain)
    {
        if (domain.All(c => c <= 0x7F))
        {
            return domain;
        }

        try
        {
            return new IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>返回区块的 [起始索引, 结束索引)。区间包含末尾换行，且起始位置对齐到行首。</summary>
    private static (int Start, int End)? FindBlock(byte[] content)
    {
        var begin = IndexOf(content, BeginBytes, 0);
        if (begin < 0)
        {
            return null;
        }

        var endMarker = IndexOf(content, EndBytes, begin + BeginBytes.Length);
        if (endMarker < 0)
        {
            return null;
        }

        var end = endMarker + EndBytes.Length;
        while (end < content.Length && content[end] != Lf)
        {
            end++;
        }
        if (end < content.Length)
        {
            end++; // 吃掉结束标记那一行的换行符
        }

        return (StartOfLine(content, begin), end);
    }

    /// <summary>
    /// 定位"只有 BEGIN 没有 END"的残留：从 BEGIN 所在行首起，连续吞掉我们写得出的行
    /// （NOTE 行、0.0.0.0 / ::1 规则行、游离的 END 行），遇到任何不认得的行就停 ——
    /// 用户自己的内容一个字节都不碰（例如残留之后又被追加进去的注释或规则）。
    /// </summary>
    private static (int Start, int End)? FindResidue(byte[] content)
    {
        var begin = IndexOf(content, BeginBytes, 0);
        if (begin < 0)
        {
            return null;
        }

        var start = StartOfLine(content, begin);
        var end = -1;
        var pos = start;

        while (pos < content.Length)
        {
            var lf = pos;
            while (lf < content.Length && content[lf] != Lf)
            {
                lf++;
            }

            var lineEnd = lf < content.Length ? lf + 1 : lf;   // 行尾（含换行符）
            var textLen = lf - pos;                            // 行内容长度（不含 \n）
            if (textLen > 0 && content[pos + textLen - 1] == Cr)
            {
                textLen--;
            }

            if (end < 0)
            {
                end = lineEnd;      // 首行就是 BEGIN 所在行，必然属于我们
            }
            else if (ContainsAt(content, pos, textLen, EndBytes))
            {
                end = lineEnd;      // 游离的 END 行：一并清掉再收手
                break;
            }
            else if (StartsWithAt(content, pos, textLen, Ipv4PrefixBytes)
                     || StartsWithAt(content, pos, textLen, Ipv6PrefixBytes)
                     || EqualsAt(content, pos, textLen, NoteBytes))
            {
                end = lineEnd;
            }
            else
            {
                break;
            }

            pos = lineEnd;
        }

        return (start, end);
    }

    private static byte[] RemoveRange(byte[] content, int start, int end)
    {
        var result = new byte[content.Length - (end - start)];
        Array.Copy(content, 0, result, 0, start);
        Array.Copy(content, end, result, start, content.Length - end);
        return result;
    }

    private static bool StartsWithAt(byte[] haystack, int offset, int length, byte[] prefix) =>
        length >= prefix.Length && EqualsAt(haystack, offset, prefix.Length, prefix);

    private static bool EqualsAt(byte[] haystack, int offset, int length, byte[] value)
    {
        if (length != value.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (haystack[offset + i] != value[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsAt(byte[] haystack, int offset, int length, byte[] needle)
    {
        for (var i = offset; i <= offset + length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }
            if (j == needle.Length)
            {
                return true;
            }
        }
        return false;
    }

    private static int StartOfLine(byte[] content, int index)
    {
        var i = index;
        while (i > 0 && content[i - 1] != Lf)
        {
            i--;
        }
        return i;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }
            if (j == needle.Length)
            {
                return i;
            }
        }
        return -1;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Array.Copy(a, 0, result, 0, a.Length);
        Array.Copy(b, 0, result, a.Length, b.Length);
        return result;
    }
}
