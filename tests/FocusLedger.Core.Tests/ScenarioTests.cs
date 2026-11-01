using FocusLedger.Core;
using static FocusLedger.Core.Tests.TestSamples;

namespace FocusLedger.Core.Tests;

/// <summary>
/// 四状态判定模型的验收场景。
/// 核心原则：没有正面证据就算离席 —— 宁可少算，也不要把「人不在」算成「在思考」。
/// </summary>
public sealed class ScenarioTests
{
    private static readonly SegmentBuilder Builder = new();

    [Fact]
    public void S1_TimerRunningButUserAway_IsAway()
    {
        var samples = Run(0, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, idleSeconds: 1800);

        var segment = Assert.Single(Builder.Build(samples));

        Assert.Equal(ActivityState.Away, segment.State);
        Assert.Equal(0, segment.EffectiveSeconds);
    }

    [Fact]
    public void S2_WatchingStudyVideoWithoutInput_IsSilentFocus()
    {
        // B 站标题带「考研」→ 学习关键词，压过分心规则
        var samples = Run(0, 12, "chrome.exe", "【考研数学】高数基础班 第 12 讲",
            ContextLevel.Task, AppClass.Reading,
            idleSeconds: 1200,
            audioPeak: 0.30,
            media: MediaPlaybackState.Playing);

        var segment = Assert.Single(Builder.Build(samples));

        Assert.Equal(ActivityState.SilentFocus, segment.State);
        Assert.Equal(0.85, segment.Weight, 3);
    }

    [Fact]
    public void S3_ScrollingLongDocument_IsSilentFocus()
    {
        var samples = Run(0, 12, "sumatrapdf.exe", "Attention Is All You Need.pdf",
            ContextLevel.Task, AppClass.Reading, visualDelta: 11);

        var segment = Assert.Single(Builder.Build(samples));

        Assert.Equal(ActivityState.SilentFocus, segment.State);
        Assert.Equal(0.70, segment.Weight, 3);
    }

    [Fact]
    public void S4_NoInputNoVisualNoMedia_IsAwayNotSilent()
    {
        // 这正是用户反馈里判错的那一类：Word 开着、人走了、屏幕不动
        var samples = Run(0, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, idleSeconds: 400);

        var segment = Assert.Single(Builder.Build(samples));

        Assert.Equal(ActivityState.Away, segment.State);
        Assert.Equal(0, segment.EffectiveSeconds);
    }

    [Fact]
    public void S5_TypingInWord_IsActiveFocus()
    {
        var samples = Run(0, 12, "winword.exe", "论文.docx",
            ContextLevel.Task, AppClass.Typing, keys: 12, mouse: 2);

        var segment = Assert.Single(Builder.Build(samples));

        Assert.Equal(ActivityState.ActiveFocus, segment.State);
        Assert.Equal(1.00, segment.Weight, 3);
    }

    [Fact]
    public void S6_SocialCalls_IsOffTask()
    {
        var samples = Run(0, 12, "wechat.exe", "微信",
            ContextLevel.Distraction, AppClass.Other, keys: 20, mouse: 10);

        var segment = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.OffTask, segment.State);
    }

    [Fact]
    public void S7_WatchingMovieInVlc_IsOffTask()
    {
        var samples = Run(0, 12, "vlc.exe", "Some Movie (2019)",
            ContextLevel.Distraction, AppClass.Media,
            audioPeak: 0.5,
            media: MediaPlaybackState.Playing,
            visualDelta: 30);

        var segment = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.OffTask, segment.State);
    }

    [Fact]
    public void S8_LockedScreen_IsAway()
    {
        var samples = Run(0, 12, "LockApp.exe", "", ContextLevel.Neutral, AppClass.Other, locked: true);

        var segment = Assert.Single(Builder.Build(samples));
        Assert.Equal(ActivityState.Away, segment.State);
        Assert.Equal(0, segment.EffectiveSeconds);
    }

    [Fact]
    public void S9_EdgeInputs_DoNotThrow()
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
