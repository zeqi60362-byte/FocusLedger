using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Services;
using FocusLedger.Core;

namespace FocusLedger.App.ViewModels;

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
    private string band = "还没有数据";

    [ObservableProperty]
    private string breakdown = "";

    [ObservableProperty]
    private string secondary = "";

    [ObservableProperty]
    private string current = "等待采样…";

    [ObservableProperty]
    private string detail = "点击时间轴上的任意一段，查看它为什么被这样判定。";

    [ObservableProperty]
    private string coverage = "";

    [ObservableProperty]
    private bool isRecording;

    [ObservableProperty]
    private string zoom = "day";

    [ObservableProperty]
    private string recordButtonText = "开始记录";

    public TodayViewModel(RecordingService recording, RuleService rules)
    {
        _recording = recording;
        _rules = rules;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        ApplyZoom("day");
        Refresh();
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

    public void Refresh()
    {
        IsRecording = _recording.IsRecording;
        RecordButtonText = IsRecording ? "暂停记录" : "开始记录";

        var samples = _recording.SnapshotToday();
        var segments = new SegmentBuilder(_rules.Current).Build(samples);
        Segments = segments;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var summary = DailyAggregator.Aggregate(segments, today, TimeZoneInfo.Local);

        Headline = Fmt.Duration(summary.EffectiveSeconds);
        Band = $"区间 {Fmt.Duration(summary.EffectiveLowSeconds)} – {Fmt.Duration(summary.EffectiveHighSeconds)}";
        Breakdown =
            $"深度 {Fmt.Duration(summary.ActiveSeconds)}     被动 {Fmt.Duration(summary.PassiveSeconds)}     静默 {Fmt.Duration(summary.SilentSeconds)}";
        Secondary =
            $"分心 {Fmt.Duration(summary.OffTaskSeconds)}     离席 {Fmt.Duration(summary.IdleSeconds)}     锁屏 {Fmt.Duration(summary.LockedSeconds)}     存疑 {Fmt.Duration(summary.AmbiguousSeconds)}";

        string top = summary.TopDistractions.Count > 0
            ? "  ·  " + string.Join(" / ", summary.TopDistractions.Select(d => $"{d.App} {Fmt.Duration(d.Seconds)}"))
            : string.Empty;

        Coverage =
            $"样本 {samples.Count} 条  ·  覆盖 {Fmt.Duration(segments.Sum(s => s.DurationSeconds))}  ·  深度工作块 {summary.FocusBlocks} 次{top}";

        var currentSample = _recording.Current;
        Current = currentSample is null
            ? "等待采样…"
            : $"{currentSample.App}  ·  {Trim(currentSample.Title, 60)}";

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
    }

    public void ShowDetail(Segment segment)
    {
        string reasons = segment.CorrectedBy switch
        {
            "return" => "（日终按「返回修正」重判：短时离开后又回到同一应用）",
            _ => string.Empty
        };

        Detail =
            $"{Fmt.Clock(segment.StartUtcMs)} – {Fmt.Clock(segment.EndUtcMs)}    " +
            $"{TimelineControl.StateName(segment.State)}    {Fmt.Duration(segment.DurationSeconds)}\n" +
            $"应用：{segment.App}    分类：{segment.AppClass}\n" +
            $"标题：{Trim(segment.Title, 90)}\n" +
            $"权重：{segment.Weight:F2}（区间 {segment.WeightLow:F2} – {segment.WeightHigh:F2}）    " +
            $"置信：{segment.Confidence}    规则：{segment.RuleId}{reasons}";
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
        string.IsNullOrEmpty(value) ? "(无标题)"
        : value.Length <= max ? value
        : value[..(max - 1)] + "…";
}
