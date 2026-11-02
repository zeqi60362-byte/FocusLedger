using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Core;

namespace FocusLedger.App.ViewModels;

public sealed record StateChip(string Label, string Value, System.Windows.Media.Brush Fill);

public sealed partial class TodayViewModel : ObservableObject
{
    private readonly RecordingService _recording;
    private readonly RuleService _rules;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private IReadOnlyList<Segment> segments = Array.Empty<Segment>();

    [ObservableProperty]
    private long windowStart;

    [ObservableProperty]
    private long windowEnd;

    [ObservableProperty]
    private string headline = "—";

    [ObservableProperty]
    private string band = string.Empty;

    [ObservableProperty]
    private string breakdown = string.Empty;

    [ObservableProperty]
    private string secondary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<StateChip> chips = Array.Empty<StateChip>();

    [ObservableProperty]
    private string current = string.Empty;

    [ObservableProperty]
    private string detail = string.Empty;

    [ObservableProperty]
    private string coverage = string.Empty;

    [ObservableProperty]
    private bool isRecording;

    [ObservableProperty]
    private string zoom = "hour";

    public bool IsHourZoom => Zoom == "hour";

    public bool IsSixZoom => Zoom == "six";

    public bool IsDayZoom => Zoom == "day";

    partial void OnZoomChanged(string value)
    {
        OnPropertyChanged(nameof(IsHourZoom));
        OnPropertyChanged(nameof(IsSixZoom));
        OnPropertyChanged(nameof(IsDayZoom));
    }

    [ObservableProperty]
    private string recordButtonText = string.Empty;

    [ObservableProperty]
    private string dateLabel = string.Empty;

    private Segment? _selected;

    public IntentViewModel Intent { get; }

    public TodayViewModel(RecordingService recording, RuleService rules, IntentViewModel intent)
    {
        _recording = recording;
        _rules = rules;
        Intent = intent;

        ApplyZoom("hour");
        Refresh();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    [RelayCommand]
    private void SetZoom(string? mode)
    {
        Zoom = mode ?? "day";
        ApplyZoom(Zoom);
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        await _recording.ToggleAsync().ConfigureAwait(true);
        Refresh();
    }

    private static System.Windows.Media.Brush StateBrush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    public void Refresh()
    {
        IsRecording = _recording.IsRecording;
        RecordButtonText = IsRecording ? Loc.T("Today_Pause") : Loc.T("Today_Resume");

        var localization = Localization.Localization.Instance;
        DateLabel = DateTime.Now.ToString(
            localization.IsEnglish ? "dddd, MMM d, yyyy" : "yyyy 年 M 月 d 日 dddd",
            localization.Culture);

        var samples = _recording.SnapshotToday();
        var segments = new SegmentBuilder(_rules.Current).Build(samples);
        Segments = segments;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var summary = DailyAggregator.Aggregate(segments, today, TimeZoneInfo.Local);

        Headline = Fmt.Duration(summary.EffectiveSeconds);
        Band = Loc.T("Today_Band",
            Fmt.Duration(summary.EffectiveLowSeconds),
            Fmt.Duration(summary.EffectiveHighSeconds));
        Chips = new[]
        {
            new StateChip(Loc.T("State_ActiveFull"), Fmt.Duration(summary.ActiveSeconds), StateBrush("StateActive")),
            new StateChip(Loc.T("State_OffTaskFull"), Fmt.Duration(summary.OffTaskSeconds), StateBrush("StateOffTask")),
            new StateChip(Loc.T("State_AwayFull"), Fmt.Duration(summary.AwaySeconds), StateBrush("StateIdle")),
            new StateChip(Loc.T("State_SilentFull"), Fmt.Duration(summary.SilentSeconds), StateBrush("StateSilent"))
        };
        Breakdown = string.Empty;
        Secondary = string.Empty;

        string top = summary.TopDistractions.Count > 0
            ? "  ·  " + string.Join(" / ", summary.TopDistractions.Select(d => $"{d.App} {Fmt.Duration(d.Seconds)}"))
            : string.Empty;

        Coverage = Loc.T("Today_Coverage",
            samples.Count,
            Fmt.Duration(segments.Sum(s => s.DurationSeconds)),
            summary.FocusBlocks,
            top);

        var currentSample = _recording.Current;
        Current = currentSample is null
            ? Loc.T("Today_Waiting")
            : $"{currentSample.App}  ·  {Trim(currentSample.Title, 60)}";

        Detail = RenderDetail();

        if (Zoom is "hour" or "six")
        {
            ApplyZoom(Zoom);
        }
        else
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (dayStart, dayEnd) = DailyAggregator.DayRangeUtc(today, TimeZoneInfo.Local);
            WindowStart = dayStart;
            WindowEnd = Math.Max(dayEnd, now + 60_000);
        }

        Intent.Refresh();
    }

    public void ShowDetail(Segment segment)
    {
        _selected = segment;
        Detail = RenderDetail();
    }

    public void ResetDetail()
    {
        _selected = null;
        Detail = RenderDetail();
    }

    private string RenderDetail()
    {
        if (_selected is null)
        {
            return Loc.T("Today_DetailPlaceholder");
        }

        return Loc.T("Today_Detail",
            Fmt.Clock(_selected.StartUtcMs),
            Fmt.Clock(_selected.EndUtcMs),
            TimelineControl.StateName(_selected.State),
            Fmt.Duration(_selected.DurationSeconds),
            _selected.App,
            Trim(_selected.Title, 90));
    }

    private void ApplyZoom(string mode)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (dayStart, dayEnd) = DailyAggregator.DayRangeUtc(
            DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local);

        switch (mode)
        {
            case "hour":
                WindowStart = now - 3_600_000;
                WindowEnd = now + 300_000;
                break;
            case "six":
                WindowStart = Math.Max(now - 6 * 3_600_000, dayStart);
                WindowEnd = Math.Min(now + 300_000, dayEnd);
                break;
            default:
                WindowStart = dayStart;
                WindowEnd = dayEnd;
                break;
        }
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? Loc.T("Today_NoTitle")
        : value.Length <= max ? value
        : value[..(max - 1)] + "…";
}
