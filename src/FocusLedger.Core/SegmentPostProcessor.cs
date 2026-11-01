namespace FocusLedger.Core;

/// <summary>
/// 判定后处理：只做相邻同类段合并，避免时间轴出现锯齿。
/// 已取消「返回修正」——它会把「离开几分钟」改判成「在思考」，
/// 而那正是用户反馈里判错的那一类。
/// </summary>
public static class SegmentPostProcessor
{
    public static IReadOnlyList<Segment> Finalize(
        IReadOnlyList<Segment> provisional,
        InferenceParams parameters)
    {
        ArgumentNullException.ThrowIfNull(provisional);
        ArgumentNullException.ThrowIfNull(parameters);

        return MergeAdjacent(provisional, parameters);
    }

    public static IReadOnlyList<Segment> MergeAdjacent(
        IReadOnlyList<Segment> segments,
        InferenceParams parameters)
    {
        var merged = new List<Segment>(segments.Count);

        foreach (var segment in segments)
        {
            if (merged.Count == 0)
            {
                merged.Add(segment);
                continue;
            }

            var last = merged[^1];

            bool sameState = last.State == segment.State
                             && string.Equals(last.App, segment.App, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(last.RuleId, segment.RuleId, StringComparison.Ordinal);
            bool contiguous = segment.StartUtcMs - last.EndUtcMs <= 1000;

            if (!sameState || !contiguous)
            {
                merged.Add(segment);
                continue;
            }

            double d1 = last.DurationSeconds;
            double d2 = segment.DurationSeconds;
            double total = d1 + d2;

            merged[^1] = total <= 0
                ? last with { EndUtcMs = segment.EndUtcMs }
                : last with
                {
                    EndUtcMs = segment.EndUtcMs,
                    Weight = (last.Weight * d1 + segment.Weight * d2) / total,
                    WeightLow = (last.WeightLow * d1 + segment.WeightLow * d2) / total,
                    WeightHigh = (last.WeightHigh * d1 + segment.WeightHigh * d2) / total
                };
        }

        return merged;
    }
}
