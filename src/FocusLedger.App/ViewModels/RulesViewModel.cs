using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Core;

namespace FocusLedger.App.ViewModels;

public sealed partial class RuleRow : ObservableObject
{
    private readonly Action<RuleRow> _enabledChanged;
    private readonly Action<RuleRow> _categoryChanged;
    private readonly bool _ready;

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private ContextLevel category;

    public RuleRow(Rule rule, Action<RuleRow> enabledChanged, Action<RuleRow> categoryChanged)
    {
        Rule = rule;
        _enabledChanged = enabledChanged;
        _categoryChanged = categoryChanged;

        MatchText = rule.MatchType switch
        {
            RuleMatchType.Process => Loc.T("Rules_Process", rule.Pattern),
            RuleMatchType.TitleContains => Loc.T("Rules_Contains", rule.Pattern),
            _ => Loc.T("Rules_Regex", rule.Pattern)
        };

        SourceText = rule.IsBuiltin ? Loc.T("Rules_Builtin") : Loc.T("Rules_Custom");
        LabelText = string.IsNullOrWhiteSpace(rule.Label) ? "—" : rule.Label;
        PriorityText = rule.Priority.ToString();

        Enabled = rule.Enabled;
        Category = rule.Context;
        _ready = true;
    }

    public Rule Rule { get; }

    public string MatchText { get; }

    public string SourceText { get; }

    public string LabelText { get; }

    public string PriorityText { get; }

    public bool IsBuiltin => Rule.IsBuiltin;

    public bool IsUser => !Rule.IsBuiltin;

    public string CategoryText => Category switch
    {
        ContextLevel.Task => Loc.T("Cat_Task"),
        ContextLevel.Neutral => Loc.T("Cat_Neutral"),
        _ => Loc.T("Cat_Distraction")
    };

    public Brush CategoryBrush => Category switch
    {
        ContextLevel.Task => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        ContextLevel.Neutral => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94)),
        _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
    };

    public Brush CategoryBackground => Category switch
    {
        ContextLevel.Task => new SolidColorBrush(Color.FromArgb(0x33, 0x4A, 0xDE, 0x80)),
        ContextLevel.Neutral => new SolidColorBrush(Color.FromArgb(0x26, 0x8A, 0x8A, 0x94)),
        _ => new SolidColorBrush(Color.FromArgb(0x33, 0xF8, 0x71, 0x71))
    };

    partial void OnEnabledChanged(bool value)
    {
        if (_ready)
        {
            _enabledChanged(this);
        }
    }

    partial void OnCategoryChanged(ContextLevel value)
    {
        if (!_ready)
        {
            return;
        }

        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(CategoryBrush));
        OnPropertyChanged(nameof(CategoryBackground));
        _categoryChanged(this);
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly RuleService _rules;
    private readonly RecordingService _recording;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private string currentApp = string.Empty;

    public ObservableCollection<RuleRow> Rows { get; } = new();

    public RulesViewModel(RuleService rules, RecordingService recording)
    {
        _rules = rules;
        _recording = recording;
        Reload();
    }

    public void Reload()
    {
        Rows.Clear();

        foreach (var rule in _rules.EffectiveRules)
        {
            Rows.Add(new RuleRow(rule, OnEnabledChanged, OnCategoryChanged));
        }

        RefreshRecents();
        var current = _recording.Current;
        CurrentApp = current is null
            ? Loc.T("Today_Waiting")
            : $"{current.App}  ·  {Trim(current.Title, 50)}";
    }

    public ObservableCollection<string> Recents { get; } = new();

    [ObservableProperty]
    private string selectedRecent = string.Empty;

    private readonly Dictionary<string, Sample> _recentMap = new(StringComparer.Ordinal);

    private void RefreshRecents()
    {
        Recents.Clear();
        _recentMap.Clear();

        foreach (var sample in _recording.RecentWindows())
        {
            string label = $"{sample.App}  ·  {Trim(sample.Title, 46)}";
            if (_recentMap.ContainsKey(label))
            {
                continue;
            }

            _recentMap[label] = sample;
            Recents.Add(label);
        }

        if (Recents.Count > 0 && !Recents.Contains(SelectedRecent))
        {
            SelectedRecent = Recents[0];
        }
    }

    [RelayCommand]
    private void NewRuleFromCurrent()
    {
        _recentMap.TryGetValue(SelectedRecent ?? string.Empty, out var picked);
        var current = picked ?? _recording.Current;
        if (current is null || string.IsNullOrEmpty(current.App))
        {
            Status = Loc.T("Rules_NoSample");
            return;
        }

        var existing = _rules.FindProcessRule(current.App);

        // 这个应用已经有进程级规则时，改成按标题关键词建一条，否则同一款浏览器
        // 只能整体设成分心（看视频和查文献会被一锅端）。
        if (existing is not null)
        {
            string keyword = ExtractionKeyword(current.Title);
            if (keyword.Length < 2)
            {
                Status = Loc.T("Rules_Exists", current.App);
                return;
            }

            if (_rules.EffectiveRules.Any(r => r.MatchType == RuleMatchType.TitleContains
                    && string.Equals(r.Pattern, keyword, StringComparison.OrdinalIgnoreCase)))
            {
                Status = Loc.T("Rules_Exists", keyword);
                return;
            }

            _rules.AddRule(
                RuleMatchType.TitleContains,
                keyword,
                ContextLevel.Distraction,
                AppClass.Other,
                priority: 4,
                label: keyword);

            Status = Loc.T("Rules_Created", keyword);
            Reload();
            return;
        }

        _rules.AddRule(
            RuleMatchType.Process,
            current.App,
            ContextLevel.Task,
            AppClass.Typing,
            priority: 5,
            label: current.App);

        Status = Loc.T("Rules_Created", current.App);
        Reload();
    }

    [RelayCommand]
    private void CycleCategory(RuleRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsBuiltin)
        {
            Status = Loc.T("Rules_BuiltinReadonly");
            return;
        }

        var next = row.Category switch
        {
            ContextLevel.Task => ContextLevel.Neutral,
            ContextLevel.Neutral => ContextLevel.Distraction,
            _ => ContextLevel.Task
        };

        _rules.SetCategory(row.Rule, next);
        Status = Loc.T("Rules_CategoryChanged", row.Rule.Pattern, CategoryName(next));
    }

    [RelayCommand]
    private void Delete(RuleRow? row)
    {
        if (row is null || row.IsBuiltin)
        {
            return;
        }

        _rules.Delete(row.Rule);
        Status = Loc.T("Rules_Deleted", row.Rule.Pattern);
        Reload();
    }

    private void OnEnabledChanged(RuleRow row)
    {
        _rules.SetEnabled(row.Rule, row.Enabled);
        Status = Loc.T(
            "Rules_Toggled",
            row.Rule.Pattern,
            Loc.T(row.Enabled ? "Rules_On" : "Rules_Off"));
    }

    private void OnCategoryChanged(RuleRow row)
    {
        if (!row.IsUser)
        {
            return;
        }

        _rules.SetCategory(row.Rule, row.Category);
    }

    private static string CategoryName(ContextLevel level) => level switch
    {
        ContextLevel.Task => Loc.T("Cat_Task"),
        ContextLevel.Neutral => Loc.T("Cat_Neutral"),
        _ => Loc.T("Cat_Distraction")
    };

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? Loc.T("Today_NoTitle")
        : value.Length <= max ? value
        : value[..(max - 1)] + "…";
    /// <summary>从窗口标题里取一个能代表当前页面的关键词：去掉浏览器后缀与「和另外 N 个页面」。</summary>
    private static string ExtractionKeyword(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string text = title.Trim();

        int extra = text.IndexOf(" 和另外", StringComparison.Ordinal);
        if (extra > 4)
        {
            text = text[..extra];
        }

        int dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 6)
        {
            text = text[..dash];
        }

        text = text.Trim();
        return text.Length > 40 ? text[..40] : text;
    }
}
