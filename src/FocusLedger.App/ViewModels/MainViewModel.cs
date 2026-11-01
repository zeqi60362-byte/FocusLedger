using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;

namespace FocusLedger.App.ViewModels;

public sealed record NavEntry(string Title, string Hint, object ViewModel)
{
    public override string ToString() => Title;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly RecordingService _recording;
    private readonly DispatcherTimer _statusTimer;

    private readonly TodayViewModel _today;
    private readonly ReportViewModel _report;
    private readonly RulesViewModel _rules;
    private readonly CalendarViewModel _calendar;
    private readonly SettingsViewModel _settings;

    [ObservableProperty]
    private NavEntry? selected;

    [ObservableProperty]
    private object? current;

    [ObservableProperty]
    private string statusText = string.Empty;

    public ObservableCollection<NavEntry> Entries { get; } = new();

    public MainViewModel(
        TodayViewModel today,
        ReportViewModel report,
        RulesViewModel rules,
        CalendarViewModel calendar,
        SettingsViewModel settings,
        RecordingService recording)
    {
        _recording = recording;
        _today = today;
        _report = report;
        _rules = rules;
        _calendar = calendar;
        _settings = settings;

        BuildEntries(0);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    private void BuildEntries(int selectedIndex)
    {
        Entries.Clear();
        Entries.Add(new NavEntry(Loc.T("Nav_Today"), Loc.T("Nav_TodayHint"), _today));
        Entries.Add(new NavEntry(Loc.T("Nav_Report"), Loc.T("Nav_ReportHint"), _report));
        Entries.Add(new NavEntry(Loc.T("Nav_Rules"), Loc.T("Nav_RulesHint"), _rules));
        Entries.Add(new NavEntry(Loc.T("Nav_Plan"), Loc.T("Nav_PlanHint"), _calendar));
        Entries.Add(new NavEntry(Loc.T("Nav_Settings"), Loc.T("Nav_SettingsHint"), _settings));

        Selected = Entries[Math.Clamp(selectedIndex, 0, Entries.Count - 1)];
    }

    /// <summary>切换语言后重建导航并刷新所有页面文案。</summary>
    public void RebuildForLanguage()
    {
        int index = Selected is null ? 0 : Entries.IndexOf(Selected);
        BuildEntries(index < 0 ? 0 : index);
        RefreshAll();
    }

    partial void OnSelectedChanged(NavEntry? value)
    {
        Current = value?.ViewModel;

        if (value?.ViewModel is ReportViewModel report)
        {
            report.Load();
        }
    }

    public void ShowToday() => Selected = Entries[0];

    public void ShowReport() => Selected = Entries[1];

    public void ShowRules() => Selected = Entries[2];

    public void UpdateStatus()
    {
        StatusText = _recording.IsRecording ? Loc.T("Status_Recording") : Loc.T("Status_Paused");
    }

    public void RefreshAll()
    {
        UpdateStatus();
        _today.Refresh();
        _report.Load();
        _rules.Reload();
        _calendar.Reload();
        _settings.RefreshStorageInfo();
    }
}
