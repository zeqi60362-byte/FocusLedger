using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty]
    private NavEntry? selected;

    [ObservableProperty]
    private object? current;

    [ObservableProperty]
    private string statusText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<NavEntry> Entries { get; } = new();

    public MainViewModel(
        TodayViewModel today,
        ReportViewModel report,
        ReconcileViewModel reconcile,
        RulesViewModel rules,
        SettingsViewModel settings,
        RecordingService recording)
    {
        _recording = recording;

        Entries.Add(new NavEntry("今日", "时间轴 · 当前状态", today));
        Entries.Add(new NavEntry("日报", "分心归因 · 深度块", report));
        Entries.Add(new NavEntry("对账", "声称 vs 实证", reconcile));
        Entries.Add(new NavEntry("规则", "什么算工作", rules));
        Entries.Add(new NavEntry("设置", "启动 · 排除 · 数据", settings));

        Selected = Entries[0];

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    partial void OnSelectedChanged(NavEntry? value)
    {
        Current = value?.ViewModel;

        // 切过来时重新取数，否则会一直显示启动那一刻的快照
        switch (value?.ViewModel)
        {
            case ReportViewModel report:
                report.Load();
                break;
            case ReconcileViewModel reconcile:
                reconcile.Load();
                break;
        }
    }

    public void ShowToday() => Selected = Entries[0];

    public void ShowReport() => Selected = Entries[1];

    public void ShowRules() => Selected = Entries[3];

    public void UpdateStatus()
    {
        StatusText = _recording.IsRecording
            ? "● 记录中"
            : "○ 已暂停";
    }

    public void RefreshAll()
    {
        UpdateStatus();

        foreach (var entry in Entries)
        {
            switch (entry.ViewModel)
            {
                case TodayViewModel today:
                    today.Refresh();
                    break;
                case ReportViewModel report:
                    report.Load();
                    break;
                case ReconcileViewModel reconcile:
                    reconcile.Load();
                    break;
                case RulesViewModel rules:
                    rules.Reload();
                    break;
                case SettingsViewModel settings:
                    settings.RefreshStorageInfo();
                    break;
            }
        }
    }
}
