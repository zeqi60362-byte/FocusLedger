using FocusLedger.Core;
using static FocusLedger.Core.Tests.TestSamples;

namespace FocusLedger.Core.Tests;

public sealed class AggregationTests
{
    private static Segment Seg(
        long startMs, long endMs, ActivityState state, double w, double lo, double hi,
        string app = "code.exe", AppClass cls = AppClass.Typing)
        => new(startMs, endMs, app, "t", cls, state, w, lo, hi, "test");

    [Fact]
    public void FeatureAggregator_InputActivitySaturates()
    {
        var samples = Run(0, 12, "code.exe", "Program.cs", ContextLevel.Task, AppClass.Typing, keys: 20);
        var f = FeatureAggregator.Aggregate(samples, RuleEngine.Default, InferenceParams.Default);

        Assert.Equal(1.0, f.InputActivity, 3);
        Assert.Equal(ContextLevel.Task, f.Context);
    }

    [Fact]
    public void FeatureAggregator_VisualIsNormalized()
    {
        var quiet = FeatureAggregator.Aggregate(
            Run(0, 12, "code.exe", "x", ContextLevel.Task, AppClass.Typing, visualDelta: 0),
            RuleEngine.Default, InferenceParams.Default);
        var busy = FeatureAggregator.Aggregate(
            Run(0, 12, "code.exe", "x", ContextLevel.Task, AppClass.Typing, visualDelta: 12),
            RuleEngine.Default, InferenceParams.Default);

        Assert.Equal(0, quiet.VisualActivity, 3);
        Assert.Equal(1.0, busy.VisualActivity, 3);
    }

    [Fact]
    public void FeatureAggregator_SnapsContextToThreeLevels()
    {
        Assert.Equal(1.0, FeatureAggregator.SnapContext(0.9), 3);
        Assert.Equal(0.6, FeatureAggregator.SnapContext(0.5), 3);
        Assert.Equal(0.0, FeatureAggregator.SnapContext(0.2), 3);
    }

    [Fact]
    public void FeatureAggregator_MediaPlayingNeedsMajority()
    {
        var samples = Run(0, 12, "chrome.exe", "lecture", ContextLevel.Neutral, AppClass.Reading,
            media: MediaPlaybackState.Playing);
        Assert.True(FeatureAggregator
            .Aggregate(samples, RuleEngine.Default, InferenceParams.Default).MediaPlaying);

        var paused = Run(0, 12, "chrome.exe", "lecture", ContextLevel.Neutral, AppClass.Reading,
            media: MediaPlaybackState.Paused);
        Assert.False(FeatureAggregator
            .Aggregate(paused, RuleEngine.Default, InferenceParams.Default).MediaPlaying);
    }

    [Fact]
    public void Daily_AccumulatesFourStatesAndCountsFocusBlocks()
    {
        long t0 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long h = 3600_000;

        var segments = new[]
        {
            Seg(t0, t0 + h, ActivityState.ActiveFocus, 1.0, 0.95, 1.0),
            Seg(t0 + h, t0 + h + h / 2, ActivityState.OffTask, 0, 0, 0, "wechat.exe", AppClass.Other),
            Seg(t0 + h + h / 2, t0 + 2 * h, ActivityState.Away, 0, 0, 0),
            Seg(t0 + 2 * h, t0 + 2 * h + h / 2, ActivityState.ActiveFocus, 1.0, 0.95, 1.0)
        };

        var summary = DailyAggregator.Aggregate(
            segments, new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);

        Assert.Equal(5400, summary.ActiveSeconds, 0);
        Assert.Equal(1800, summary.OffTaskSeconds, 0);
        Assert.Equal(1800, summary.AwaySeconds, 0);
        Assert.Equal(5400, summary.EffectiveSeconds, 0);
        Assert.Equal(2, summary.FocusBlocks);
        Assert.Single(summary.TopDistractions);
        Assert.Equal("wechat.exe", summary.TopDistractions[0].App);
    }

    [Fact]
    public void Daily_ClipsSegmentsToDayBoundary()
    {
        long dayStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // 跨越午夜：前一天 23:00 到当天 01:00，只应计入当天 1 小时
        var segments = new[]
        {
            Seg(dayStart - 3600_000, dayStart + 3600_000, ActivityState.ActiveFocus, 1.0, 0.95, 1.0)
        };

        var summary = DailyAggregator.Aggregate(
            segments, new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);

        Assert.Equal(3600, summary.ActiveSeconds, 0);
    }

    [Fact]
    public void Daily_SilentFocusCountsAtDiscountedWeight()
    {
        long t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var segments = new[]
        {
            Seg(t0, t0 + 3600_000, ActivityState.SilentFocus, 0.85, 0.75, 0.95)
        };

        var summary = DailyAggregator.Aggregate(
            segments, new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);

        Assert.Equal(3600, summary.SilentSeconds, 0);

        // 467f18e 起「深度与静默不再打折」：有效专注按原始时长计入，
        // 权重只用来标记弱证据段（WeakEvidenceSeconds），不再参与折算。
        Assert.Equal(3600, summary.EffectiveSeconds, 0);
        Assert.Equal(3600, summary.WeakEvidenceSeconds, 0);
    }
}
