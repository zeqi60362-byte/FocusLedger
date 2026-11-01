using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed record DistractionRow(string App, string Duration, string Share);

public sealed record StatRow(string Label, string Effective, string Detail, IReadOnlyList<BarSlice> Bar);

public sealed partial class ReportViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly RuleService _rules;

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

    [ObservableProperty]
    private string statsPeriod = "week";

    [ObservableProperty]
    private string statsSummary = string.Empty;

    public ObservableCollection<DistractionRow> Distractions { get; } = new();

    public ObservableCollection<StatRow> Stats { get; } = new();

    public ReportViewModel(FocusLedgerStore store, RuleService rules)
    {
        _store = store;
        _rules = rules;
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

    [RelayCommand]
    private void SetPeriod(string? period)
    {
        StatsPeriod = period ?? "week";
        BuildStats();
    }

    public void Load()
    {
        DateText = _date.ToString("yyyy 年 M 月 d 日");

        var summary = Summarize(_date);
        Distractions.Clear();

        if (summary is null)
        {
            HasData = false;
            Effective = "—";
            Band = string.Empty;
            PrimaryRow = string.Empty;
            SecondaryRow = string.Empty;
            StatsRow = string.Empty;
            EmptyHint = "这一天还没有数据。";
        }
        else
        {
            HasData = true;
            EmptyHint = string.Empty;

            Effective = Fmt.Duration(summary.EffectiveSeconds);
            Band = $"区间 {Fmt.Duration(summary.EffectiveLowSeconds)} – {Fmt.Duration(summary.EffectiveHighSeconds)}";
            PrimaryRow =
                $"深度 {Fmt.Duration(summary.ActiveSeconds)}     静默 {Fmt.Duration(summary.SilentSeconds)}";
            SecondaryRow =
                $"分心 {Fmt.Duration(summary.OffTaskSeconds)}     离席 {Fmt.Duration(summary.AwaySeconds)}";
            StatsRow =
                $"深度工作块 {summary.FocusBlocks} 次（最长 {summary.LongestFocusBlockSeconds / 60:F0} 分钟）    " +
                $"应用切换 {summary.AppSwitches} 次";

            double totalOffTask = summary.OffTaskSeconds;
            foreach (var usage in summary.TopDistractions)
            {
                string share = totalOffTask > 0 ? $"{usage.Seconds / totalOffTask:P0}" : "—";
                Distractions.Add(new DistractionRow(usage.App, Fmt.Duration(usage.Seconds), share));
            }
        }

        BuildStats();
    }

    // ---------- 统计 ----------

    private void BuildStats()
    {
        Stats.Clear();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var buckets = new List<(string Label, DateOnly From, DateOnly To)>();

        switch (StatsPeriod)
        {
            case "month":
                for (int i = 5; i >= 0; i--)
                {
                    var anchor = today.AddMonths(-i);
                    var from = new DateOnly(anchor.Year, anchor.Month, 1);
                    buckets.Add((anchor.ToString("M 月"), from, from.AddMonths(1).AddDays(-1)));
                }

                break;

            case "year":
                for (int i = 4; i >= 0; i--)
                {
                    var anchor = today.AddYears(-i);
                    var from = new DateOnly(anchor.Year, 1, 1);
                    buckets.Add((anchor.ToString("yyyy 年"), from, from.AddYears(1).AddDays(-1)));
                }

                break;

            default:
                for (int i = 6; i >= 0; i--)
                {
                    var day = today.AddDays(-i);
                    buckets.Add((day.ToString("M/d"), day, day));
                }

                break;
        }

        var values = buckets
            .Select(b => (b.Label, Summary: SumRange(b.From, min(b.To, today))))
            .ToList();

        double max = values.Count == 0 ? 0 : values.Max(v => v.Summary.EffectiveSeconds);
        if (max <= 0)
        {
            max = 1;
        }

        double total = values.Sum(v => v.Summary.EffectiveSeconds);

        foreach (var (label, summary) in values)
        {
            string detail =
                $"深度 {Fmt.Duration(summary.ActiveSeconds)}   静默 {Fmt.Duration(summary.SilentSeconds)}   " +
                $"分心 {Fmt.Duration(summary.OffTaskSeconds)}";

            Stats.Add(new StatRow(
                label,
                summary.EffectiveSeconds > 0 ? Fmt.Duration(summary.EffectiveSeconds) : "—",
                detail,
                new[]
                {
                    new BarSlice(Math.Max(0.001, summary.EffectiveSeconds), Brush("StateActive")),
                    new BarSlice(Math.Max(0.001, max - summary.EffectiveSeconds), Brushes.Transparent)
                }));
        }

        StatsSummary = $"{PeriodName()}合计有效 {Fmt.Duration(total)}";
    }

    private DailySummary SumRange(DateOnly from, DateOnly to)
    {
        double active = 0, silent = 0, offTask = 0, away = 0, effective = 0, low = 0, high = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var summary = Summarize(day);
            if (summary is null)
            {
                continue;
            }

            active += summary.ActiveSeconds;
            silent += summary.SilentSeconds;
            offTask += summary.OffTaskSeconds;
            away += summary.AwaySeconds;
            effective += summary.EffectiveSeconds;
            low += summary.EffectiveLowSeconds;
            high += summary.EffectiveHighSeconds;
        }

        return new DailySummary(from, active, silent, offTask, away, effective, low, high, 0, 0, 0,
            Array.Empty<AppUsage>());
    }

    private DailySummary? Summarize(DateOnly date)
    {
        var (start, end) = DailyAggregator.DayRangeUtc(date, TimeZoneInfo.Local);
        if (_store.CountSamplesInRange(start, end) == 0)
        {
            return null;
        }

        var segments = new SegmentBuilder(_rules.Current).Build(_store.LoadSamples(start, end));
        return DailyAggregator.Aggregate(segments, date, TimeZoneInfo.Local);
    }

    private string PeriodName() => StatsPeriod switch
    {
        "month" => "近 6 个月",
        "year" => "近 5 年",
        _ => "近 7 天"
    };

    private static DateOnly min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
}
