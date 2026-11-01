using System.Text.RegularExpressions;

namespace FocusLedger.Core;

public enum RuleMatchType
{
    Process,
    TitleContains,
    TitleRegex
}

/// <summary>分类规则。优先级数字越小越先匹配。</summary>
public sealed record Rule(
    int Id,
    RuleMatchType MatchType,
    string Pattern,
    ContextLevel Context,
    AppClass AppClass,
    int Priority,
    bool Enabled = true,
    bool IsBuiltin = true,
    string? Label = null)
{
    public double ContextValue => Context switch
    {
        ContextLevel.Task => 1.0,
        ContextLevel.Neutral => 0.6,
        _ => 0.0
    };
}

public sealed record RuleMatch(
    ContextLevel Context,
    double ContextValue,
    AppClass AppClass,
    string RuleId);

public interface IRuleEngine
{
    RuleMatch Match(string app, string title);
}

public sealed class RuleEngine : IRuleEngine
{
    private readonly List<(Rule Rule, Regex? Regex)> _ordered;

    public RuleEngine(IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _ordered = rules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Select(r => (r, r.MatchType == RuleMatchType.TitleRegex ? TryCompile(r.Pattern) : null))
            .ToList();
    }

    /// <summary>内置规则包。浏览器等双用途应用的区分完全依赖标题规则，是核心资产。</summary>
    public static RuleEngine Default { get; } = new(BuiltinRules.All);

    public RuleMatch Match(string app, string title)
    {
        string appName = app ?? string.Empty;
        string titleText = title ?? string.Empty;

        foreach (var (rule, regex) in _ordered)
        {
            bool hit = rule.MatchType switch
            {
                RuleMatchType.Process =>
                    string.Equals(appName, rule.Pattern, StringComparison.OrdinalIgnoreCase),
                RuleMatchType.TitleContains =>
                    titleText.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
                RuleMatchType.TitleRegex =>
                    regex is not null && regex.IsMatch(titleText),
                _ => false
            };

            if (hit)
            {
                return new RuleMatch(rule.Context, rule.ContextValue, rule.AppClass, $"rule:{rule.Id}");
            }
        }

        return new RuleMatch(ContextLevel.Neutral, 0.6, AppClass.Other, "rule:default");
    }

    private static Regex? TryCompile(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public static class BuiltinRules
{
    public static IReadOnlyList<Rule> All { get; } = Build();

    private static List<Rule> Build()
    {
        var rules = new List<Rule>();
        int id = 1;

        void Add(RuleMatchType type, string pattern, ContextLevel ctx, AppClass cls, int priority, string label)
            => rules.Add(new Rule(id++, type, pattern, ctx, cls, priority, true, true, label));

        // ---- 任务类：打字型（优先级 10） ----
        foreach (var p in new[]
                 {
                     "code.exe", "devenv.exe", "pycharm64.exe", "rstudio.exe", "idea64.exe",
                     "sublime_text.exe", "notepad++.exe", "cursor.exe", "typora.exe", "obsidian.exe",
                     "windowsterminal.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe",
                     "winword.exe", "excel.exe", "powerpnt.exe", "wps.exe", "et.exe", "wpp.exe",
                     "stata.exe", "stata-mp.exe", "spss.exe", "matlab.exe", "jupyter.exe"
                 })
        {
            Add(RuleMatchType.Process, p, ContextLevel.Task, AppClass.Typing, 10, "任务·写作与编码");
        }

        // ---- 任务类：阅读型（优先级 12） ----
        foreach (var p in new[]
                 {
                     "zotero.exe", "acrobat.exe", "acrord32.exe", "sumatrapdf.exe",
                     "foxitreader.exe", "foxitpdfreader.exe", "mupdf.exe", "calibre.exe"
                 })
        {
            Add(RuleMatchType.Process, p, ContextLevel.Task, AppClass.Reading, 12, "任务·文献阅读");
        }

        // ---- 任务类：学术站点标题（优先级 15，先于浏览器中性规则） ----
        foreach (var k in new[]
                 {
                     "Overleaf", "arXiv", "Google Scholar", "PubMed", "Web of Science",
                     "ScienceDirect", "JSTOR", "ResearchGate", "SpringerLink", "Wiley Online",
                     "Taylor & Francis", "SAGE Journals", "IEEE Xplore", "中国知网", "CNKI"
                 })
        {
            Add(RuleMatchType.TitleContains, k, ContextLevel.Task, AppClass.Reading, 15, "任务·学术站点");
        }

        // ---- 分心类：通讯与娱乐应用（优先级 50） ----
        foreach (var p in new[]
                 {
                     "wechat.exe", "weixin.exe", "qq.exe", "telegram.exe", "discord.exe",
                     "slack.exe", "feishu.exe", "lark.exe", "dingtalk.exe",
                     "steam.exe", "vlc.exe", "potplayermini64.exe", "mpc-hc64.exe",
                     "spotify.exe", "cloudmusic.exe", "qqmusic.exe", "kugou.exe", "bilibili.exe"
                 })
        {
            Add(RuleMatchType.Process, p, ContextLevel.Distraction, AppClass.Other, 50, "分心·通讯娱乐");
        }

        // ---- 分心类：娱乐站点标题（优先级 55） ----
        foreach (var k in new[]
                 {
                     "哔哩哔哩", "bilibili", "YouTube", "Netflix", "爱奇艺", "腾讯视频",
                     "优酷", "Twitch", "抖音", "微博", "知乎", "小红书", "Reddit", "Twitter",
                     "淘宝", "京东", "斗鱼", "虎牙"
                 })
        {
            Add(RuleMatchType.TitleContains, k, ContextLevel.Distraction, AppClass.Media, 55, "分心·娱乐站点");
        }

        // ---- 中性：浏览器（优先级 80，最后兜底） ----
        foreach (var p in new[]
                 {
                     "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
                     "360se.exe", "360chrome.exe", "qqbrowser.exe", "safari.exe"
                 })
        {
            Add(RuleMatchType.Process, p, ContextLevel.Neutral, AppClass.Reading, 80, "中性·浏览器");
        }

        // ---- 中性：系统界面（优先级 85） ----
        foreach (var p in new[]
                 {
                     "explorer.exe", "searchhost.exe", "startmenuexperiencehost.exe",
                     "shellexperiencehost.exe", "systemsettings.exe", "taskmgr.exe"
                 })
        {
            Add(RuleMatchType.Process, p, ContextLevel.Neutral, AppClass.Other, 85, "中性·系统");
        }

        return rules;
    }
}
