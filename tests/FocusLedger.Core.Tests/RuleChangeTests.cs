using FocusLedger.Core;
using static FocusLedger.Core.Tests.TestSamples;

namespace FocusLedger.Core.Tests;

/// <summary>
/// 规则变更必须能重算历史数据。
/// 如果哪天判定改回"使用采集时存下的上下文快照"，这些测试会立刻变红。
/// </summary>
public sealed class RuleChangeTests
{
    private static RuleEngine With(params Rule[] extra) =>
        new(BuiltinRules.All.Concat(extra));

    [Fact]
    public void ChangingRule_ReclassifiesHistoricalSamples()
    {
        var samples = Run(0, 12, "msedge.exe", "某个页面", ContextLevel.Neutral, AppClass.Reading, keys: 10);

        var neutralSegment = Assert.Single(new SegmentBuilder(RuleEngine.Default).Build(samples));
        Assert.Equal(ActivityState.ActiveFocus, neutralSegment.State);
        Assert.Equal(0.75, neutralSegment.Weight, 2);

        var taskRule = new Rule(
            Id: 9001,
            MatchType: RuleMatchType.Process,
            Pattern: "msedge.exe",
            Context: ContextLevel.Task,
            AppClass: AppClass.Reading,
            Priority: 5,
            Enabled: true,
            IsBuiltin: false);

        var taskSegment = Assert.Single(new SegmentBuilder(With(taskRule)).Build(samples));
        Assert.Equal(1.00, taskSegment.Weight, 2);
    }

    [Fact]
    public void DisablingBuiltinRule_FallsBackToDefault()
    {
        var samples = Run(0, 12, "wechat.exe", "微信", ContextLevel.Neutral, AppClass.Other, keys: 10);
        Assert.Equal(ActivityState.OffTask,
            Assert.Single(new SegmentBuilder(RuleEngine.Default).Build(samples)).State);

        var withoutWechat = new RuleEngine(
            BuiltinRules.All.Where(r => !string.Equals(r.Pattern, "wechat.exe", StringComparison.OrdinalIgnoreCase)));

        var segment = Assert.Single(new SegmentBuilder(withoutWechat).Build(samples));
        Assert.Equal(ActivityState.ActiveFocus, segment.State);
        Assert.Equal(0.75, segment.Weight, 2);
    }

    [Fact]
    public void UserRuleWithLowerPriorityWinsOverBuiltin()
    {
        var rule = new Rule(9002, RuleMatchType.Process, "chrome.exe",
            ContextLevel.Task, AppClass.Reading, 5, true, false);

        Assert.Equal(ContextLevel.Task, With(rule).Match("chrome.exe", "任意标题").Context);
    }

    [Fact]
    public void LearningKeywordBeatsDistractionApp()
    {
        // B 站默认是分心；标题带「考研」时应判为学习内容
        var rules = RuleEngine.Default;

        Assert.Equal(ContextLevel.Distraction, rules.Match("bilibili.exe", "【日常】今天吃了什么").Context);
        Assert.Equal(ContextLevel.Task, rules.Match("bilibili.exe", "【考研数学】高数基础班 第 12 讲").Context);
        Assert.Equal(ContextLevel.Task, rules.Match("chrome.exe", "哔哩哔哩 线性代数 公开课").Context);
    }

    [Fact]
    public void LearningKeywordMakesVideoBecomeSilentFocus()
    {
        var samples = Run(0, 12, "bilibili.exe", "【考研数学】高数基础班",
            ContextLevel.Neutral, AppClass.Media,
            idleSeconds: 900,
            audioPeak: 0.4,
            media: MediaPlaybackState.Playing);

        var segment = Assert.Single(new SegmentBuilder().Build(samples));

        Assert.Equal(ActivityState.SilentFocus, segment.State);
        Assert.Equal(0.85, segment.Weight, 2);
    }
}
