using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

public sealed class ReconcilerTests
{
    private static Segment Seg(long startSec, long endSec, ActivityState state, double weight) =>
        new(startSec * 1000, endSec * 1000, "app", "title", AppClass.Typing,
            state, weight, weight, weight, "test");

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
    }

    [Fact]
    public void ClaimSpanningActiveAndAway_IsSplitProportionally()
    {
        var segments = new[]
        {
            Seg(0, 600, ActivityState.ActiveFocus, 1.0),
            Seg(600, 1200, ActivityState.Away, 0)
        };
        var claims = new[] { Claim(300, 900) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(600, result.ClaimedSeconds, 0);
        Assert.Equal(300, result.VerifiedSeconds, 0);
        Assert.Equal(300, result.AwaySeconds, 0);
        Assert.Equal(0.5, result.VerifiedRatio, 2);
    }

    [Fact]
    public void ClaimOutsideAnySegment_CountsAsUntracked()
    {
        var segments = new[] { Seg(0, 600, ActivityState.ActiveFocus, 1.0) };
        var claims = new[] { Claim(1000, 1600) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(0, result.VerifiedSeconds, 0);
        Assert.Equal(600, result.UntrackedSeconds, 0);
    }

    [Fact]
    public void SilentFocusContributesWeightedTime()
    {
        var segments = new[] { Seg(0, 600, ActivityState.SilentFocus, 0.85) };
        var claims = new[] { Claim(0, 600) };

        var result = Reconciler.Compute(claims, segments);

        Assert.Equal(510, result.VerifiedSeconds, 0);
        Assert.Equal(90, result.GapSeconds, 0);
    }

    [Fact]
    public void GapAttributionIsClosed()
    {
        var segments = new[]
        {
            Seg(0, 300, ActivityState.ActiveFocus, 1.0),
            Seg(300, 600, ActivityState.OffTask, 0),
            Seg(600, 900, ActivityState.Away, 0),
            Seg(900, 1200, ActivityState.SilentFocus, 0.85)
        };
        var claims = new[] { Claim(0, 1800) };

        var result = Reconciler.Compute(claims, segments);

        // 差额必须严格等于各项之和
        Assert.Equal(result.GapSeconds, result.AttributedGapSeconds, 1);
    }

    [Fact]
    public void NoClaims_ReturnsEmpty()
    {
        var result = Reconciler.Compute(Array.Empty<TimerClaim>(), Array.Empty<Segment>());
        Assert.Equal(0, result.ClaimedSeconds, 0);
        Assert.Equal(0, result.ClaimCount);
    }
}
