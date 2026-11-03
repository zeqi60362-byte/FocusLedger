namespace FocusLedger.Core;

public sealed record AppUsage(string App, double Seconds, IReadOnlyList<AppUsage>? Children = null);

internal static class BrowserSites
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe",
        "360se.exe", "360chrome.exe", "qqbrowser.exe", "safari.exe", "iexplore.exe",
        "vivaldi.exe", "arc.exe"
    };

    private static readonly (string Keyword, string Site)[] Sites =
    {
        ("bilibili", "B站"), ("哔哩哔哩", "B站"),
        ("小红书", "小红书"), ("xiaohongshu", "小红书"),
        ("weibo", "微博"), ("微博", "微博"),
        ("zhihu", "知乎"), ("知乎", "知乎"),
        ("douyin", "抖音"), ("抖音", "抖音"),
        ("youtube", "YouTube"), ("github", "GitHub"), ("arxiv", "arXiv"),
        ("google scholar", "Google Scholar"), ("overleaf", "Overleaf"),
        ("cnki", "知网"), ("知网", "知网"),
        ("taobao", "淘宝"), ("jd.com", "京东"), ("baidu", "百度"),
        ("notion", "Notion"), ("chatgpt", "ChatGPT"), ("deepseek", "DeepSeek")
    };

    public static bool IsBrowser(string app) => !string.IsNullOrEmpty(app) && Browsers.Contains(app);

    /// <summary>从窗口标题里认出站点。认不出就归到「其他网页」。</summary>
    public static string? SiteOf(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        foreach (var (keyword, site) in Sites)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return site;
            }
        }

        return "其他网页";
    }
}
/// <summary>从 A 应用切到 B 应用的次数。</summary>
public sealed record AppTransition(string From, string To, int Count);

/// <summary>一天的账。</summary>
public sealed record DailySummary(
    DateOnly Date,
    double ActiveSeconds,
    double SilentSeconds,
    double OffTaskSeconds,
    double AwaySeconds,
    double EffectiveSeconds,
    int FocusBlocks,
    double LongestFocusBlockSeconds,
    int AppSwitches,
    IReadOnlyList<AppUsage> TopDistractions,
    IReadOnlyList<AppTransition> TopTransitions,
    IReadOnlyList<AppUsage> AppTotals,
    double WeakEvidenceSeconds = 0)
{
    public double EffectiveHours => EffectiveSeconds / 3600.0;
    public double ActiveHours => ActiveSeconds / 3600.0;
    public double SilentHours => SilentSeconds / 3600.0;
    public double OffTaskHours => OffTaskSeconds / 3600.0;
    public double AwayHours => AwaySeconds / 3600.0;

    public double AccountedSeconds => ActiveSeconds + SilentSeconds + OffTaskSeconds + AwaySeconds;

    /// <summary>实证率：有效专注占已统计时长的比例。</summary>
    public double EffectiveRatio => AccountedSeconds > 0 ? EffectiveSeconds / AccountedSeconds : 0;

    public static DailySummary Empty(DateOnly date) =>
        new(date, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<AppUsage>(), Array.Empty<AppTransition>(),
            Array.Empty<AppUsage>());
}

public static class DailyAggregator
{
    public static DailySummary Aggregate(
        IEnumerable<Segment> segments,
        DateOnly date,
        TimeZoneInfo? timeZone = null,
        InferenceParams? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var tz = timeZone ?? TimeZoneInfo.Local;
        var p = parameters ?? InferenceParams.Default;

        long dayStart = StartOfDayUtc(date, tz);
        long dayEnd = StartOfDayUtc(date.AddDays(1), tz);

        double active = 0, silent = 0, offTask = 0, away = 0;
        double effective = 0;
        double weak = 0;
        int switches = 0;
        int focusBlocks = 0;
        double longestBlock = 0;
        double currentRun = 0;
        string? lastApp = null;
        var distractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var transitions = new Dictionary<(string From, string To), int>();
        var appSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var browserSites = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments.OrderBy(s => s.StartUtcMs))
        {
            long start = Math.Max(segment.StartUtcMs, dayStart);
            long end = Math.Min(segment.EndUtcMs, dayEnd);
            if (end <= start)
            {
                continue;
            }

            double duration = (end - start) / 1000.0;

            switch (segment.State)
            {
                case ActivityState.ActiveFocus:
                    active += duration;
                    break;
                case ActivityState.SilentFocus:
                    silent += duration;
                    break;
                case ActivityState.OffTask:
                    offTask += duration;
                    distractions.TryGetValue(segment.App, out double previous);
                    distractions[segment.App] = previous + duration;
                    break;
                default:
                    away += duration;
                    break;
            }

            // 深度与静默按原始时长计入，不再打折——见 v0.6 口径调整
            if (segment.State is ActivityState.ActiveFocus or ActivityState.SilentFocus)
            {
                effective += duration;
            }
            if (segment.Weight < 0.999 && segment.State is ActivityState.ActiveFocus or ActivityState.SilentFocus)
            {
                weak += duration;
            }

            if (lastApp is not null
                && !string.Equals(lastApp, segment.App, StringComparison.OrdinalIgnoreCase))
            {
                switches++;
                var key = (lastApp, segment.App);
                transitions[key] = transitions.TryGetValue(key, out int seen) ? seen + 1 : 1;
            }

            lastApp = segment.App;

            if (!string.IsNullOrEmpty(segment.App))
            {
                appSeconds[segment.App] = appSeconds.TryGetValue(segment.App, out double seen)
                    ? seen + segment.DurationSeconds
                    : segment.DurationSeconds;

                if (BrowserSites.IsBrowser(segment.App) && BrowserSites.SiteOf(segment.Title) is { } site)
                {
                    if (!browserSites.TryGetValue(segment.App, out var sites))
                    {
                        sites = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        browserSites[segment.App] = sites;
                    }

                    sites[site] = sites.TryGetValue(site, out double siteSeen)
                        ? siteSeen + segment.DurationSeconds
                        : segment.DurationSeconds;
                }
            }

            if (segment.State == ActivityState.ActiveFocus)
            {
                currentRun += duration;
            }
            else
            {
                if (currentRun >= p.FocusBlockMinSeconds)
                {
                    focusBlocks++;
                    longestBlock = Math.Max(longestBlock, currentRun);
                }

                currentRun = 0;
            }
        }

        if (currentRun >= p.FocusBlockMinSeconds)
        {
            focusBlocks++;
            longestBlock = Math.Max(longestBlock, currentRun);
        }

        var topTransitions = transitions
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv => new AppTransition(kv.Key.From, kv.Key.To, kv.Value))
            .ToList();

        var top = distractions
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new AppUsage(kv.Key, kv.Value))
            .ToList();

        var appTotals = appSeconds
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .Select(kv =>
            {
                IReadOnlyList<AppUsage>? children = null;
                if (browserSites.TryGetValue(kv.Key, out var sites) && sites.Count > 0)
                {
                    children = sites
                        .OrderByDescending(s => s.Value)
                        .Take(5)
                        .Select(s => new AppUsage(s.Key, s.Value))
                        .ToList();
                }

                return new AppUsage(kv.Key, kv.Value, children);
            })
            .ToList();

        double shown = appTotals.Sum(a => a.Seconds);
        double otherSeconds = appSeconds.Sum(kv => kv.Value) - shown;
        if (otherSeconds > 1)
        {
            appTotals.Add(new AppUsage("__other__", otherSeconds));
        }

        return new DailySummary(
            date, active, silent, offTask, away,
            effective,
            focusBlocks, longestBlock, switches, top, topTransitions, appTotals, weak);
    }

    /// <summary>某一天在 UTC 上覆盖的区间 [start, end)。</summary>
    public static (long StartUtcMs, long EndUtcMs) DayRangeUtc(DateOnly date, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        return (StartOfDayUtc(date, tz), StartOfDayUtc(date.AddDays(1), tz));
    }

    public static long StartOfDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }
}
