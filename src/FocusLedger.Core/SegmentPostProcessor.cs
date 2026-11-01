namespace FocusLedger.Core;

/// <summary>
/// 第二遍处理：把实时判定（provisional）修正为日终判定（final）。
/// 核心是"返回修正"——短时间离开后回来，该段更可能是静默思考而非离席。
/// 见 docs/02-state-machine.md §4。
/// </summary>
public static class SegmentPostProcessor
{
    private const long TrimMs = 60_000;

    public static IReadOnlyList<Segment> Finalize(
        IReadOnlyList<Segment> provisional,
        InferenceParams parameters)
    {
        ArgumentNullException.ThrowIfNull(provisional);
        ArgumentNullException.ThrowIfNull(parameters);

        var reclassified = ApplyReturnReclassification(provisional, parameters);
        return MergeAdjacent(reclassified, parameters);
    }

    /// <summary>
    /// 返回修正：对每个 idle_away 段，若前后是同一个应用、时长不超过阈值、
    /// 且之后确实恢复了输入，则把中段（去掉首尾各 60 秒）重判为静默专注，权重 0.40。
    /// </summary>
    public static IReadOnlyList<Segment> ApplyReturnReclassification(
        IReadOnlyList<Segment> segments,
        InferenceParams parameters)
    {
        var result = new List<Segment>(segments.Count);

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            bool isGapCandidate = seg.State == ActivityState.IdleAway
                                  && i > 0
                                  && i < segments.Count - 1;

            if (!isGapCandidate)
            {
                result.Add(seg);
                continue;
            }

            var previous = segments[i - 1];
            var next = segments[i + 1];

            bool sameApp = string.Equals(previous.App, next.App, StringComparison.OrdinalIgnoreCase);
            bool sameReadingClass = previous.AppClass == AppClass.Reading
                                    && next.AppClass == AppClass.Reading;
            bool returned = next.State == ActivityState.ActiveFocus;
            bool shortEnough = seg.DurationSeconds <= parameters.ReturnMaxGapSeconds;

            if (!(returned && (sameApp || sameReadingClass) && shortEnough))
            {
                result.Add(seg);
                continue;
            }

            var silent = seg with
            {
                State = ActivityState.SilentFocus,
                Weight = 0.40,
                WeightLow = 0.20,
                WeightHigh = 0.55,
                Confidence = "low",
                RuleId = "R10:return",
                CorrectedBy = "return"
            };

            if (seg.EndUtcMs - seg.StartUtcMs > 2 * TrimMs)
            {
                // 离开与回归的边界各留 60 秒仍算离席，只有中间部分按静默专注计
                result.Add(seg with { EndUtcMs = seg.StartUtcMs + TrimMs });
                result.Add(silent with
                {
                    StartUtcMs = seg.StartUtcMs + TrimMs,
                    EndUtcMs = seg.EndUtcMs - TrimMs
                });
                result.Add(seg with { StartUtcMs = seg.EndUtcMs - TrimMs });
            }
            else
            {
                result.Add(silent);
            }
        }

        return result;
    }

    /// <summary>合并相邻的同类段，避免时间轴出现锯齿。</summary>
    public static IReadOnlyList<Segment> MergeAdjacent(
        IReadOnlyList<Segment> segments,
        InferenceParams parameters)
    {
        var merged = new List<Segment>(segments.Count);

        foreach (var seg in segments)
        {
            if (merged.Count == 0)
            {
                merged.Add(seg);
                continue;
            }

            var last = merged[^1];

            bool sameState = last.State == seg.State
                             && last.AppClass == seg.AppClass
                             && string.Equals(last.App, seg.App, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(last.CorrectedBy, seg.CorrectedBy, StringComparison.Ordinal);
            bool contiguous = seg.StartUtcMs - last.EndUtcMs <= 1000;

            if (!sameState || !contiguous)
            {
                merged.Add(seg);
                continue;
            }

            double d1 = last.DurationSeconds;
            double d2 = seg.DurationSeconds;
            double total = d1 + d2;
            if (total <= 0)
            {
                merged[^1] = last with { EndUtcMs = seg.EndUtcMs };
                continue;
            }

            merged[^1] = last with
            {
                EndUtcMs = seg.EndUtcMs,
                Weight = (last.Weight * d1 + seg.Weight * d2) / total,
                WeightLow = (last.WeightLow * d1 + seg.WeightLow * d2) / total,
                WeightHigh = (last.WeightHigh * d1 + seg.WeightHigh * d2) / total
            };
        }

        return merged;
    }
}
