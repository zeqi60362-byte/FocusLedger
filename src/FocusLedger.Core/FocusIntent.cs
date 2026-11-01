namespace FocusLedger.Core;

public enum IntentStatus
{
    Running,
    Completed,
    Abandoned
}

/// <summary>一次"接下来 N 分钟做某事"的声明。</summary>
public sealed record FocusIntent(
    int Id,
    long StartUtcMs,
    long PlannedEndUtcMs,
    string Label,
    IntentStatus Status,
    double? Adherence)
{
    public double PlannedSeconds => Math.Max(0, (PlannedEndUtcMs - StartUtcMs) / 1000.0);
}

/// <summary>意图窗口内的实际表现。</summary>
public sealed record IntentOutcome(
    double WindowSeconds,
    double EffectiveSeconds,
    double ActiveSeconds,
    double SilentSeconds,
    double OffTaskSeconds,
    double AwaySeconds,
    double TrackedSeconds)
{
    public double Adherence =>
        WindowSeconds <= 0 ? 0 : Math.Clamp(EffectiveSeconds / WindowSeconds, 0, 1);

    /// <summary>窗口内没有任何采集数据的占比。太高说明程序没在跑，遵守率不可信。</summary>
    public double UntrackedRatio =>
        WindowSeconds <= 0 ? 0 : Math.Clamp(1 - TrackedSeconds / WindowSeconds, 0, 1);
}

public static class IntentEvaluator
{
    /// <summary>
    /// 评估意图窗口。[untilUtcMs] 为空时按计划的结束时间算；
    /// 实时进度传入"现在"，即可得到截止此刻的遵守率。
    /// </summary>
    public static IntentOutcome Evaluate(
        FocusIntent intent,
        IReadOnlyList<Segment> segments,
        long? untilUtcMs = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(segments);

        long from = intent.StartUtcMs;
        long to = untilUtcMs.HasValue
            ? Math.Min(untilUtcMs.Value, intent.PlannedEndUtcMs)
            : intent.PlannedEndUtcMs;

        if (to <= from)
        {
            return new IntentOutcome(0, 0, 0, 0, 0, 0, 0);
        }

        double window = (to - from) / 1000.0;
        double effective = 0, active = 0, silent = 0, offTask = 0, away = 0, tracked = 0;

        foreach (var segment in segments)
        {
            long overlapFrom = Math.Max(from, segment.StartUtcMs);
            long overlapTo = Math.Min(to, segment.EndUtcMs);
            if (overlapTo <= overlapFrom)
            {
                continue;
            }

            double seconds = (overlapTo - overlapFrom) / 1000.0;
            tracked += seconds;
            effective += seconds * segment.Weight;

            switch (segment.State)
            {
                case ActivityState.ActiveFocus:
                    active += seconds;
                    break;
                case ActivityState.SilentFocus:
                    silent += seconds;
                    break;
                case ActivityState.OffTask:
                    offTask += seconds;
                    break;
                default:
                    away += seconds;
                    break;
            }
        }

        return new IntentOutcome(
            window, effective, active, silent, offTask, away, Math.Min(tracked, window));
    }
}
