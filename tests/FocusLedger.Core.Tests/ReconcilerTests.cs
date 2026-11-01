using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

public sealed class ReconcilerTests
{
    private static Segment Seg(long startSec, long endSec, ActivityState state, double weight) =>
        new(startSec * 1000, endSec * 1000, "app", "title", AppClass.Typing,
            state, weight, weight, weight, "high", "test");

    private static TimerClaim Claim(long startSec, long endSec) =>
        TimerClaim.Create(startSec * 1000, endSec * 1000, "test");

    [Fact]
    public void ClaimFullyInsideActive_IsFullyVerified()
    {
        var segments = new[] { Seg(0, 3600, ActivityState.ActiveFocus, 1.0) };
        var claims = new[] { Claim(600, 1200) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(600, result.ClaimedSeconds, 0);
        Assert.Equal(600, result.VerifiedSeconds, 0);
        Assert.Equal(0, result.GapSeconds, 0);
        Assert.Equal(1.0, result.VerifiedRatio, 2);
    }

    [Fact]
    public void ClaimSpanningActiveAndIdle_IsSplitProportionally()
    {
        var segments = new[]
        {
            Seg(0, 600, ActivityState.ActiveFocus, 1.0),
            Seg(600, 1200, ActivityState.IdleAway, 0)
        };
        var claims = new[] { Claim(300, 900) }; // 300s 活跃 + 300s 离席

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(600, result.ClaimedSeconds, 0);
        Assert.Equal(300, result.VerifiedSeconds, 0);
        Assert.Equal(300, result.ActiveSeconds, 0);
        Assert.Equal(300, result.IdleSeconds, 0);
        Assert.Equal(0.5, result.VerifiedRatio, 2);
    }

    [Fact]
    public void ClaimOutsideAnySegment_CountsAsUntracked()
    {
        var segments = new[] { Seg(0, 600, ActivityState.ActiveFocus, 1.0) };
        var claims = new[] { Claim(1000, 1600) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(600, result.ClaimedSeconds, 0);
        Assert.Equal(0, result.VerifiedSeconds, 0);
        Assert.Equal(600, result.UntrackedSeconds, 0);
    }

    [Fact]
    public void PassiveFocusContributesWeightedTime()
    {
        var segments = new[] { Seg(0, 600, ActivityState.PassiveFocus, 0.85) };
        var claims = new[] { Claim(0, 600) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(510, result.VerifiedSeconds, 0);
        Assert.Equal(90, result.GapSeconds, 0);
    }

    [Fact]
    public void NoClaims_ReturnsEmpty()
    {
        var result = Reconciler.Compute(Array.Empty<TimerClaim>(), Array.Empty<Segment>());
        Assert.Equal(0, result.ClaimedSeconds, 0);
        Assert.Equal(0, result.ClaimCount);
    }
}
