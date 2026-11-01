namespace FocusLedger.Core;

/// <summary>对账结果：声称的专注时间里，实际有多少被证据支持。</summary>
public sealed record ReconcileResult(
    double ClaimedSeconds,
    double VerifiedSeconds,
    double ActiveSeconds,
    double SilentSeconds,
    double OffTaskSeconds,
    double AwaySeconds,
    double UntrackedSeconds,
    int ClaimCount)
{
    public double GapSeconds => Math.Max(0, ClaimedSeconds - VerifiedSeconds);

    /// <summary>
    /// 权重折损：确实在学习，但因为是被动观看或静默，只能按折扣计入。
    /// 不是「假时间」，是「打折时间」，必须与离席、分心分开说。
    /// </summary>
    public double WeightLossSeconds =>
        Math.Max(0, ActiveSeconds + SilentSeconds - VerifiedSeconds);

    public double AttributedGapSeconds =>
        OffTaskSeconds + AwaySeconds + UntrackedSeconds + WeightLossSeconds;

    public double VerifiedRatio => ClaimedSeconds > 0 ? VerifiedSeconds / ClaimedSeconds : 0;

    public static ReconcileResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// 把「外部计时器声称的专注区间」与「本机判定出的实证状态」按时间轴对齐。
/// 重叠部分按比例切分，避免边界上的段被整段计入或整段丢弃。
/// </summary>
public static class Reconciler
{
    public static ReconcileResult Compute(
        IReadOnlyList<TimerClaim> claims,
        IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(segments);

        if (claims.Count == 0)
        {
            return ReconcileResult.Empty;
        }

        double claimed = claims.Sum(c => c.ClaimedSeconds);
        double verified = 0, active = 0, silent = 0, offTask = 0, away = 0, overlapped = 0;

        foreach (var claim in claims)
        {
            foreach (var segment in segments)
            {
                long from = Math.Max(claim.StartUtcMs, segment.StartUtcMs);
                long to = Math.Min(claim.EndUtcMs, segment.EndUtcMs);
                if (to <= from)
                {
                    continue;
                }

                double seconds = (to - from) / 1000.0;
                overlapped += seconds;
                verified += seconds * segment.Weight;

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
        }

        return new ReconcileResult(
            claimed,
            verified,
            active,
            silent,
            offTask,
            away,
            Math.Max(0, claimed - overlapped),
            claims.Count);
    }
}
