using System;

namespace FocusLedger.Core;

/// <summary>
/// 身份规则包：同一款软件在不同职业里性质不同（PS 对设计师是工作、对别的专业未必）。
/// 首次启动选定身份后只启用对应那一包，减少误判；换身份后历史会按新规则重算。
/// </summary>
public static class RulePacks
{
    public static readonly (string Id, string LabelZh, string LabelEn)[] All =
    {
        ("general", "通用", "General"),
        ("research", "科研 / 研究生", "Research"),
        ("design", "设计 / 建筑", "Design & Architecture"),
        ("code", "编程 / 开发", "Software"),
        ("writing", "写作 / 编辑", "Writing"),
        ("admin", "行政 / 办公", "Office & Admin")
    };

    private static readonly Dictionary<string, string[]> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["research"] = new[]
        {
            "zotero.exe", "endnote.exe", "mendeley.exe", "jabref.exe",
            "spss.exe", "stata.exe", "stata-mp.exe", "jasp.exe", "rstudio.exe",
            "matlab.exe", "origin.exe", "prism.exe", "graphpad prism.exe",
            "texstudio.exe", "texmaker.exe", "winamp.exe", "jupyter.exe", "anaconda-navigator.exe"
        },
        ["design"] = new[]
        {
            "photoshop.exe", "illustrator.exe", "indesign.exe", "lightroom.exe", "acrobat.exe",
            "rhino.exe", "autocad.exe", "acad.exe", "sketchup.exe", "revit.exe",
            "3dsmax.exe", "blender.exe", "cinema 4d.exe", "c4d.exe",
            "archicad.exe", "vectorworks.exe", "lumion.exe", "enscape.exe", "vray.exe",
            "arcgispro.exe", "arcmap.exe", "qgis.exe", "qgis-ltr-bin.exe"
        },
        ["code"] = new[]
        {
            "code.exe", "cursor.exe", "devenv.exe", "pycharm64.exe", "idea64.exe",
            "webstorm64.exe", "goland64.exe", "rider64.exe", "studio64.exe",
            "sublime_text.exe", "notepad++.exe", "windowsterminal.exe", "wt.exe",
            "docker desktop.exe", "postman.exe", "git-bash.exe", "mintty.exe"
        },
        ["writing"] = new[]
        {
            "winword.exe", "wps.exe", "scrivener.exe", "ulysses.exe",
            "typora.exe", "obsidian.exe", "notion.exe", "evernote.exe",
            "youdaonote.exe", "typora.exe", "zotero.exe"
        },
        ["admin"] = new[]
        {
            "excel.exe", "et.exe", "powerpnt.exe", "wpp.exe", "outlook.exe",
            "zoom.exe", "teams.exe", "acrobat.exe", "onenote.exe", "visio.exe"
        }
    };

    public static string Normalize(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return "general";
        }

        string key = profile.Trim().ToLowerInvariant();
        return Packs.ContainsKey(key) ? key : "general";
    }

    public static string LabelOf(string? profile)
    {
        string key = Normalize(profile);
        foreach (var (id, zh, _) in All)
        {
            if (string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
            {
                return zh;
            }
        }

        return "通用";
    }

    /// <summary>该身份包对应的规则，优先级 11（高于通用写作类，低于学习关键词）。</summary>
    public static IReadOnlyList<Rule> BuildRules(string? profile)
    {
        string key = Normalize(profile);
        if (!Packs.TryGetValue(key, out var apps))
        {
            return Array.Empty<Rule>();
        }

        var rules = new List<Rule>(apps.Length);
        int id = 900_000;
        foreach (string app in apps)
        {
            rules.Add(new Rule(
                id++,
                RuleMatchType.Process,
                app,
                ContextLevel.Task,
                AppClass.Typing,
                Priority: 11,
                Enabled: true,
                IsBuiltin: true,
                Label: $"身份包·{LabelOf(key)}"));
        }

        return rules;
    }
}
