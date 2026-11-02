using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed record DistractionRow(string App, string Duration, string Share);

public sealed record LegendRow(string Label, string Value, Brush Fill);

public sealed record TransitionRow(string From, string To, string Count, IReadOnlyList<BarSlice> Bar);

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

    [ObservableProperty]
    private IReadOnlyList<StateChip> chips = Array.Empty<StateChip>();

    [ObservableProperty]
    private IReadOnlyList<BarSlice> donutSlices = Array.Empty<BarSlice>();

    [ObservableProperty]
    private string donutValue = string.Empty;

    [ObservableProperty]
    private string donutLabel = string.Empty;

    public ObservableCollection<LegendRow> Legend { get; } = new();

    public ObservableCollection<LegendRow> AppLegend { get; } = new();

    [ObservableProperty]
    private IReadOnlyList<BarSlice> appSlices = Array.Empty<BarSlice>();

    [ObservableProperty]
    private string appTotal = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<FlowSlice> flows = Array.Empty<FlowSlice>();

    /// <summary>软件用时用的另一套配色：同样低饱和、中深，但和四状态色区分开。</summary>
    private static readonly string[] AppPalette =
    {
        "#5B6F8F", "#8A6FA8", "#C08A3E", "#4E8C8A", "#9A6B6B", "#6E7A5A"
    };

    public ObservableCollection<TransitionRow> Transitions { get; } = new();

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
        DateText = _date.ToString(
            Localization.Localization.Instance.IsEnglish ? "MMM d, yyyy" : "yyyy 年 M 月 d 日",
            Localization.Localization.Instance.Culture);

        var summary = Summarize(_date);
        Distractions.Clear();
        Legend.Clear();
        Transitions.Clear();
        AppLegend.Clear();
        AppSlices = Array.Empty<BarSlice>();
        AppTotal = string.Empty;
        Flows = Array.Empty<FlowSlice>();

        if (summary is null)
        {
            HasData = false;
            Effective = "—";
            Band = string.Empty;
            PrimaryRow = string.Empty;
            SecondaryRow = string.Empty;
            StatsRow = string.Empty;
            EmptyHint = Loc.T("Report_Empty");
            DonutSlices = Array.Empty<BarSlice>();
            DonutValue = "—";
            DonutLabel = string.Empty;
        }
        else
        {
            HasData = true;
            EmptyHint = string.Empty;

            Effective = Fmt.Duration(summary.EffectiveSeconds);
            Band = Loc.T("Today_Band",
                Fmt.Duration(summary.EffectiveLowSeconds),
                Fmt.Duration(summary.EffectiveHighSeconds));
            PrimaryRow = Loc.T("Today_Breakdown",
                Fmt.Duration(summary.ActiveSeconds),
                Fmt.Duration(summary.SilentSeconds));
            SecondaryRow = Loc.T("Today_Secondary",
                Fmt.Duration(summary.OffTaskSeconds),
                Fmt.Duration(summary.AwaySeconds));
            Chips = new[]
            {
                new StateChip(Loc.T("State_ActiveFull"), Fmt.Duration(summary.ActiveSeconds), Brush("StateActive")),
                new StateChip(Loc.T("State_OffTaskFull"), Fmt.Duration(summary.OffTaskSeconds), Brush("StateOffTask")),
                new StateChip(Loc.T("State_AwayFull"), Fmt.Duration(summary.AwaySeconds), Brush("StateIdle")),
                new StateChip(Loc.T("State_SilentFull"), Fmt.Duration(summary.SilentSeconds), Brush("StateSilent"))
            };
            StatsRow = Loc.T("Report_StatLine",
                summary.FocusBlocks,
                (summary.LongestFocusBlockSeconds / 60).ToString("F0"),
                summary.AppSwitches);

            DonutSlices = new[]
            {
                new BarSlice(summary.ActiveSeconds, Brush("StateActive")),
                new BarSlice(summary.SilentSeconds, Brush("StateSilent")),
                new BarSlice(summary.OffTaskSeconds, Brush("StateOffTask")),
                new BarSlice(summary.AwaySeconds, Brush("StateIdle"))
            };
            DonutValue = Fmt.Duration(summary.AccountedSeconds);
            DonutLabel = Loc.T("Today_Effective");

            Legend.Add(new LegendRow(Loc.T("State_ActiveFull"), Fmt.Duration(summary.ActiveSeconds), Brush("StateActive")));
            Legend.Add(new LegendRow(Loc.T("State_OffTaskFull"), Fmt.Duration(summary.OffTaskSeconds), Brush("StateOffTask")));
            Legend.Add(new LegendRow(Loc.T("State_AwayFull"), Fmt.Duration(summary.AwaySeconds), Brush("StateIdle")));
            Legend.Add(new LegendRow(Loc.T("State_SilentFull"), Fmt.Duration(summary.SilentSeconds), Brush("StateSilent")));

            int maxTransition = summary.TopTransitions.Count > 0
                ? summary.TopTransitions.Max(t => t.Count)
                : 1;
            foreach (var transition in summary.TopTransitions)
            {
                Transitions.Add(new TransitionRow(
                    Fmt.AppName(transition.From),
                    Fmt.AppName(transition.To),
                    Loc.T("Report_Times", transition.Count),
                    new[]
                    {
                        new BarSlice(transition.Count, Brush("StateSilent")),
                        new BarSlice(Math.Max(0.001, maxTransition - transition.Count), Brushes.Transparent)
                    }));
            }

            var appSlices = new List<BarSlice>();
            int colorIndex = 0;
            double appTotalSeconds = summary.AppTotals.Sum(a => a.Seconds);

            foreach (var app in summary.AppTotals)
            {
                string label = app.App == "__other__" ? Loc.T("Report_Other") : Fmt.AppName(app.App);
                var brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(
                        AppPalette[Math.Min(colorIndex, AppPalette.Length - 1)]));

                appSlices.Add(new BarSlice(app.Seconds, brush));
                AppLegend.Add(new LegendRow(label, Fmt.Duration(app.Seconds), brush));
                colorIndex++;
            }

            AppSlices = appSlices;
            AppTotal = Fmt.Duration(appTotalSeconds);

            var sourceColors = new Dictionary<string, Brush>();
            var flowList = new List<FlowSlice>();
            int flowColor = 0;

            foreach (var transition in summary.TopTransitions.Take(10))
            {
                string from = Fmt.AppName(transition.From);
                string to = Fmt.AppName(transition.To);

                if (!sourceColors.TryGetValue(from, out Brush? brush))
                {
                    brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(
                            AppPalette[Math.Min(flowColor, AppPalette.Length - 1)]));
                    sourceColors[from] = brush;
                    flowColor++;
                }

                flowList.Add(new FlowSlice(from, to, transition.Count, brush));
            }

            Flows = flowList;

            double totalOffTask = summary.OffTaskSeconds;
            foreach (var usage in summary.TopDistractions)
            {
                string share = totalOffTask > 0 ? $"{usage.Seconds / totalOffTask:P0}" : "—";
                Distractions.Add(new DistractionRow(
                    Fmt.AppName(usage.App),
                    Fmt.MinutesShort(usage.Seconds),
                    share));
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
                // 倒序：最近的月份在最上面
                for (int i = 0; i < 6; i++)
                {
                    var anchor = today.AddMonths(-i);
                    var from = new DateOnly(anchor.Year, anchor.Month, 1);
                    buckets.Add((MonthLabel(anchor), from, from.AddMonths(1).AddDays(-1)));
                }

                break;

            case "year":
                for (int i = 0; i < 5; i++)
                {
                    var anchor = today.AddYears(-i);
                    var from = new DateOnly(anchor.Year, 1, 1);
                    buckets.Add((YearLabel(anchor), from, from.AddYears(1).AddDays(-1)));
                }

                break;

            default:
                for (int i = 0; i < 7; i++)
                {
                    var day = today.AddDays(-i);
                    buckets.Add((DayLabel(day), day, day));
                }

                break;
        }

        var values = buckets
            .Select(b => (b.Label, Summary: SumRange(b.From, Min(b.To, today))))
            .ToList();

        double max = values.Count == 0 ? 0 : values.Max(v => v.Summary.EffectiveSeconds);
        if (max <= 0)
        {
            max = 1;
        }

        double total = values.Sum(v => v.Summary.EffectiveSeconds);

        foreach (var (label, summary) in values)
        {
            Stats.Add(new StatRow(
                label,
                summary.EffectiveSeconds > 0 ? Fmt.Duration(summary.EffectiveSeconds) : "—",
                Loc.T("Report_StatDetail",
                    Fmt.Duration(summary.ActiveSeconds),
                    Fmt.Duration(summary.SilentSeconds),
                    Fmt.Duration(summary.OffTaskSeconds)),
                new[]
                {
                    new BarSlice(Math.Max(0.001, summary.EffectiveSeconds), Brush("StateActive")),
                    new BarSlice(Math.Max(0.001, max - summary.EffectiveSeconds), Brushes.Transparent)
                }));
        }

        StatsSummary = Loc.T("Report_StatsTotal", PeriodName(), Fmt.Duration(total));
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
            Array.Empty<AppUsage>(), Array.Empty<AppTransition>(), Array.Empty<AppUsage>());
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

    private static string DayLabel(DateOnly day) =>
        day.ToString(Localization.Localization.Instance.IsEnglish ? "MMM d" : "M/d",
            Localization.Localization.Instance.Culture);

    private static string MonthLabel(DateOnly day) =>
        day.ToString(Localization.Localization.Instance.IsEnglish ? "MMM" : "M 月",
            Localization.Localization.Instance.Culture);

    private static string YearLabel(DateOnly day) =>
        day.ToString(Localization.Localization.Instance.IsEnglish ? "yyyy" : "yyyy 年",
            Localization.Localization.Instance.Culture);

    private string PeriodName() => StatsPeriod switch
    {
        "month" => Loc.T("Report_PeriodMonth"),
        "year" => Loc.T("Report_PeriodYear"),
        _ => Loc.T("Report_PeriodWeek")
    };

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
}
