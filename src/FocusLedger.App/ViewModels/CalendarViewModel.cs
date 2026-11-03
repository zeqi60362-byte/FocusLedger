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

/// <summary>自定义周期里的一枚星期开关。</summary>
public sealed partial class RepeatDay : ObservableObject
{
    public RepeatDay(string label, DayOfWeek day)
    {
        Label = label;
        Day = day;
    }

    public string Label { get; }

    public DayOfWeek Day { get; }

    [ObservableProperty]
    private bool isActive;
}

/// <summary>编辑器里的一枚周期预设（不重复 / 每天 / 工作日 / 休息日）。</summary>
public sealed partial class RepeatOption : ObservableObject
{
    public RepeatOption(string label, string mode)
    {
        Label = label;
        Mode = mode;
    }

    public string Label { get; }

    public string Mode { get; }

    [ObservableProperty]
    private bool isActive;
}

/// <summary>「持续几周」的一枚选项。</summary>
public sealed partial class RepeatWeekOption : ObservableObject
{
    public RepeatWeekOption(string label, int weeks)
    {
        Label = label;
        Weeks = weeks;
    }

    public string Label { get; }

    public int Weeks { get; }

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

    /// <summary>顶栏右侧的临时提示（周期任务建了几条、跳过了几天）。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    private DateOnly _month = new(DateOnly.FromDateTime(DateTime.Now).Year, DateOnly.FromDateTime(DateTime.Now).Month, 1);
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Now);

    public ObservableCollection<CalendarDay> Days { get; } = new();

    public ObservableCollection<WeekBlock> WeekBlocks { get; } = new();

    public ObservableCollection<PlanPreset> Presets { get; } = new();

    /// <summary>自定义周期用的七个星期开关。</summary>
    public ObservableCollection<RepeatDay> RepeatDays { get; } = new();

    /// <summary>周期预设：不重复 / 每天 / 工作日 / 休息日。</summary>
    public ObservableCollection<RepeatOption> RepeatOptions { get; } = new();

    /// <summary>持续几周的可选项。</summary>
    public ObservableCollection<RepeatWeekOption> RepeatWeekOptions { get; } = new();

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

    /// <summary>周期规则：none / daily / weekday / weekend / custom（自定义星期）。</summary>
    [ObservableProperty]
    private string editorRepeat = "none";

    /// <summary>这批周期任务持续几周；铺的天数就是它的 7 倍。</summary>
    [ObservableProperty]
    private int repeatWeeks = 8;

    /// <summary>选了重复才显示「持续几周」。</summary>
    public bool HasRepeat => EditorRepeat != "none";

    /// <summary>「重复」只对新建有意义；改重复等于在原日期上再铺一批，容易重复建。</summary>
    public bool IsNewPlan => !IsExistingPlan;

    partial void OnIsExistingPlanChanged(bool value) => OnPropertyChanged(nameof(IsNewPlan));

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
        BuildRepeatDays();
        Reload();
    }

    private void BuildRepeatDays()
    {
        RepeatDays.Clear();
        RepeatDays.Add(new RepeatDay("一", DayOfWeek.Monday));
        RepeatDays.Add(new RepeatDay("二", DayOfWeek.Tuesday));
        RepeatDays.Add(new RepeatDay("三", DayOfWeek.Wednesday));
        RepeatDays.Add(new RepeatDay("四", DayOfWeek.Thursday));
        RepeatDays.Add(new RepeatDay("五", DayOfWeek.Friday));
        RepeatDays.Add(new RepeatDay("六", DayOfWeek.Saturday));
        RepeatDays.Add(new RepeatDay("日", DayOfWeek.Sunday));

        RepeatOptions.Clear();
        RepeatOptions.Add(new RepeatOption(Loc.T("Repeat_None"), "none") { IsActive = true });
        RepeatOptions.Add(new RepeatOption(Loc.T("Repeat_Daily"), "daily"));
        RepeatOptions.Add(new RepeatOption(Loc.T("Repeat_Weekday"), "weekday"));
        RepeatOptions.Add(new RepeatOption(Loc.T("Repeat_Weekend"), "weekend"));

        RepeatWeekOptions.Clear();
        foreach (int weeks in new[] { 1, 2, 4, 8, 12 })
        {
            RepeatWeekOptions.Add(new RepeatWeekOption(Loc.T("Repeat_Weeks", weeks), weeks)
            {
                IsActive = weeks == RepeatWeeks
            });
        }
    }

    partial void OnEditorRepeatChanged(string value)
    {
        foreach (var option in RepeatOptions)
        {
            option.IsActive = string.Equals(option.Mode, value, StringComparison.Ordinal);
        }

        // 「持续几周」只在真的要重复时才有意义
        OnPropertyChanged(nameof(HasRepeat));
    }

    partial void OnRepeatWeeksChanged(int value)
    {
        foreach (var option in RepeatWeekOptions)
        {
            option.IsActive = option.Weeks == value;
        }
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

        StatusText = string.Empty;
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
    private void SetRepeat(string? mode)
    {
        EditorRepeat = string.IsNullOrWhiteSpace(mode) ? "none" : mode;

        // 选了预设规律就清掉自定义的那几个星期，避免两套状态同时生效
        if (EditorRepeat != "custom")
        {
            foreach (var day in RepeatDays)
            {
                day.IsActive = false;
            }
        }
    }

    [RelayCommand]
    private void SetRepeatWeeks(RepeatWeekOption? option)
    {
        if (option is not null)
        {
            RepeatWeeks = option.Weeks;
        }
    }

    [RelayCommand]
    private void ToggleRepeatDay(RepeatDay? day)
    {
        if (day is null)
        {
            return;
        }

        day.IsActive = !day.IsActive;
        EditorRepeat = RepeatDays.Any(d => d.IsActive) ? "custom" : "none";
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
        string repeatMessage = string.Empty;

        if (_editingPlanId is { } id)
        {
            _store.UpdatePlanDetails(id, title, location, url, EditorRemind);
            _store.UpdatePlanRange(id, _pendingStart, _pendingEnd);
        }
        else
        {
            // 周期任务：从这一天起往后铺到 RepeatHorizonDays，
            // 碰上已被占用的时段就跳过（不覆盖别人，也不整批失败）。
            string token = RepeatToken();
            int created = 0;
            int skipped = 0;

            foreach (var date in ExpandRepeatDates(_pendingDate))
            {
                if (FindConflict(date, _pendingStart, _pendingEnd, null) is not null)
                {
                    skipped++;
                    continue;
                }

                _store.AddPlanFull(date, _pendingStart, _pendingEnd, title, location, url, EditorRemind, token);
                created++;
            }

            if (EditorRepeat != "none")
            {
                repeatMessage = skipped > 0
                    ? Loc.T("Plan_RepeatDoneSkipped", created, skipped)
                    : Loc.T("Plan_RepeatDone", created);
            }
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

        // Reload 会清空提示，所以放在它后面写
        StatusText = repeatMessage;
    }

    /// <summary>按当前周期规则展开这一批日期；「不重复」时只返回起始那一天。</summary>
    private IEnumerable<DateOnly> ExpandRepeatDates(DateOnly start)
    {
        if (EditorRepeat == "none")
        {
            yield return start;
            yield break;
        }

        // 铺的天数 = 周数 × 7，从起始日算起（含当天）
        int horizon = Math.Max(1, RepeatWeeks) * 7;
        for (int i = 0; i < horizon; i++)
        {
            var date = start.AddDays(i);
            if (MatchesRepeat(date))
            {
                yield return date;
            }
        }
    }

    private bool MatchesRepeat(DateOnly date) => EditorRepeat switch
    {
        "daily" => true,
        "weekday" => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        "weekend" => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
        "custom" => RepeatDays.Any(d => d.IsActive && d.Day == date.DayOfWeek),
        _ => false
    };

    /// <summary>落到库里的规则标记：daily / weekday / weekend / weekly:1,3,5（ISO，周一=1）。</summary>
    private string RepeatToken() => EditorRepeat switch
    {
        "daily" => "daily",
        "weekday" => "weekday",
        "weekend" => "weekend",
        "custom" => "weekly:" + string.Join(",", RepeatDays
            .Where(d => d.IsActive)
            .Select(d => ((int)d.Day + 6) % 7 + 1)
            .OrderBy(d => d)),
        _ => string.Empty
    };

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
