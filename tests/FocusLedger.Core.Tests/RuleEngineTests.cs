using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

public sealed class RuleEngineTests
{
    private readonly RuleEngine _rules = RuleEngine.Default;

    [Theory]
    [InlineData("chrome.exe", "YouTube - 视频", ContextLevel.Distraction)]
    [InlineData("chrome.exe", "哔哩哔哩 (゜-゜)つロ 干杯~", ContextLevel.Distraction)]
    [InlineData("chrome.exe", "Overleaf - 论文 - Online LaTeX Editor", ContextLevel.Task)]
    [InlineData("chrome.exe", "arXiv:2401.00001 Attention", ContextLevel.Task)]
    [InlineData("chrome.exe", "某个普通网页", ContextLevel.Neutral)]
    [InlineData("winword.exe", "论文.docx - Word", ContextLevel.Task)]
    [InlineData("wechat.exe", "微信", ContextLevel.Distraction)]
    [InlineData("unknown-app.exe", "某窗口", ContextLevel.Neutral)]
    public void Match_ReturnsExpectedContext(string app, string title, ContextLevel expected)
    {
        Assert.Equal(expected, _rules.Match(app, title).Context);
    }

    [Fact]
    public void Match_ProcessRuleBeatsTitleRuleForSameApp()
    {
        // Word 里打开一个标题含 "YouTube" 的文档，仍应算任务，而不是被标题规则误伤
        var match = _rules.Match("winword.exe", "关于 YouTube 传播机制的论文.docx");
        Assert.Equal(ContextLevel.Task, match.Context);
    }

    [Fact]
    public void Match_BrowserNeutralIsLastResort()
    {
        var match = _rules.Match("chrome.exe", "no keywords here");
        Assert.Equal(ContextLevel.Neutral, match.Context);
        Assert.Equal(0.6, match.ContextValue, 3);
    }
}
