using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed partial class CalendarDay : ObservableObject
{
    public CalendarDay(DateOnly date, bool inMonth, bool isToday, bool isWeekend)
    {
        Date = date;
        InMonth = inMonth;
        IsToday = isToday;
        IsWeekend = isWeekend;
        Day = date.Day;
    }

    public DateOnly Date { get; }

    public int Day { get; }

    public bool InMonth { get; }

    public bool IsToday { get; }

    public bool IsWeekend { get; }

    [ObservableProperty]
    private string? holiday;

    [ObservableProperty]
    private bool hasPlan;

    [ObservableProperty]
    private bool isSelected;

    public bool IsHoliday => !string.IsNullOrEmpty(Holiday);

    public string Marker => HasPlan ? "•" : string.Empty;

    partial void OnHolidayChanged(string? value) => OnPropertyChanged(nameof(IsHoliday));

    partial void OnHasPlanChanged(bool value) => OnPropertyChanged(nameof(Marker));
}

/// <summary>时间表格里的一行：7:00–22:00，一格一条计划。</summary>
public sealed partial class HourSlot : ObservableObject
{
    private readonly Action<HourSlot> _onTextChanged;
    private readonly bool _ready;
    private string _text;

    public HourSlot(int hour, string text, long? planId, Action<HourSlot> onTextChanged)
    {
        Hour = hour;
        _text = text;
        PlanId = planId;
        _onTextChanged = onTextChanged;
        _ready = true;
    }

    public int Hour { get; }

    public long? PlanId { get; set; }

    public string TimeLabel => $"{Hour:00}:00";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
            if (_ready)
            {
                _onTextChanged(this);
            }
        }
    }
}

public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly Dictionary<string, string> _holidays;
    private bool _syncing;

    [ObservableProperty]
    private string monthTitle = string.Empty;

    [ObservableProperty]
    private string selectedDateText = string.Empty;

    [ObservableProperty]
    private string holidayText = string.Empty;

    [ObservableProperty]
    private string planCountText = string.Empty;

    [ObservableProperty]
    private string holidaySourceText = string.Empty;

    private DateOnly _month = new(DateOnly.FromDateTime(DateTime.Now).Year, DateOnly.FromDateTime(DateTime.Now).Month, 1);
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Now);

    public ObservableCollection<CalendarDay> Days { get; } = new();

    public ObservableCollection<HourSlot> Hours { get; } = new();

    public ObservableCollection<string> Weekdays { get; } = new();

    public CalendarViewModel(FocusLedgerStore store)
    {
        _store = store;

        // 首次运行：播种香港 2026 法定假期
        if (store.CountHolidays(Holidays.DefaultRegion) == 0)
        {
            store.ReplaceHolidays(Holidays.DefaultRegion, Holidays.HongKong2026);
        }

        _holidays = new Dictionary<string, string>(store.LoadHolidays(Holidays.DefaultRegion), StringComparer.Ordinal);

        Reload();
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        _month = _month.AddMonths(-1);
        Reload();
    }

    [RelayCommand]
    private void NextMonth()
    {
        _month = _month.AddMonths(1);
        Reload();
    }

    [RelayCommand]
    private void GoToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _month = new DateOnly(today.Year, today.Month, 1);
        _selected = today;
        Reload();
    }

    [RelayCommand]
    private void SelectDay(CalendarDay? day)
    {
        if (day is null)
        {
            return;
        }

        if (!day.InMonth)
        {
            _month = new DateOnly(day.Date.Year, day.Date.Month, 1);
        }

        _selected = day.Date;
        Reload();
    }

    public void Reload()
    {
        var localization = Localization.Localization.Instance;
        var culture = localization.Culture;
        bool english = localization.IsEnglish;

        MonthTitle = _month.ToString(english ? "MMMM yyyy" : "yyyy 年 M 月", culture);
        SelectedDateText = _selected.ToString(english ? "ddd, MMM d" : "M 月 d 日 dddd", culture);

        RebuildWeekdays(english);

        var today = DateOnly.FromDateTime(DateTime.Now);
        int offset = ((int)_month.DayOfWeek + 6) % 7;   // 周一为一周之始
        var start = _month.AddDays(-offset);

        var plans = _store.LoadPlansInRange(start, start.AddDays(41))
            .GroupBy(p => p.date_local ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Days.Clear();
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var day = new CalendarDay(
                date,
                date.Month == _month.Month,
                date == today,
                date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                IsSelected = date == _selected
            };

            string key = date.ToString("yyyy-MM-dd");
            if (_holidays.TryGetValue(key, out string? name))
            {
                day.Holiday = name;
            }

            day.HasPlan = plans.ContainsKey(key);
            Days.Add(day);
        }

        HolidayText = _holidays.TryGetValue(_selected.ToString("yyyy-MM-dd"), out string? holiday)
            ? Loc.T("Plan_Holiday", holiday)
            : string.Empty;

        HolidaySourceText = _holidays.Count == 0
            ? Loc.T("Plan_NoHoliday")
            : Loc.T("Plan_HolidaySource", _holidays.Count);

        LoadHours();
    }

    private void RebuildWeekdays(bool english)
    {
        Weekdays.Clear();
        string[] zh = { "一", "二", "三", "四", "五", "六", "日" };
        string[] en = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        foreach (var name in english ? en : zh)
        {
            Weekdays.Add(name);
        }
    }

    private void LoadHours()
    {
        _syncing = true;
        Hours.Clear();

        var plans = _store.LoadPlans(_selected)
            .GroupBy(p => p.hour)
            .ToDictionary(g => g.Key, g => g.First());

        for (int hour = 7; hour <= 22; hour++)
        {
            plans.TryGetValue(hour, out var plan);
            Hours.Add(new HourSlot(hour, plan?.title ?? string.Empty, plan?.id, SaveSlot));
        }

        _syncing = false;
        PlanCountText = Loc.T("Plan_Count", plans.Count);
    }

    private void SaveSlot(HourSlot slot)
    {
        if (_syncing)
        {
            return;
        }

        string text = slot.Text.Trim();

        if (text.Length == 0)
        {
            if (slot.PlanId is { } existingId)
            {
                _store.DeletePlan(existingId);
                slot.PlanId = null;
            }
        }
        else if (slot.PlanId is { } id)
        {
            _store.UpdatePlanTitle(id, text);
        }
        else
        {
            slot.PlanId = _store.AddPlan(_selected, slot.Hour, text);
        }

        PlanCountText = Loc.T("Plan_Count", _store.LoadPlans(_selected).Count);
    }
}
