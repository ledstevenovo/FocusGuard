using System.Text.Encodings.Web;
using System.Text.Json;

namespace FocusGuard.Core;

/// <summary>
/// 一次善始善终的专注会话：Start 成功 → Stop 最终成功（docs/专注统计设计方案.md §2.1）。
/// 只存两个端点，时长与归属日均由端点推导，不冗余存储。
/// </summary>
public sealed record FocusSession(DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    /// <summary>时钟回拨防御：负时长按 0 聚合，记录本身照常保留（不删数据）。</summary>
    public TimeSpan Duration => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;

    /// <summary>跨午夜会话整场归属开始日（§2.2）。</summary>
    public DateOnly StartDay => DateOnly.FromDateTime(StartedAt.LocalDateTime);
}

/// <summary>统计卡片的时间筛选档位，语义 = 含今天的自然日窗口。</summary>
public enum StatsRange
{
    All,
    Last30Days,
    Last7Days,
}

/// <summary>
/// 会话历史的落盘与聚合。统计是锦上添花：<see cref="Load"/> 损坏自愈、绝不抛异常阻断启动；
/// <see cref="TryAppend"/> 失败只写日志返回 false，绝不影响锁定 / 解除主流程（§3.2）。
/// </summary>
public sealed class FocusHistory
{
    /// <summary>万年量级防御：超过后丢弃最旧的一半。正常使用远触不到。</summary>
    private const int MaxSessions = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private List<FocusSession> _sessions;

    private FocusHistory(string path, List<FocusSession> sessions)
    {
        _path = path;
        _sessions = sessions;
    }

    /// <summary>
    /// 文件不存在 → 空历史；解析失败 → 把坏文件改名 <c>.corrupt-&lt;时间戳&gt;</c> 保留现场、
    /// 从空历史开始并写日志。任何情况下都不抛异常。
    /// </summary>
    public static FocusHistory Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<FocusSession>>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    return new FocusHistory(path, loaded.Where(s => s is not null).ToList()!);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            }
            catch
            {
                // 改名也失败（文件被锁等）就保留原文件，下次启动再试
            }
            AppPaths.Log("历史文件损坏，已改名保留并从空开始：" + AppPaths.Describe(ex));
        }

        return new FocusHistory(path, new List<FocusSession>());
    }

    /// <summary>原子落盘（tmp + 整体替换）。失败只写日志返回 false，且不改动内存数据。</summary>
    public bool TryAppend(FocusSession session)
    {
        try
        {
            var updated = new List<FocusSession>(_sessions) { session };
            if (updated.Count > MaxSessions)
            {
                updated = updated.Skip(updated.Count - MaxSessions / 2).ToList();
            }

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(updated, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            try
            {
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }

            _sessions = updated;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("统计记录写入失败（不影响主流程）：" + AppPaths.Describe(ex));
            return false;
        }
    }

    public IReadOnlyList<FocusSession> Sessions => _sessions;

    /// <summary>单次补记上限：一天的专注时长按定义不可能超过 24 小时。</summary>
    public static readonly TimeSpan MaxBackfillDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// 时长补记（专注统计方案 §5.6）：忘开 FocusGuard 的专注，按用户报的时长折成一条
    /// 会话补进当天。会话锚在当日零点起算 —— 归属日必为当天、入账时长与输入分毫不差，
    /// 也几乎不会与白天里的正常会话重叠；多次补记就是各次时长的直接相加。
    /// 参数非法（≤0 或超过 24 小时）返回 false 不落盘，落盘失败同 <see cref="TryAppend"/>。
    /// </summary>
    public bool TryBackfill(TimeSpan duration, DateTimeOffset now)
    {
        if (duration <= TimeSpan.Zero || duration > MaxBackfillDuration)
        {
            return false;
        }

        // now - TimeOfDay 即本地当日零点，偏移与 now 一致：StartDay 恒为当天
        var start = now - now.TimeOfDay;
        return TryAppend(new FocusSession(start, start + duration));
    }

    /// <summary>最早记录的开始日；无记录时为 null。</summary>
    public DateOnly? FirstDay => _sessions.Count == 0 ? null : _sessions.Min(s => s.StartDay);

    /// <summary>筛选档位对应的窗口起点（含端点）。All 无记录时返回今天。</summary>
    public DateOnly RangeStart(StatsRange range, DateOnly today) => range switch
    {
        StatsRange.Last7Days => today.AddDays(-6),
        StatsRange.Last30Days => today.AddDays(-29),
        _ => FirstDay ?? today,
    };

    /// <summary>某日的专注总时长（按归属开始日）。</summary>
    public TimeSpan TotalOn(DateOnly day) =>
        _sessions.Where(s => s.StartDay == day).Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);

    /// <summary>区间逐日总量，空日补零（热力图 / 近 7 天图的数据源）。</summary>
    public IReadOnlyList<(DateOnly Day, TimeSpan Total)> DailyTotals(DateOnly from, DateOnly to)
    {
        var result = new List<(DateOnly, TimeSpan)>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            result.Add((day, TotalOn(day)));
        }
        return result;
    }

    /// <summary>
    /// 最近 <paramref name="weeks"/> 周（含 <paramref name="today"/> 所在周）的逐周总量，
    /// 周以周一为首，元素为 (周一起始日, 周内总量)。
    /// </summary>
    public IReadOnlyList<(DateOnly WeekStart, TimeSpan Total)> WeeklyTotals(int weeks, DateOnly today)
    {
        var monday = today.AddDays(-((today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek) - 1));
        var result = new List<(DateOnly, TimeSpan)>(weeks);
        for (var i = weeks - 1; i >= 0; i--)
        {
            var weekStart = monday.AddDays(-7 * i);
            var weekEnd = weekStart.AddDays(6);
            var total = _sessions
                .Where(s => s.StartDay >= weekStart && s.StartDay <= weekEnd)
                .Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
            result.Add((weekStart, total));
        }
        return result;
    }

    /// <summary>窗口内时长 &gt; 0 的天数。</summary>
    public int ActiveDayCount(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (TotalOn(day) > TimeSpan.Zero)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>窗口内连续活跃天的最长段。</summary>
    public int LongestStreak(DateOnly from, DateOnly to)
    {
        var longest = 0;
        var run = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            run = TotalOn(day) > TimeSpan.Zero ? run + 1 : 0;
            if (run > longest)
            {
                longest = run;
            }
        }
        return longest;
    }

    /// <summary>从今天往回数的连续活跃天数；今天尚无记录则从昨天起算（今天中途完成会话后自然续上）。</summary>
    public int CurrentStreak(DateOnly today)
    {
        var day = TotalOn(today) > TimeSpan.Zero ? today : today.AddDays(-1);
        var streak = 0;
        while (TotalOn(day) > TimeSpan.Zero)
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>热力图色阶档位 0–5（§5.2 阈值表，边界含下界）。</summary>
    public static int HeatLevel(TimeSpan dayTotal) =>
        dayTotal >= TimeSpan.FromHours(8) ? 5 :
        dayTotal >= TimeSpan.FromHours(6) ? 4 :
        dayTotal >= TimeSpan.FromHours(4) ? 3 :
        dayTotal >= TimeSpan.FromHours(2) ? 2 :
        dayTotal > TimeSpan.Zero ? 1 : 0;

    /// <summary>人读时长：42秒 / 36分钟 / 3小时24分（分钟两位补零）/ 123小时（≥100 小时不带分钟）。</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0分钟";
        }
        var hours = (int)value.TotalHours;
        if (hours >= 100)
        {
            return hours + "小时";
        }
        if (hours > 0)
        {
            return $"{hours}小时{value.Minutes:00}分";
        }
        return value.TotalMinutes < 1 ? $"{value.Seconds}秒" : $"{value.Minutes}分钟";
    }
}
