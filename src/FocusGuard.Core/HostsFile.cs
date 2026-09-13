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
    private const byte Lf = (byte)'\n';

    public static bool ContainsBlock(byte[] content) => FindBlock(content) is not null;

    /// <summary>清掉已有区块后重新追加，因此重复调用是幂等的。</summary>
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

    public static byte[] RemoveBlock(byte[] content)
    {
        var hit = FindBlock(content);
        if (hit is null)
        {
            return content;
        }

        var (start, end) = hit.Value;
        var result = new byte[content.Length - (end - start)];
        Array.Copy(content, 0, result, 0, start);
        Array.Copy(content, end, result, start, content.Length - end);
        return result;
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

    /// <summary>域名规范化：去空白、转小写、丢弃含空格或 # 的非法项、去重、稳定排序。</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> domains)
    {
        return domains
            .Select(d => (d ?? string.Empty).Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.Contains(' ') && !d.Contains('\t') && !d.Contains('#'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
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
