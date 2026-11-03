using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

/// <summary>
/// 意图声明：开始前承诺"接下来 N 分钟做某事"，结束时给回执。
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

    /// <summary>时间进度（已过时间 / 计划时长），蓝色。</summary>
    [ObservableProperty]
    private IReadOnlyList<BarSlice> timeSlices = Array.Empty<BarSlice>();

    [ObservableProperty]
    private string timeProgressText = string.Empty;

    [ObservableProperty]
    private string activeTrack = string.Empty;

    private static string MusicFolder => FocusAudio.Folder;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private string resultText = string.Empty;

    [ObservableProperty]
    private string activeLabel = string.Empty;

    public IReadOnlyList<MusicOption> MusicOptions => FocusAudio.Options;

    [ObservableProperty]
    private MusicOption selectedMusicOption = FocusAudio.Options[0];

    public IReadOnlyList<int> Durations { get; } = new[] { 25, 50, 90 };

    /// <summary>
    /// 剩余时间。超过一小时要带上小时，否则 mm:ss 只取分钟分量——
    /// 90 分钟会被显示成 30:00，看起来像点错了时长。
    /// </summary>
    private static string FormatRemaining(long remainingMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, remainingMs));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    partial void OnPlannedMinutesChanged(int value)
    {
        int clamped = Math.Clamp(value, 5, 600);
        if (clamped != value)
        {
            PlannedMinutes = clamped;
        }
    }

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
            Label = Loc.T("Intent_DefaultLabel");
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
        string text = string.IsNullOrWhiteSpace(Label) ? Loc.T("Intent_DefaultLabel") : Label.Trim();

        string musicId = SelectedMusicOption?.Id ?? "none";
        if (musicId == "folder")
        {
            FocusAudio.PlayFolder(MusicFolder);
            ActiveTrack = FocusAudio.CurrentTrack;
        }
        else
        {
            FocusAudio.Play(musicId);
        }

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

        FocusAudio.Stop();
        _store.FinishIntent(_active.Id, IntentStatus.Abandoned, 0);
        HasResult = true;
        ResultText = Loc.T("Intent_Abandoned", _active.Label);
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
        ActiveLabel = _active.Label;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long remainingMs = _active.PlannedEndUtcMs - now;

        long totalMs = Math.Max(1, _active.PlannedEndUtcMs - _active.StartUtcMs);
        long elapsedMs = Math.Clamp(now - _active.StartUtcMs, 0, totalMs);
        int percent = (int)Math.Round(elapsedMs * 100.0 / totalMs);

        TimeSlices = new[]
        {
            new BarSlice(elapsedMs, BrushOf("StateSilent")),
            new BarSlice(Math.Max(1, totalMs - elapsedMs), Brushes.Transparent)
        };
        TimeProgressText = Loc.T("Intent_TimeProgress", percent);

        Remaining = remainingMs <= 0
            ? Loc.T("Intent_TimeUp")
            : Loc.T("Intent_Remaining", FormatRemaining(remainingMs));

        long until = Math.Min(now, _active.PlannedEndUtcMs);
        var segments = new SegmentBuilder(_rules.Current)
            .Build(_store.LoadSamples(_active.StartUtcMs, until));

        var outcome = IntentEvaluator.Evaluate(_active, segments, until);
        AdherenceText = Loc.T("Intent_Adherence", outcome.Adherence.ToString("P0"));
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
        FocusAudio.Stop();
        ActiveTrack = string.Empty;
        if (_active is null)
        {
            return;
        }

        var segments = new SegmentBuilder(_rules.Current)
            .Build(_store.LoadSamples(_active.StartUtcMs, _active.PlannedEndUtcMs));

        var outcome = IntentEvaluator.Evaluate(_active, segments, _active.PlannedEndUtcMs);
        _store.FinishIntent(_active.Id, IntentStatus.Completed, outcome.Adherence);

        string suffix = automatic ? Loc.T("Intent_OnTime") : Loc.T("Intent_Early");
        string caution = outcome.UntrackedRatio > 0.3 ? Loc.T("Intent_Untracked") : string.Empty;

        ResultText = Loc.T(
            "Intent_Result",
            _active.Label,
            (_active.PlannedSeconds / 60).ToString("F0"),
            outcome.Adherence.ToString("P0"),
            suffix,
            caution);

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

    private Brush BrushOf(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
