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
        // 样本采集时被记为"中性"，但判定阶段应以当前规则为准
        var samples = Run(0, 12, "msedge.exe", "某个页面", ContextLevel.Neutral, AppClass.Reading, keys: 10);

        var neutralSegment = Assert.Single(new SegmentBuilder(RuleEngine.Default).Build(samples));
        Assert.Equal(ActivityState.ActiveFocus, neutralSegment.State);
        Assert.Equal(0.60, neutralSegment.Weight, 2);

        // 新增一条"msedge.exe 算任务"的规则后，同一批历史样本应改判为深度专注
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
        Assert.Equal(ActivityState.ActiveFocus, taskSegment.State);
        Assert.Equal(1.00, taskSegment.Weight, 2);
    }

    [Fact]
    public void DisablingBuiltinRule_FallsBackToDefault()
    {
        // wechat.exe 默认是分心
        var samples = Run(0, 12, "wechat.exe", "微信", ContextLevel.Neutral, AppClass.Other, keys: 10);
        Assert.Equal(ActivityState.OffTask, Assert.Single(new SegmentBuilder(RuleEngine.Default).Build(samples)).State);

        // 去掉所有 wechat 规则后，落到默认中性，有输入就是 active 0.6
        var withoutWechat = new RuleEngine(
            BuiltinRules.All.Where(r => !string.Equals(r.Pattern, "wechat.exe", StringComparison.OrdinalIgnoreCase)));

        var segment = Assert.Single(new SegmentBuilder(withoutWechat).Build(samples));
        Assert.Equal(ActivityState.ActiveFocus, segment.State);
        Assert.Equal(0.60, segment.Weight, 2);
    }

    [Fact]
    public void UserRuleWithLowerPriorityWinsOverBuiltin()
    {
        // 给浏览器加一条"任务"规则，优先级 5 < 内置中性规则的 80
        var rule = new Rule(9002, RuleMatchType.Process, "chrome.exe",
            ContextLevel.Task, AppClass.Reading, 5, true, false);

        var match = With(rule).Match("chrome.exe", "任意标题");
        Assert.Equal(ContextLevel.Task, match.Context);
    }
}
