using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

/// <summary>
/// 意图声明：开始前承诺"接下来 N 分钟做某事"，结束时给回执。
/// 它和对账是同一个「声称 vs 实证」框架，只是把时间点从事后提前到事前。
/// </summary>
public sealed partial class IntentViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly RuleService _rules;

    private FocusIntent? _active;

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private bool isEditing;

    [ObservableProperty]
    private string label = string.Empty;

    [ObservableProperty]
    private int plannedMinutes = 50;

    [ObservableProperty]
    private string remaining = string.Empty;

    [ObservableProperty]
    private string adherenceText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BarSlice> progressSlices = Array.Empty<BarSlice>();

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private string resultText = string.Empty;

    public IReadOnlyList<int> Durations { get; } = new[] { 25, 50, 90 };

    public IntentViewModel(FocusLedgerStore store, RuleService rules)
    {
        _store = store;
        _rules = rules;
        Refresh();
    }

    public bool ShowIdle => !IsActive && !IsEditing;

    public bool ShowEditor => IsEditing;

    public bool ShowActive => IsActive;

    [RelayCommand]
    private void BeginEdit()
    {
        IsEditing = true;
        if (string.IsNullOrWhiteSpace(Label))
        {
            Label = "写论文";
        }
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SetDuration(int minutes) => PlannedMinutes = minutes;

    [RelayCommand]
    private void Start()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long end = now + PlannedMinutes * 60_000L;
        string text = string.IsNullOrWhiteSpace(Label) ? "专注" : Label.Trim();

        long id = _store.CreateIntent(now, end, text);

        _active = new FocusIntent((int)id, now, end, text, IntentStatus.Running, null);
        IsEditing = false;
        IsActive = true;
        HasResult = false;
        ResultText = string.Empty;

        Refresh();
    }

    [RelayCommand]
    private void Finish() => Complete(automatic: false);

    [RelayCommand]
    private void Abandon()
    {
        if (_active is null)
        {
            return;
        }

        _store.FinishIntent(_active.Id, IntentStatus.Abandoned, 0);
        HasResult = true;
        ResultText = $"{_active.Label} · 已放弃";
        ClearActive();
    }

    public void Refresh()
    {
        _active ??= _store.LoadRunningIntent();

        if (_active is null)
        {
            if (IsActive)
            {
                ClearActive();
            }

            return;
        }

        IsActive = true;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long remainingMs = _active.PlannedEndUtcMs - now;

        Remaining = remainingMs <= 0
            ? "时间到"
            : $"还剩 {TimeSpan.FromMilliseconds(remainingMs):mm\\:ss}";

        long until = Math.Min(now, _active.PlannedEndUtcMs);
        var segments = new SegmentBuilder(_rules.Current)
            .Build(_store.LoadSamples(_active.StartUtcMs, until));

        var outcome = IntentEvaluator.Evaluate(_active, segments, until);
        AdherenceText = $"已遵守 {outcome.Adherence:P0}";
        ProgressSlices = new[]
        {
            new BarSlice(Math.Max(0.0001, outcome.Adherence), Res("StateActive")),
            new BarSlice(Math.Max(0.0001, 1 - outcome.Adherence), Brushes.Transparent)
        };

        if (remainingMs <= 0)
        {
            Complete(automatic: true);
        }
    }

    private void Complete(bool automatic)
    {
        if (_active is null)
        {
            return;
        }

        var segments = new SegmentBuilder(_rules.Current)
            .Build(_store.LoadSamples(_active.StartUtcMs, _active.PlannedEndUtcMs));

        var outcome = IntentEvaluator.Evaluate(_active, segments, _active.PlannedEndUtcMs);
        _store.FinishIntent(_active.Id, IntentStatus.Completed, outcome.Adherence);

        string suffix = automatic ? "· 到时" : "· 提前结束";
        string caution = outcome.UntrackedRatio > 0.3
            ? "（其中相当一部分没有采集数据，这个数字不太可信）"
            : string.Empty;

        ResultText =
            $"{_active.Label} · {_active.PlannedSeconds / 60:F0} 分钟，遵守 {outcome.Adherence:P0} {suffix}{caution}";

        HasResult = true;
        ClearActive();
    }

    private void ClearActive()
    {
        _active = null;
        IsActive = false;
        Remaining = string.Empty;
        AdherenceText = string.Empty;
        ProgressSlices = Array.Empty<BarSlice>();
    }

    private static Brush Res(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowEditor));
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowActive));
    }
}
