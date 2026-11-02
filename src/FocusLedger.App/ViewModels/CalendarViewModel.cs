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

/// <summary>时间表格里的一行。一条计划可以横跨多个小时，跨进来的那几行只做只读展示。</summary>
public sealed partial class HourSlot : ObservableObject
{
    private readonly Action<HourSlot> _onTitleChanged;
    private readonly Action<HourSlot> _onRangeChanged;
    private readonly bool _ready;
    private string _text;

    public HourSlot(
        int hour,
        string text,
        long? planId,
        int startMinute,
        int endMinute,
        bool isContinuation,
        Action<HourSlot> onTitleChanged,
        Action<HourSlot> onRangeChanged)
    {
        Hour = hour;
        _text = text;
        PlanId = planId;
        StartMinute = startMinute;
        EndMinute = endMinute;
        IsContinuation = isContinuation;
        _onTitleChanged = onTitleChanged;
        _onRangeChanged = onRangeChanged;
        _ready = true;
    }

    public int Hour { get; }

    private long? _planId;

    public long? PlanId
    {
        get => _planId;
        set
        {
            if (_planId == value)
            {
                return;
            }

            _planId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlan));
        }
    }

    /// <summary>已经有计划的行才展开时间选择，空行只给一个输入框，避免整页都是下拉框。</summary>
    public bool HasPlan => _planId is not null;

    public bool IsContinuation { get; }

    public bool IsEditable => !IsContinuation;

    public int StartMinute { get; private set; }

    public int EndMinute { get; private set; }

    public string TimeLabel => $"{Hour:00}:00";

    /// <summary>15 分钟一档，6:00–24:00。实例属性，方便在 DataTemplate 里直接绑定。</summary>
    public IReadOnlyList<string> TimeOptions => Options;

    private static readonly IReadOnlyList<string> Options = BuildOptions();

    public string StartText
    {
        get => Format(StartMinute);
        set
        {
            if (!TryParse(value, out int minutes) || minutes == StartMinute)
            {
                return;
            }

            StartMinute = minutes;
            if (EndMinute <= StartMinute)
            {
                EndMinute = Math.Min(24 * 60, StartMinute + 60);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(EndText));
            OnPropertyChanged(nameof(DurationText));
            if (_ready)
            {
                _onRangeChanged(this);
            }
        }
    }

    public string EndText
    {
        get => Format(EndMinute);
        set
        {
            if (!TryParse(value, out int minutes) || minutes == EndMinute)
            {
                return;
            }

            EndMinute = Math.Max(StartMinute + 15, minutes);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
            if (_ready)
            {
                _onRangeChanged(this);
            }
        }
    }

    public string DurationText
    {
        get
        {
            int minutes = Math.Max(0, EndMinute - StartMinute);
            return minutes >= 60
                ? $"{minutes / 60}h{(minutes % 60 == 0 ? string.Empty : (minutes % 60) + "m")}"
                : $"{minutes}m";
        }
    }

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
                _onTitleChanged(this);
            }
        }
    }

    public void ApplyRange(int startMinute, int endMinute)
    {
        StartMinute = startMinute;
        EndMinute = endMinute;
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(DurationText));
    }

    private static string Format(int minutes) =>
        $"{Math.Clamp(minutes, 0, 24 * 60) / 60:00}:{Math.Clamp(minutes, 0, 24 * 60) % 60:00}";

    private static bool TryParse(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int hour) ||
            !int.TryParse(parts[1], out int minute))
        {
            return false;
        }

        if (hour is < 0 or > 24 || minute is < 0 or > 59)
        {
            return false;
        }

        minutes = hour * 60 + minute;
        return true;
    }

    private static IReadOnlyList<string> BuildOptions()
    {
        var list = new List<string>();
        for (int m = 6 * 60; m <= 24 * 60; m += 15)
        {
            list.Add(Format(m));
        }

        return list;
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
            .OrderBy(p => p.start_min)
            .ToList();

        for (int hour = 7; hour <= 22; hour++)
        {
            int hourStart = hour * 60;
            int hourEnd = hourStart + 60;

            // 正好在这个小时开始的计划
            var owner = plans.FirstOrDefault(p => p.start_min >= hourStart && p.start_min < hourEnd);

            if (owner is not null)
            {
                Hours.Add(new HourSlot(hour, owner.title ?? string.Empty, owner.id,
                    owner.start_min, owner.end_min, isContinuation: false, SaveSlot, SaveRange));
                continue;
            }

            // 被上一个小时的计划覆盖，只做只读展示
            var covering = plans.FirstOrDefault(p => p.start_min < hourStart && p.end_min > hourStart);
            if (covering is not null)
            {
                Hours.Add(new HourSlot(hour, covering.title ?? string.Empty, covering.id,
                    covering.start_min, covering.end_min, isContinuation: true, SaveSlot, SaveRange));
                continue;
            }

            Hours.Add(new HourSlot(hour, string.Empty, null,
                hourStart, hourEnd, isContinuation: false, SaveSlot, SaveRange));
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
            slot.PlanId = _store.AddPlanRange(_selected, slot.StartMinute, slot.EndMinute, text);
        }

        PlanCountText = Loc.T("Plan_Count", _store.LoadPlans(_selected).Count);
    }

    private void SaveRange(HourSlot slot)
    {
        if (_syncing)
        {
            return;
        }

        if (slot.PlanId is { } id)
        {
            _store.UpdatePlanRange(id, slot.StartMinute, slot.EndMinute);
            PlanCountText = Loc.T("Plan_Count", _store.LoadPlans(_selected).Count);
        }
    }
}
