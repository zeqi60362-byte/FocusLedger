using FocusLedger.App.Controls;
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

/// <summary>编辑器里的一枚预设标签。点一下就把标题填成 <see cref="Title"/>。</summary>
public sealed partial class PlanPreset : ObservableObject
{
    public PlanPreset(string label, string title)
    {
        Label = label;
        Title = title;
    }

    public string Label { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool isActive;
}

public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly Dictionary<string, string> _holidays;
    private readonly Dictionary<long, FocusLedgerStore.PlanRow> _planDetails = new();

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

    public ObservableCollection<WeekBlock> WeekBlocks { get; } = new();

    public ObservableCollection<PlanPreset> Presets { get; } = new();

    private DateOnly _weekStart;
    private DateOnly _pendingDate;
    private long? _editingPlanId;

    [ObservableProperty]
    private bool isEditorOpen;

    [ObservableProperty]
    private string editorHeading = string.Empty;

    [ObservableProperty]
    private string editorTitle = string.Empty;

    [ObservableProperty]
    private string editorRange = string.Empty;

    [ObservableProperty]
    private string editorLocation = string.Empty;

    [ObservableProperty]
    private string editorUrl = string.Empty;

    /// <summary>保存被拒绝时显示在弹窗里的提示（时间冲突、标题为空）。空串表示没有错误。</summary>
    [ObservableProperty]
    private string editorError = string.Empty;

    [ObservableProperty]
    private bool editorRemind = true;

    /// <summary>正在编辑已有计划时才显示删除按钮。</summary>
    [ObservableProperty]
    private bool isExistingPlan;

    private int _pendingStart;
    private int _pendingEnd;

    public ObservableCollection<string> Weekdays { get; } = new();

    /// <summary>本周里"今天"是第几列；不在本周时为 -1。</summary>
    [ObservableProperty]
    private int todayIndex = -1;

    public CalendarViewModel(FocusLedgerStore store)
    {
        _store = store;

        // 首次运行：播种香港 2026 法定假期
        if (store.CountHolidays(Holidays.DefaultRegion) == 0)
        {
            store.ReplaceHolidays(Holidays.DefaultRegion, Holidays.HongKong2026);
        }

        _holidays = new Dictionary<string, string>(store.LoadHolidays(Holidays.DefaultRegion), StringComparer.Ordinal);

        BuildPresets();
        Reload();
    }

    private void BuildPresets()
    {
        Presets.Clear();
        Presets.Add(new PlanPreset(Loc.T("Preset_Write"), Loc.T("Preset_Write")));
        Presets.Add(new PlanPreset(Loc.T("Preset_Read"), Loc.T("Preset_Read")));
        Presets.Add(new PlanPreset(Loc.T("Preset_Meeting"), Loc.T("Preset_Meeting")));
        Presets.Add(new PlanPreset(Loc.T("Preset_Exercise"), Loc.T("Preset_Exercise")));
        Presets.Add(new PlanPreset(Loc.T("Preset_Break"), Loc.T("Preset_Break")));
        Presets.Add(new PlanPreset(Loc.T("Preset_Chores"), Loc.T("Preset_Chores")));
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

        LoadWeek();
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

    private void LoadWeek()
    {
        _planDetails.Clear();

        int offset = ((int)_selected.DayOfWeek + 6) % 7;
        _weekStart = _selected.AddDays(-offset);

        var today = DateOnly.FromDateTime(DateTime.Now);
        TodayIndex = today >= _weekStart && today <= _weekStart.AddDays(6)
            ? today.DayNumber - _weekStart.DayNumber
            : -1;

        WeekBlocks.Clear();
        foreach (var plan in _store.LoadPlansInRange(_weekStart, _weekStart.AddDays(6)))
        {
            // 点色块要能回填地点/网址/提醒，所以顺手留一份完整行
            _planDetails[plan.id] = plan;

            if (!DateOnly.TryParse(plan.date_local, out var day))
            {
                continue;
            }

            int index = day.DayNumber - _weekStart.DayNumber;
            if (index is < 0 or > 6)
            {
                continue;
            }

            WeekBlocks.Add(new WeekBlock(plan.id, index, plan.start_min, plan.end_min,
                plan.title ?? string.Empty, plan.location ?? string.Empty, plan.url ?? string.Empty));
        }

        PlanCountText = Loc.T("Plan_Count", _store.LoadPlans(_selected).Count);
    }

    /// <summary>周视图里拖出一段时间 → 打开新建计划窗。</summary>
    public void OnRangeSelected(int dayIndex, int start, int end)
    {
        _editingPlanId = null;
        _pendingDate = _weekStart.AddDays(dayIndex);
        _pendingStart = start;
        _pendingEnd = end;

        OpenEditor(Loc.T("Plan_EditorNew"), WeekdayName(dayIndex, start, end));
        IsExistingPlan = false;
        EditorTitle = string.Empty;
        EditorLocation = string.Empty;
        EditorUrl = string.Empty;
        EditorRemind = true;
    }

    /// <summary>点中已有计划 → 用同一扇窗编辑它。</summary>
    public void OnBlockClicked(WeekBlock block)
    {
        if (!_planDetails.TryGetValue(block.Id, out var row))
        {
            return;
        }

        _editingPlanId = row.id;
        _pendingStart = row.start_min;
        _pendingEnd = row.end_min;
        _pendingDate = DateOnly.TryParse(row.date_local, out var day) ? day : _weekStart.AddDays(block.DayIndex);

        OpenEditor(Loc.T("Plan_EditorEdit"), WeekdayName(block.DayIndex, row.start_min, row.end_min));
        IsExistingPlan = true;
        EditorTitle = row.title ?? string.Empty;
        EditorLocation = row.location ?? string.Empty;
        EditorUrl = row.url ?? string.Empty;
        EditorRemind = row.Remind;
    }

    private void OpenEditor(string heading, string range)
    {
        EditorHeading = heading;
        EditorRange = range;
        EditorError = string.Empty;
        UpdatePresetHighlight();
        IsEditorOpen = true;
    }

    private string WeekdayName(int dayIndex, int start, int end)
    {
        string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        return $"{names[Math.Clamp(dayIndex, 0, 6)]} {start / 60:00}:{start % 60:00} – {end / 60:00}:{end % 60:00}";
    }

    partial void OnEditorTitleChanged(string value) => UpdatePresetHighlight();

    private void UpdatePresetHighlight()
    {
        string title = EditorTitle.Trim();
        foreach (var preset in Presets)
        {
            preset.IsActive = title.Length > 0 && string.Equals(title, preset.Title, StringComparison.Ordinal);
        }
    }

    [RelayCommand]
    private void ApplyPreset(PlanPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        // 再点一次同一枚标签就取消选中，避免只能用一次
        EditorTitle = EditorTitle.Trim() == preset.Title ? string.Empty : preset.Title;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private void SavePlan()
    {
        string title = EditorTitle.Trim();
        if (title.Length == 0)
        {
            EditorError = Loc.T("Plan_NeedTitle");
            return;
        }

        // 同一份日程上不允许两段时间重叠：拖的时候看着是一整条，保存后却是两条，
        // 后面读回来自己也分不清哪条算数，所以直接拒绝，让人重拖。
        if (FindConflict(_pendingDate, _pendingStart, _pendingEnd, _editingPlanId) is { } clash)
        {
            EditorError = Loc.T("Plan_Conflict",
                clash.title ?? string.Empty,
                $"{clash.start_min / 60:00}:{clash.start_min % 60:00} – {clash.end_min / 60:00}:{clash.end_min % 60:00}");
            return;
        }

        string location = EditorLocation.Trim();
        string url = EditorUrl.Trim();

        if (_editingPlanId is { } id)
        {
            _store.UpdatePlanDetails(id, title, location, url, EditorRemind);
            _store.UpdatePlanRange(id, _pendingStart, _pendingEnd);
        }
        else
        {
            _store.AddPlanFull(_pendingDate, _pendingStart, _pendingEnd, title, location, url, EditorRemind);
        }

        EditorError = string.Empty;
        IsEditorOpen = false;
        _editingPlanId = null;

        // 在别的日子上新建/编辑完，把选中日也挪过去：
        // 否则顶栏还写着「11 月 3 日 星期二」、计数也不变，看着像没生效。
        if (_pendingDate != _selected)
        {
            _selected = _pendingDate;
            _month = new DateOnly(_selected.Year, _selected.Month, 1);
        }

        Reload();
    }

    /// <summary>同一天里与 [start, end) 相交的另一条计划；编辑自己时要把自己排除掉。</summary>
    private FocusLedgerStore.PlanRow? FindConflict(DateOnly date, int start, int end, long? ignoreId)
        => _store.LoadPlans(date)
            .FirstOrDefault(p => p.id != ignoreId && start < p.end_min && end > p.start_min);

    [RelayCommand]
    private void DeletePlan()
    {
        if (_editingPlanId is not { } id)
        {
            return;
        }

        _store.DeletePlan(id);
        IsEditorOpen = false;
        _editingPlanId = null;
        Reload();
    }
}
