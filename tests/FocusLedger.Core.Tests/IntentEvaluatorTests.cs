using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

public sealed class IntentEvaluatorTests
{
    private const long T0 = 1_700_000_000_000;

    private static Segment Seg(long startSec, long endSec, ActivityState state, double weight) =>
        new(T0 + startSec * 1000, T0 + endSec * 1000, "app", "title", AppClass.Typing,
            state, weight, weight, weight, "high", "test");

    private static FocusIntent Intent(long minutes) =>
        new(1, T0, T0 + (long)minutes * 60_000, "写论文", IntentStatus.Running, null);

    [Fact]
    public void FullyFocusedWindow_IsFullAdherence()
    {
        var segments = new[] { Seg(0, 1500, ActivityState.ActiveFocus, 1.0) };

        var outcome = IntentEvaluator.Evaluate(Intent(25), segments);

        Assert.Equal(1500, outcome.WindowSeconds, 0);
        Assert.Equal(1500, outcome.EffectiveSeconds, 0);
        Assert.Equal(1.0, outcome.Adherence, 2);
    }

    [Fact]
    public void HalfIdleWindow_IsHalfAdherence()
    {
        var segments = new[]
        {
            Seg(0, 750, ActivityState.ActiveFocus, 1.0),
            Seg(750, 1500, ActivityState.IdleAway, 0)
        };

        var outcome = IntentEvaluator.Evaluate(Intent(25), segments);

        Assert.Equal(0.5, outcome.Adherence, 2);
        Assert.Equal(750, outcome.IdleSeconds, 0);
    }

    [Fact]
    public void PassiveWatchingCountsAsDiscountedAdherence()
    {
        var segments = new[] { Seg(0, 1500, ActivityState.PassiveFocus, 0.85) };

        var outcome = IntentEvaluator.Evaluate(Intent(25), segments);

        Assert.Equal(0.85, outcome.Adherence, 2);
        Assert.Equal(1500, outcome.PassiveSeconds, 0);
    }

    [Fact]
    public void NoData_ShowsUntracked()
    {
        var outcome = IntentEvaluator.Evaluate(Intent(25), Array.Empty<Segment>());

        Assert.Equal(0, outcome.Adherence, 2);
        Assert.Equal(1.0, outcome.UntrackedRatio, 2);
    }

    [Fact]
    public void PartialWindowOnlyCountsElapsedTime()
    {
        // 计划 50 分钟，只过了 10 分钟，且这 10 分钟都在专注
        var segments = new[] { Seg(0, 600, ActivityState.ActiveFocus, 1.0) };

        var outcome = IntentEvaluator.Evaluate(Intent(50), segments, T0 + 600_000);

        Assert.Equal(600, outcome.WindowSeconds, 0);
        Assert.Equal(1.0, outcome.Adherence, 2);
    }

    [Fact]
    public void SegmentsOutsideWindow_AreIgnored()
    {
        var segments = new[]
        {
            Seg(-600, 0, ActivityState.ActiveFocus, 1.0),   // 窗口之前
            Seg(1500, 1800, ActivityState.ActiveFocus, 1.0) // 窗口之后
        };

        var outcome = IntentEvaluator.Evaluate(Intent(25), segments);

        Assert.Equal(0, outcome.EffectiveSeconds, 0);
        Assert.Equal(1.0, outcome.UntrackedRatio, 2);
    }
}
