namespace FocusLedger.Core;

public sealed record AppUsage(string App, double Seconds);

/// <summary>一天的账。</summary>
public sealed record DailySummary(
    DateOnly Date,
    double ActiveSeconds,
    double SilentSeconds,
    double OffTaskSeconds,
    double AwaySeconds,
    double EffectiveSeconds,
    double EffectiveLowSeconds,
    double EffectiveHighSeconds,
    int FocusBlocks,
    double LongestFocusBlockSeconds,
    int AppSwitches,
    IReadOnlyList<AppUsage> TopDistractions)
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
        new(date, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<AppUsage>());
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
        double effective = 0, low = 0, high = 0;
        int switches = 0;
        int focusBlocks = 0;
        double longestBlock = 0;
        double currentRun = 0;
        string? lastApp = null;
        var distractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

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

            effective += duration * segment.Weight;
            low += duration * segment.WeightLow;
            high += duration * segment.WeightHigh;

            if (lastApp is not null
                && !string.Equals(lastApp, segment.App, StringComparison.OrdinalIgnoreCase))
            {
                switches++;
            }

            lastApp = segment.App;

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

        var top = distractions
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new AppUsage(kv.Key, kv.Value))
            .ToList();

        return new DailySummary(
            date, active, silent, offTask, away,
            effective, low, high,
            focusBlocks, longestBlock, switches, top);
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
