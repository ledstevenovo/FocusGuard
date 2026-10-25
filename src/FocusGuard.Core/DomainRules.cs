using System.Globalization;

namespace FocusGuard.Core;

/// <summary>设置页用的一个站点分组：主域 + 它名下实际在屏蔽列表里的域名。</summary>
public sealed record DomainGroup(string Site, IReadOnlyList<string> Hostnames);

/// <summary>
/// 站点级域名规则：把用户输入（主域、带 www 的主机、甚至整条网址）解析成"主域"，
/// 并按主域展开出常见子域家族。hosts 只能精确匹配主机名、不支持通配，
/// 所以"屏蔽一个站"必须展开成一批主机名逐条写入 —— 这正是本类的职责。
/// 纯逻辑、无 IO，可直接单元测试。
/// </summary>
public static class DomainRules
{
    /// <summary>
    /// 家族展开时附加的常见子域前缀。选的是内容消费型子域（www / m / bbs / 贴吧 / 专栏……），
    /// 刻意不含 mail / smtp / cloud / drive 这类基础服务子域 —— 用户想屏蔽一个网站时，
    /// 不应该连带断掉自己的邮箱和网盘。不存在的子域写进 hosts 也无害（永远不会被解析到）。
    /// </summary>
    public static readonly string[] FamilyPrefixes =
    {
        "www", "m", "wap", "mobile",
        "bbs", "forum", "tieba",
        "live", "video", "v",
        "space", "zhuanlan", "blog",
        "search", "news", "app", "api", "pan",
    };

    /// <summary>
    /// 常见的"两段式后缀"（公战后缀之下还有一层）：在这些后缀下主域是三段
    /// （www.sina.com.cn → sina.com.cn），其余情况主域是两段（www.hupu.com → hupu.com）。
    /// 没有内置完整 Public Suffix List，只覆盖中文用户常见的一批，够分组与展开用。
    /// </summary>
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn", "mil.cn",
        "co.uk", "org.uk", "ac.uk", "gov.uk", "net.uk",
        "com.hk", "org.hk", "gov.hk", "com.tw", "org.tw", "gov.tw", "idv.tw",
        "com.au", "net.au", "org.au",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "co.kr", "or.kr", "co.nz", "net.nz", "org.nz",
        "com.br", "com.mx", "com.ar", "com.ru", "net.ru", "org.ru",
        "com.sg", "com.my", "co.in", "net.in", "org.in", "com.tr", "com.vn",
    };

    /// <summary>
    /// 解析用户输入的"一个网站"。接受主域（hupu.com）、子域主机（m.hupu.com）、
    /// 完整网址（https://www.hupu.com/compete 乃至含端口与路径的形态）、中文 IDN（虎扑.com）。
    /// 返回可作家族根的主域；解析不出有效主域（空串、无点单标签、非法字符）返回 null。
    /// </summary>
    public static string? TryParseSite(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var s = input.Trim();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }

        // 去掉路径 / 查询 / 锚点：取第一个分隔符之前的部分
        foreach (var c in "/?#")
        {
            var cut = s.IndexOf(c);
            if (cut >= 0)
            {
                s = s[..cut];
            }
        }

        // 去掉 userinfo（user:pass@host 形态）
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            s = s[(at + 1)..];
        }

        // 去掉端口。IPv6 字面量不是网站场景，LastIndexOf 按主机名处理即可
        var colon = s.LastIndexOf(':');
        if (colon >= 0)
        {
            s = s[..colon];
        }

        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0)
        {
            return null;
        }

        // 中文 IDN 转 punycode，与 hosts 区块的 ASCII 编码约定一致（见 HostsFile.Normalize）
        if (!s.All(c => c <= 0x7F))
        {
            try
            {
                s = new IdnMapping().GetAscii(s);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return TryGetRegistrable(s);
    }

    /// <summary>
    /// 从一个裸主机名（已小写）提取主域。两段式后缀取三段，其余取两段；
    /// 非法主机名（单标签、空标签、非法字符）返回 null。
    /// </summary>
    public static string? TryGetRegistrable(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var labels = host.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length < 2 || !labels.All(IsValidLabel))
        {
            return null;
        }

        var lastTwo = labels[^2] + "." + labels[^1];
        if (TwoLevelSuffixes.Contains(lastTwo))
        {
            if (labels.Length < 3 || !IsValidLabel(labels[^3]))
            {
                return null; // 只有 "com.cn" 本身，没有主域
            }
            return labels[^3] + "." + lastTwo;
        }

        return lastTwo;
    }

    /// <summary>
    /// 把一个主域展开成屏蔽家族：主域本身 + <see cref="FamilyPrefixes"/> 里的常见子域。
    /// site 应来自 <see cref="TryParseSite"/>（已验证）；这里仍做一次防御性校验，非法直接返回空。
    /// </summary>
    public static IReadOnlyList<string> ExpandFamily(string site)
    {
        if (TryGetRegistrable(site) is not { } root)
        {
            return Array.Empty<string>();
        }

        return FamilyPrefixes.Prepend(string.Empty)
            .Select(p => p.Length == 0 ? root : p + "." + root)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 把扁平的屏蔽域名列表按主域分组，设置页据此展示"一个站点一行"。
    /// 归不出主域的条目（单标签等）按原样各自成组，绝不丢弃 —— 列表里每一项都有去处。
    /// </summary>
    public static IReadOnlyList<DomainGroup> GroupBySite(IEnumerable<string> domains)
    {
        var bySite = new SortedList<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            var host = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (host.Length == 0)
            {
                continue;
            }

            var site = TryGetRegistrable(host) ?? host;
            if (!bySite.TryGetValue(site, out var bucket))
            {
                bucket = new List<string>();
                bySite[site] = bucket;
            }
            if (!bucket.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                bucket.Add(host);
            }
        }

        return bySite.Select(kv => new DomainGroup(kv.Key, kv.Value)).ToList();
    }

    /// <summary>标准主机名标签：1–63 个字符，字母/数字/连字符，不以下划线等非法字符出现，连字符不落首尾。</summary>
    private static bool IsValidLabel(string label) =>
        label.Length is >= 1 and <= 63 &&
        label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') &&
        !label.StartsWith('-') && !label.EndsWith('-');
}
