using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            RuleMatchType.Process => $"进程  {rule.Pattern}",
            RuleMatchType.TitleContains => $"标题含  {rule.Pattern}",
            _ => $"正则  {rule.Pattern}"
        };

        SourceText = rule.IsBuiltin ? "内置" : "自定义";
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
        ContextLevel.Task => "任务",
        ContextLevel.Neutral => "中性",
        _ => "分心"
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
    private string currentApp = "等待采样…";

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

        var current = _recording.Current;
        CurrentApp = current is null
            ? "等待采样…"
            : $"{current.App}  ·  {Trim(current.Title, 50)}";
    }

    [RelayCommand]
    private void NewRuleFromCurrent()
    {
        var current = _recording.Current;
        if (current is null || string.IsNullOrEmpty(current.App))
        {
            Status = "还没采到当前应用，等几秒再试。";
            return;
        }

        var existing = _rules.FindProcessRule(current.App);
        if (existing is { IsBuiltin: false })
        {
            Status = $"已经有一条针对 {current.App} 的自定义规则了，直接改它的类别即可。";
            return;
        }

        _rules.AddRule(
            RuleMatchType.Process,
            current.App,
            ContextLevel.Task,
            AppClass.Typing,
            priority: 5,
            label: $"自定义 · {current.App}");

        Status = $"已为 {current.App} 新建规则：任务（优先级 5，高于所有内置规则）。";
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
            Status = "内置规则不能直接改类别。点「用当前窗口新建规则」，新规则的优先级更高，会盖过内置规则。";
            return;
        }

        var next = row.Category switch
        {
            ContextLevel.Task => ContextLevel.Neutral,
            ContextLevel.Neutral => ContextLevel.Distraction,
            _ => ContextLevel.Task
        };

        _rules.SetCategory(row.Rule, next);
        Status = $"{row.Rule.Pattern} → {next}";
    }

    [RelayCommand]
    private void Delete(RuleRow? row)
    {
        if (row is null || row.IsBuiltin)
        {
            return;
        }

        _rules.Delete(row.Rule);
        Status = $"已删除规则：{row.Rule.Pattern}";
        Reload();
    }

    private void OnEnabledChanged(RuleRow row)
    {
        _rules.SetEnabled(row.Rule, row.Enabled);
        Status = $"{(row.Enabled ? "已启用" : "已停用")}：{row.Rule.Pattern}";
    }

    private void OnCategoryChanged(RuleRow row)
    {
        if (!row.IsUser)
        {
            return;
        }

        _rules.SetCategory(row.Rule, row.Category);
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? "(无标题)"
        : value.Length <= max ? value
        : value[..(max - 1)] + "…";
}
