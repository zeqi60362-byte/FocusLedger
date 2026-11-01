using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed record DistractionRow(string App, string Duration, string Share);

public sealed partial class ReportViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly SegmentBuilder _builder = new();

    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);

    [ObservableProperty]
    private string dateText = string.Empty;

    [ObservableProperty]
    private string effective = "—";

    [ObservableProperty]
    private string band = string.Empty;

    [ObservableProperty]
    private string primaryRow = string.Empty;

    [ObservableProperty]
    private string secondaryRow = string.Empty;

    [ObservableProperty]
    private string statsRow = string.Empty;

    [ObservableProperty]
    private string emptyHint = string.Empty;

    [ObservableProperty]
    private bool hasData;

    public ObservableCollection<DistractionRow> Distractions { get; } = new();

    public ReportViewModel(FocusLedgerStore store)
    {
        _store = store;
        Load();
    }

    [RelayCommand]
    private void PreviousDay()
    {
        _date = _date.AddDays(-1);
        Load();
    }

    [RelayCommand]
    private void NextDay()
    {
        if (_date >= DateOnly.FromDateTime(DateTime.Now))
        {
            return;
        }

        _date = _date.AddDays(1);
        Load();
    }

    [RelayCommand]
    private void Today()
    {
        _date = DateOnly.FromDateTime(DateTime.Now);
        Load();
    }

    public void Load()
    {
        DateText = _date.ToString("yyyy 年 M 月 d 日");

        var (start, end) = DailyAggregator.DayRangeUtc(_date, TimeZoneInfo.Local);
        var samples = _store.LoadSamples(start, end);

        Distractions.Clear();

        if (samples.Count == 0)
        {
            HasData = false;
            Effective = "—";
            Band = string.Empty;
            PrimaryRow = string.Empty;
            SecondaryRow = string.Empty;
            StatsRow = string.Empty;
            EmptyHint = "这一天还没有数据。";
            return;
        }

        HasData = true;
        EmptyHint = string.Empty;

        var segments = _builder.Build(samples);
        var summary = DailyAggregator.Aggregate(segments, _date, TimeZoneInfo.Local);

        Effective = Fmt.Duration(summary.EffectiveSeconds);
        Band = $"区间 {Fmt.Duration(summary.EffectiveLowSeconds)} – {Fmt.Duration(summary.EffectiveHighSeconds)}";
        PrimaryRow =
            $"深度 {Fmt.Duration(summary.ActiveSeconds)}     被动 {Fmt.Duration(summary.PassiveSeconds)}     静默 {Fmt.Duration(summary.SilentSeconds)}";
        SecondaryRow =
            $"分心 {Fmt.Duration(summary.OffTaskSeconds)}     离席 {Fmt.Duration(summary.IdleSeconds)}     锁屏 {Fmt.Duration(summary.LockedSeconds)}     存疑 {Fmt.Duration(summary.AmbiguousSeconds)}";
        StatsRow =
            $"深度工作块 {summary.FocusBlocks} 次（最长 {summary.LongestFocusBlockSeconds / 60:F0} 分钟）    " +
            $"应用切换 {summary.AppSwitches} 次    样本 {samples.Count} 条";

        double totalOffTask = summary.OffTaskSeconds;
        foreach (var usage in summary.TopDistractions)
        {
            string share = totalOffTask > 0 ? $"{usage.Seconds / totalOffTask:P0}" : "—";
            Distractions.Add(new DistractionRow(usage.App, Fmt.Duration(usage.Seconds), share));
        }
    }
}
