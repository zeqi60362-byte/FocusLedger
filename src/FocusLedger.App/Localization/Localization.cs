using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace FocusLedger.App.Localization;

public enum AppLanguage
{
    Chinese,
    English
}

/// <summary>
/// 极简本地化：一个键 → 中英两份文案。
/// XAML 用 {loc:T Key}，运行时切换语言会刷新所有绑定。
/// </summary>
public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Instance { get; } = new();

    private static readonly Dictionary<string, (string Zh, string En)> Strings = new(StringComparer.Ordinal)
    {
        // 全局
        ["App_Name"] = ("FocusLedger", "FocusLedger"),
        ["App_Tagline"] = ("专注账本 · 本地优先", "Focus ledger · local-first"),
        ["App_LocalOnly"] = ("数据仅存本机", "Data stays on this device"),
        ["Status_Recording"] = ("● 记录中", "● Recording"),
        ["Status_Paused"] = ("○ 已暂停", "○ Paused"),

        // 导航
        ["Nav_Today"] = ("今日专注", "Today"),
        ["Nav_TodayHint"] = ("", ""),
        ["Nav_Report"] = ("专注统计", "Statistics"),
        ["Nav_ReportHint"] = ("", ""),
        ["Nav_Rules"] = ("规则安排", "Rules"),
        ["Nav_RulesHint"] = ("", ""),
        ["Nav_Plan"] = ("预期计划", "Planner"),
        ["Nav_PlanHint"] = ("", ""),
        ["Nav_Settings"] = ("系统设置", "Settings"),
        ["Nav_SettingsHint"] = ("", ""),

        // 状态名
        ["State_Active"] = ("深度", "Deep"),
        ["State_Silent"] = ("静默", "Silent"),
        ["State_OffTask"] = ("分心", "Distracted"),
        ["State_Away"] = ("离席", "Away"),
        ["State_ActiveFull"] = ("深度专注", "Deep focus"),
        ["State_SilentFull"] = ("静默专注", "Silent focus"),
        ["State_OffTaskFull"] = ("分心", "Distracted"),
        ["State_AwayFull"] = ("离席", "Away"),

        // 今日
        ["Today_Title"] = ("今天", "Today"),
        ["Today_Pause"] = ("暂停记录", "Pause"),
        ["Today_Resume"] = ("开始记录", "Resume"),
        ["Today_Effective"] = ("有效专注", "Effective focus"),
        ["Today_DistractedAway"] = ("离席/分心", "Away / distracted"),
        ["Today_TotalWork"] = ("今日总计", "Total today"),
        ["Today_EffectiveFormula"] = ("＝ 深度×1.0 ＋ 静默×0.85（弱证据段按 0.75 折算）",
                                      "= deep 1.0 + silent 0.85 (weak evidence at 0.75)"),
        ["Today_Band"] = ("区间 {0} – {1}", "range {0} – {1}"),
        ["Today_Breakdown"] = ("深度 {0}     静默 {1}", "Deep {0}     Silent {1}"),
        ["Today_Secondary"] = ("分心 {0}     离席 {1}", "Distracted {0}     Away {1}"),
        ["Today_Coverage"] = ("样本 {0} 条  ·  覆盖 {1}  ·  深度工作块 {2} 次{3}",
            "Samples {0}  ·  covered {1}  ·  deep blocks {2}{3}"),
        ["Today_Waiting"] = ("等待采样…", "Waiting for samples…"),
        ["Today_Timeline"] = ("时间轴", "Timeline"),
        ["Today_ZoomHour"] = ("1 小时", "1 hour"),
        ["Today_ZoomSix"] = ("6 小时", "6 hours"),
        ["Today_ZoomDay"] = ("全天", "Full day"),
        ["Today_TimelineHint"] = ("把鼠标放到色带上可查看细节。",
                                  "Hover the band to see details."),
        ["Today_DetailPlaceholder"] = ("点击时间轴上的任意一段，查看它为什么被这样判定。",
            "Click any segment to see how it was classified."),
        ["Today_Detail"] = ("{0} – {1}    {2}    {3}\n应用：{4}\n标题：{5}",
            "{0} – {1}    {2}    {3}\nApp: {4}\nTitle: {5}"),
        ["Today_NoTitle"] = ("(无标题)", "(no title)"),

        // 意图
        ["Intent_Title"] = ("意图声明", "Focus pledge"),
        ["Intent_Start"] = ("开始一段专注", "Start a session"),
        ["Intent_WhatNext"] = ("接下来做什么？", "What are you working on?"),
        ["Intent_Duration"] = ("时长", "Duration"),
        ["Intent_Custom"] = ("自定义", "Custom"),
        ["Intent_Minutes"] = ("{0} 分钟", "{0} min"),
        ["Intent_Begin"] = ("开始", "Start"),
        ["Intent_Cancel"] = ("取消", "Cancel"),
        ["Intent_Running"] = ("进行中", "In progress"),
        ["Intent_Remaining"] = ("还剩 {0}", "{0} left"),
        ["Intent_TimeUp"] = ("时间到", "Time's up"),
        ["Intent_Adherence"] = ("已遵守 {0}", "{0} kept"),
        ["Intent_End"] = ("结束", "End"),
        ["Intent_GiveUp"] = ("放弃", "Give up"),
        ["Intent_DefaultLabel"] = ("写论文", "Writing"),
        ["Intent_NextHint"] = ("下一次开始时，这个数字会跟着你的实际表现走。",
            "The next session will be scored the same way."),
        ["Intent_Result"] = ("{0} · {1} 分钟，遵守 {2} {3}{4}",
            "{0} · {1} min, {2} kept {3}{4}"),
        ["Intent_OnTime"] = ("· 到时", "· on time"),
        ["Intent_Early"] = ("· 提前结束", "· ended early"),
        ["Intent_Untracked"] = ("（其中相当一部分没有采集数据，这个数字不太可信）",
            " (much of this window has no samples, so this number is unreliable)"),
        ["Intent_Abandoned"] = ("{0} · 已放弃", "{0} · abandoned"),

        // 日报
        ["Report_Title"] = ("专注统计", "Statistics"),
        ["Report_Prev"] = ("前一天", "Previous"),
        ["Report_Today"] = ("今天", "Today"),
        ["Report_Next"] = ("后一天", "Next"),
        ["Report_DistractionTitle"] = ("分心点", "Distractions"),
        ["Report_BreakdownTitle"] = ("状态构成", "Composition"),
        ["Report_CompositionTotal"] = ("总记录", "Tracked"),
        ["Report_CompositionNote"] = ("四态相加，就是当天被记录的总时长", "The four states added up = total tracked time"),
        ["Report_AppNote"] = ("同一段总时长，按软件重新切分", "The same tracked time, split by app"),
        ["Report_AppTime"] = ("软件用时", "Time by app"),
        ["Report_AppTotal"] = ("总时长", "Total"),
        ["Report_Other"] = ("其他", "Other"),
        ["Report_FlowTitle"] = ("应用流转", "App flow"),
        ["Report_FlowHint"] = ("你一天里在软件之间跳了多少次，以及最常见的几条路径。",
            "How often you jump between apps — and the most common paths."),
        ["Report_Times"] = ("{0} 次", "{0}×"),
        ["Report_Stats"] = ("统计", "Stats"),
        ["Report_Week"] = ("逐周", "Weekly"),
        ["Report_Month"] = ("逐月", "Monthly"),
        ["Report_Year"] = ("逐年", "Yearly"),
        ["Report_PeriodWeek"] = ("近 7 天", "last 7 days"),
        ["Report_PeriodMonth"] = ("近 6 个月", "last 6 months"),
        ["Report_PeriodYear"] = ("近 5 年", "last 5 years"),
        ["Report_StatsTotal"] = ("{0}合计有效 {1}", "{0}: {1} effective"),
        ["Report_Empty"] = ("这一天还没有数据。", "No data for this day."),
        ["Report_StatLine"] = ("深度工作块 {0} 次（最长 {1} 分钟）    应用切换 {2} 次",
            "Deep blocks {0} (longest {1} min)    App switches {2}"),
        ["Report_StatDetail"] = ("深度 {0}   静默 {1}   分心 {2}",
            "Deep {0}   Silent {1}   Distracted {2}"),

        // 规则
        ["Rules_Title"] = ("规则安排", "Rules"),
        ["Rules_Desc"] = ("决定每个应用算「任务 / 中性 / 分心」。改完立即生效，历史数据也会一起重算。",
            "Decides whether an app counts as work, neutral, or distraction. Changes apply retroactively."),
        ["Rules_LastWindow"] = ("刚才在用的窗口", "Last window"),
        ["Rules_New"] = ("用当前窗口新建规则", "New rule from last window"),
        ["Rules_Match"] = ("匹配", "Match"),
        ["Rules_Source"] = ("来源", "Source"),
        ["Rules_Category"] = ("类别", "Category"),
        ["Rules_Priority"] = ("优先级", "Priority"),
        ["Rules_Enabled"] = ("启用", "On"),
        ["Rules_Delete"] = ("删除", "Delete"),
        ["Rules_Builtin"] = ("内置", "Built-in"),
        ["Rules_Custom"] = ("自定义", "Custom"),
        ["Rules_Process"] = ("进程  {0}", "Process  {0}"),
        ["Rules_Contains"] = ("标题含  {0}", "Title contains  {0}"),
        ["Rules_Regex"] = ("正则  {0}", "Regex  {0}"),
        ["Rules_CycleHint"] = ("点击循环切换：任务 → 中性 → 分心", "Click to cycle: work → neutral → distraction"),
        ["Rules_NoSample"] = ("还没采到当前应用，等几秒再试。", "No sample yet — wait a few seconds."),
        ["Rules_Exists"] = ("已经有一条针对 {0} 的自定义规则了，直接改它的类别即可。",
            "A custom rule for {0} already exists — just change its category."),
        ["Rules_Created"] = ("已为 {0} 新建规则：任务（优先级 5，高于所有内置规则）。",
            "Created a rule for {0}: work (priority 5, above all built-in rules)."),
        ["Rules_BuiltinReadonly"] = ("内置规则不能直接改类别。点「用当前窗口新建规则」，新规则的优先级更高，会盖过内置规则。",
            "Built-in rules can't be edited. Create a new rule instead — it gets higher priority and overrides them."),
        ["Rules_CategoryChanged"] = ("{0} → {1}", "{0} → {1}"),
        ["Rules_Deleted"] = ("已删除规则：{0}", "Deleted rule: {0}"),
        ["Rules_Toggled"] = ("{0}：{1}", "{0}: {1}"),
        ["Rules_On"] = ("已启用", "enabled"),
        ["Rules_Off"] = ("已停用", "disabled"),
        ["Cat_Task"] = ("任务", "Work"),
        ["Cat_Neutral"] = ("中性", "Neutral"),
        ["Cat_Distraction"] = ("分心", "Distraction"),

        // 设置
        ["Settings_Title"] = ("系统设置", "Settings"),
        ["Settings_Desc"] = ("所有设置在本地生效，不会上传任何内容。", "Everything is applied locally. Nothing is uploaded."),
        ["Settings_Startup"] = ("启动", "Startup"),
        ["Settings_AutoStart"] = ("开机时自动启动 FocusLedger", "Start FocusLedger when Windows starts"),
        ["Settings_Retention"] = ("数据保留", "Data retention"),
        ["Settings_RetentionDesc"] = ("原始样本（每 5 秒一条）超过保留天数后会被清理，日聚合与标注不受影响。",
            "Raw samples (one per 5 seconds) are pruned after the retention period; daily totals are kept."),
        ["Settings_Days"] = ("天", "days"),
        ["Settings_Exclude"] = ("排除列表", "Excluded apps"),
        ["Settings_ExcludeDesc"] = ("命中的进程不会被记录，例如密码管理器、私人聊天工具。",
            "Matching processes are never recorded — e.g. password managers, private chats."),
        ["Settings_Add"] = ("添加", "Add"),
        ["Settings_Remove"] = ("移除", "Remove"),
        ["Settings_Data"] = ("数据", "Data"),
        ["Settings_Import"] = ("导入计时器 CSV", "Import timer CSV"),
        ["Settings_Export"] = ("导出今日 CSV", "Export today's CSV"),
        ["Settings_DeleteAll"] = ("删除全部数据", "Delete all data"),
        ["Settings_DeleteConfirm"] = ("将删除全部采集数据（样本、判定段、标注），且无法恢复。\n\n确定继续吗？",
            "This deletes all recorded data permanently.\n\nContinue?"),
        ["Settings_Deleted"] = ("已删除全部数据。", "All data deleted."),
        ["Settings_Exported"] = ("已导出到 {0}", "Exported to {0}"),
        ["Settings_ExportEmpty"] = ("今天还没有数据可以导出。", "Nothing to export yet today."),
        ["Settings_ExportFailed"] = ("导出失败：{0}", "Export failed: {0}"),
        ["Settings_ExcludedUpdated"] = ("排除列表已更新（{0} 项）。", "Excluded apps updated ({0})."),
        ["Settings_AutoStartOn"] = ("已设置为开机自启。", "Will start with Windows."),
        ["Settings_AutoStartOff"] = ("已取消开机自启。", "No longer starts with Windows."),
        ["Settings_Imported"] = ("已导入 {0} 条计时器记录。", "Imported {0} timer records."),
        ["Settings_ImportDuplicate"] = ("这份文件已经导入过了。", "This file was already imported."),
        ["Settings_Storage"] = ("数据库：{0}\n样本总数：{1:N0} 条", "Database: {0}\nSamples: {1:N0}"),
        ["Settings_Language"] = ("语言", "Language"),
        ["Settings_LanguageDesc"] = ("切换界面语言，立即生效。", "Switch the interface language. Applies immediately."),
        ["Settings_Holidays"] = ("节假日", "Holidays"),
        ["Settings_HolidaysDesc"] = ("导入自己地区的节假日，格式：日期,名称（2026-01-01,元旦）", "Import your region's holidays as date,name"),
        ["Holiday_Import"] = ("导入 CSV", "Import CSV"),
        ["Holiday_Imported"] = ("已导入 {0} 条，跳过 {1} 行", "Imported {0}, skipped {1}"),
        ["Holiday_Empty"] = ("文件里没有可用的行", "No usable rows in the file"),
        ["Settings_Theme"] = ("皮肤", "Theme"),
        ["Settings_ThemeDesc"] = ("三套预设，切换即时生效。", "Three presets. Applies immediately."),
        ["Theme_Dark"] = ("深色", "Dark"),
        ["Theme_Light"] = ("浅色", "Light"),
        ["Theme_Midnight"] = ("夜蓝", "Midnight"),

        // 计划
        ["Plan_Title"] = ("预期计划", "Planner"),
        ["Plan_Prev"] = ("上月", "Prev"),
        ["Plan_Today"] = ("今天", "Today"),
        ["Plan_Next"] = ("下月", "Next"),
        ["Plan_WriteHere"] = ("拖拽空白处新建计划", "Drag to add a plan"),
        ["Plan_MonthTitle"] = ("{0} 年 {1} 月", "{0}-{1:D2}"),
        ["Plan_Count"] = ("{0} 项计划", "{0} plans"),
        ["Plan_Holiday"] = ("节假日：{0}", "Holiday: {0}"),
        ["Plan_HolidaySource"] = ("节假日：香港 {0} 天", "Holidays: Hong Kong, {0} days"),
        ["Plan_NoHoliday"] = ("未导入节假日", "No holidays imported"),

        // 计划编辑器
        ["Plan_EditorNew"] = ("新建计划", "New plan"),
        ["Plan_EditorEdit"] = ("编辑计划", "Edit plan"),
        ["Plan_TitleLabel"] = ("标题", "Title"),
        ["Plan_LocationLabel"] = ("地点", "Location"),
        ["Plan_UrlLabel"] = ("网址", "Link"),
        ["Plan_RemindLabel"] = ("到时提醒", "Remind me"),
        ["Plan_Presets"] = ("常用", "Quick tags"),
        ["Plan_Save"] = ("保存", "Save"),
        ["Plan_Cancel"] = ("取消", "Cancel"),
        ["Plan_Delete"] = ("删除", "Delete"),
        ["Preset_Write"] = ("写论文", "Writing"),
        ["Preset_Read"] = ("阅读", "Reading"),
        ["Preset_Meeting"] = ("会议", "Meeting"),
        ["Preset_Exercise"] = ("运动", "Exercise"),
        ["Preset_Break"] = ("休息", "Break"),
        ["Preset_Chores"] = ("杂事", "Chores")
    };

    private AppLanguage _language = AppLanguage.Chinese;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Language => _language;

    public bool IsEnglish => _language == AppLanguage.English;

    public CultureInfo Culture => _language == AppLanguage.Chinese
        ? new CultureInfo("zh-CN")
        : new CultureInfo("en-US");

    /// <summary>绑定源：XAML 用 [Key] 语法取值。</summary>
    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (!Strings.TryGetValue(key, out var pair))
        {
            return key;
        }

        return _language == AppLanguage.Chinese ? pair.Zh : pair.En;
    }

    public void SetLanguage(AppLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;

        // 索引器绑定统一刷新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }
}

/// <summary>C# 里取文案：T("Key") 或 T("Key", arg)。</summary>
public static class Loc
{
    public static string T(string key) => Localization.Instance.Get(key);

    public static string T(string key, params object?[] args)
    {
        string format = Localization.Instance.Get(key);
        try
        {
            return string.Format(Localization.Instance.Culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}

/// <summary>XAML 里取文案：{loc:T Key}。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Localization.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
