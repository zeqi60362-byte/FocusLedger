namespace FocusLedger.Core;

/// <summary>
/// 把 60 秒窗口内的样本聚合成特征向量。见 docs/02-state-machine.md §2。
/// </summary>
public static class FeatureAggregator
{
    public static WindowFeatures Aggregate(
        IReadOnlyList<Sample> samples,
        IRuleEngine rules,
        InferenceParams parameters)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(parameters);

        if (samples.Count == 0)
        {
            throw new ArgumentException("窗口内至少需要一个样本。", nameof(samples));
        }

        var ordered = samples.OrderBy(s => s.TsUtcMs).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        long start = first.TsUtcMs;
        long end = last.TsUtcMs + parameters.TickSeconds * 1000L;
        if (end <= start)
        {
            end = start + parameters.TickSeconds * 1000L;
        }

        // 主导应用：按出现次数，其次按先后顺序
        var dominant = ordered
            .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(s => s.TsUtcMs))
            .First();

        string app = dominant.Key;

        string title = dominant
            .GroupBy(s => s.Title, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .First();

        int keys = ordered.Sum(s => s.KeyCount);
        int mouse = ordered.Sum(s => s.MouseCount);
        double input = MathUtil.Clamp01(
            keys / parameters.KeySaturation + mouse / parameters.MouseSaturation);

        double visualAverage = ordered.Average(s => (double)s.VisualDelta);
        double visual = MathUtil.Clamp01(
            visualAverage / parameters.VisualNormalizationDenominator);

        double audio = ordered.Max(s => s.AudioPeak);

        int playing = ordered.Count(s => s.MediaState == MediaPlaybackState.Playing);
        bool mediaPlaying = playing * 2 >= ordered.Count;

        // 上下文按"当前规则"重新推导，而不是用采集时存下的快照。
        // 这样用户改了规则，历史数据会立刻跟着变 —— 否则规则编辑形同虚设。
        var matches = ordered.Select(s => rules.Match(s.App, s.Title)).ToList();

        double contextValue = SnapContext(matches.Average(m => m.ContextValue));
        ContextLevel contextLevel = contextValue switch
        {
            >= 0.99 => ContextLevel.Task,
            <= 0.01 => ContextLevel.Distraction,
            _ => ContextLevel.Neutral
        };

        int maxIdle = ordered.Max(s => s.IdleSeconds);
        double lockedRatio = ordered.Count(s => s.Locked) / (double)ordered.Count;

        int dominantIndex = ordered.FindIndex(s => string.Equals(s.App, app, StringComparison.OrdinalIgnoreCase));
        var appClass = matches[Math.Max(0, dominantIndex)].AppClass;

        return new WindowFeatures(
            start,
            end,
            app,
            title,
            appClass,
            input,
            visual,
            audio,
            mediaPlaying,
            contextValue,
            contextLevel,
            maxIdle,
            lockedRatio,
            ordered.Count);
    }

    /// <summary>把连续的上下文系数吸附到 {1.0, 0.6, 0.0} 三档，避免出现 0.83 这种假精度。</summary>
    internal static double SnapContext(double value)
    {
        if (value >= 0.8) return 1.0;
        if (value > 0.3) return 0.6;
        return 0.0;
    }
}
