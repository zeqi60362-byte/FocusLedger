using FocusLedger.Core;
using static FocusLedger.Core.Tests.TestSamples;

namespace FocusLedger.Core.Tests;

/// <summary>
/// docs/02-state-machine.md §6 的 8 个验收场景。
/// 这些测试是产品的契约：任何一条变红都说明判定逻辑跑偏了。
/// </summary>
public sealed class ScenarioTests
{
    private static readonly SegmentBuilder Builder = new();

    // S1 番茄钟在跑，人已离开 30 分钟，屏幕静止 → 不能算学习
    [Fact]
    public void S1_TimerRunningButUserAway_IsIdleAway()
    {
        var samples = Run(0, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, idleSeconds: 1800);

        var segments = Builder.Build(samples);

        var seg = Assert.Single(segments);
        Assert.Equal(ActivityState.IdleAway, seg.State);
        Assert.Equal(0, seg.Weight);
        Assert.Equal(0, seg.EffectiveSeconds);
    }

    // S2 看 45 分钟网课，全程无键鼠 → 必须算被动专注，不能判离席
    [Fact]
    public void S2_WatchingLectureWithoutInput_IsPassiveFocus()
    {
        var samples = Run(0, 12, "chrome.exe", "MIT 6.824 Lecture 12",
            ContextLevel.Neutral, AppClass.Reading,
            idleSeconds: 1200,
            audioPeak: 0.30,
            media: MediaPlaybackState.Playing);

        var seg = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.PassiveFocus, seg.State);
        Assert.Equal(0.85, seg.Weight, 3);
    }

    // S3 读 PDF 20 分钟只滚动 3 次 → 无输入的窗口应判静默专注 0.70
    [Fact]
    public void S3_ReadingPdfWithoutScrolling_IsSilentFocusAtReadingWeight()
    {
        var samples = Run(0, 12, "sumatrapdf.exe", "Attention Is All You Need.pdf",
            ContextLevel.Task, AppClass.Reading, idleSeconds: 240);

        var seg = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.SilentFocus, seg.State);
        Assert.Equal(0.70, seg.Weight, 3);
    }

    // S4 前台 Word，切到微信聊 8 分钟 → 这段时间算分心
    [Fact]
    public void S4_SwitchingToWechat_IsOffTask()
    {
        var samples = Run(0, 12, "wechat.exe", "微信",
            ContextLevel.Distraction, AppClass.Other,
            keys: 20, mouse: 10);

        var seg = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.OffTask, seg.State);
        Assert.Equal(0, seg.Weight);
    }

    // S5 前台 Word，思考 6 分钟无输入后回来 → 实时判离席，日终修正为静默专注
    [Fact]
    public void S5_ShortGapThenReturn_IsReclassifiedAsSilentFocus()
    {
        var samples = new List<Sample>();
        samples.AddRange(Run(0, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, keys: 10));
        samples.AddRange(Run(60_000, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, idleSeconds: 400));
        samples.AddRange(Run(120_000, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, keys: 10));

        var provisional = Builder.Build(samples, finalize: false);
        var gap = provisional[1];
        Assert.Equal(ActivityState.IdleAway, gap.State);

        var final = Builder.Build(samples);
        var healed = final.Single(s => s.CorrectedBy == "return");
        Assert.Equal(ActivityState.SilentFocus, healed.State);
        Assert.Equal(0.40, healed.Weight, 3);
    }

    // S6 深夜用 VLC 看电影 → 分心，不能因为"屏幕在动"就算学习
    [Fact]
    public void S6_WatchingMovieInVlc_IsOffTask()
    {
        var samples = Run(0, 12, "vlc.exe", "Some Movie (2019)",
            ContextLevel.Distraction, AppClass.Media,
            audioPeak: 0.5,
            media: MediaPlaybackState.Playing,
            visualDelta: 30);

        var seg = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.OffTask, seg.State);
        Assert.Equal(0, seg.Weight);
    }

    // S7 锁屏 2 小时 → 完全不产生时长
    [Fact]
    public void S7_LockedScreen_ProducesNoTime()
    {
        var samples = Run(0, 12, "LockApp.exe", "", ContextLevel.Neutral, AppClass.Other, locked: true);

        var seg = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.Locked, seg.State);
        Assert.Equal(0, seg.EffectiveSeconds);
    }

    // S8 边界情况：空输入、单样本、重复时间戳都不得抛异常
    [Fact]
    public void S8_EdgeInputs_DoNotThrow()
    {
        Assert.Empty(Builder.Build(Array.Empty<Sample>()));

        var single = Builder.Build(new[]
        {
            Sample(0, "code.exe", "Program.cs", ContextLevel.Task, AppClass.Typing, keys: 5)
        });
        Assert.Single(single);

        var duplicated = Enumerable.Repeat(
            Sample(0, "code.exe", "Program.cs", ContextLevel.Task, AppClass.Typing, keys: 5), 12);
        Assert.Single(Builder.Build(duplicated));
    }
}
