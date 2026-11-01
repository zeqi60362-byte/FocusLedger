namespace FocusLedger.Core;

/// <summary>把样本流切成窗口并逐窗判定，是整个判定管线的入口。</summary>
public sealed class SegmentBuilder
{
    private readonly IRuleEngine _rules;
    private readonly StateMachine _machine;
    private readonly InferenceParams _parameters;

    public SegmentBuilder(
        IRuleEngine? rules = null,
        InferenceParams? parameters = null,
        StateMachine? machine = null)
    {
        _rules = rules ?? RuleEngine.Default;
        _parameters = parameters ?? InferenceParams.Default;
        _machine = machine ?? new StateMachine();
    }

    public IReadOnlyList<Segment> Build(IEnumerable<Sample> samples, bool finalize = true)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples.OrderBy(s => s.TsUtcMs).ToList();
        if (ordered.Count == 0)
        {
            return Array.Empty<Segment>();
        }

        long windowMs = _parameters.WindowSeconds * 1000L;
        var segments = new List<Segment>();
        var bucket = new List<Sample>();

        foreach (var sample in ordered)
        {
            if (bucket.Count > 0 && sample.TsUtcMs - bucket[0].TsUtcMs >= windowMs)
            {
                segments.Add(Classify(bucket));
                bucket = new List<Sample>();
            }

            bucket.Add(sample);
        }

        if (bucket.Count > 0)
        {
            segments.Add(Classify(bucket));
        }

        return finalize
            ? SegmentPostProcessor.Finalize(segments, _parameters)
            : segments;
    }

    private Segment Classify(IReadOnlyList<Sample> bucket)
    {
        var features = FeatureAggregator.Aggregate(bucket, _rules, _parameters);
        var classification = _machine.Classify(features, _parameters);

        return new Segment(
            features.StartUtcMs,
            features.EndUtcMs,
            features.App,
            features.Title,
            features.AppClass,
            classification.State,
            classification.Weight,
            classification.WeightLow,
            classification.WeightHigh,
            classification.RuleId);
    }
}
