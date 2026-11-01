using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly FocusLedgerStore _store;
    private readonly RecordingService _recording;
    private readonly RuleService _rules;
    private bool _initialized;

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private int retentionDays;

    [ObservableProperty]
    private string newExclusion = string.Empty;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private string storageInfo = string.Empty;

    public ObservableCollection<string> Exclusions { get; } = new();

    public SettingsViewModel(AppSettings settings, FocusLedgerStore store, RecordingService recording, RuleService rules)
    {
        _settings = settings;
        _store = store;
        _recording = recording;
        _rules = rules;

        StartWithWindows = AutoStart.IsEnabled();
        RetentionDays = settings.RetentionDays;

        foreach (var app in settings.ExcludedApps)
        {
            Exclusions.Add(app);
        }

        RefreshStorageInfo();
        _initialized = true;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        AutoStart.Set(value);
        _settings.AutoStart = value;
        _settings.Save();
        Status = value ? "已设置为开机自启。" : "已取消开机自启。";
    }

    partial void OnRetentionDaysChanged(int value)
    {
        if (!_initialized)
        {
            return;
        }

        if (value < 1)
        {
            RetentionDays = 1;
            return;
        }

        _settings.RetentionDays = value;
        _settings.Save();
    }

    [RelayCommand]
    private void AddExclusion()
    {
        string app = NewExclusion.Trim();
        if (app.Length == 0)
        {
            return;
        }

        if (!Exclusions.Any(e => string.Equals(e, app, StringComparison.OrdinalIgnoreCase)))
        {
            Exclusions.Add(app);
            PersistExclusions();
        }

        NewExclusion = string.Empty;
    }

    [RelayCommand]
    private void RemoveExclusion(string? app)
    {
        if (app is null)
        {
            return;
        }

        if (Exclusions.Remove(app))
        {
            PersistExclusions();
        }
    }

    [RelayCommand]
    private void ImportTimerCsv()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择计时器导出的 CSV",
            Filter = "CSV / 文本|*.csv;*.txt|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(dialog.FileName);
            var parsed = TimerCsvParser.Parse(text);

            if (parsed.Claims.Count == 0)
            {
                Status = parsed.Warnings.Count > 0
                    ? string.Join(" ", parsed.Warnings)
                    : "没有解析出任何记录。";
                return;
            }

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var (_, inserted, duplicate) = _store.SaveTimerImport(Path.GetFileName(dialog.FileName), hash, parsed.Claims);

            Status = duplicate
                ? "这份文件已经导入过了。"
                : $"已导入 {inserted} 条计时器记录。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"读取失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        var (start, end) = DailyAggregator.DayRangeUtc(date, TimeZoneInfo.Local);
        var segments = new SegmentBuilder(_rules.Current).Build(_store.LoadSamples(start, end));

        if (segments.Count == 0)
        {
            Status = "今天还没有数据可以导出。";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("start_local,end_local,duration_min,app,state,weight,rule");

        foreach (var segment in segments)
        {
            var from = DateTimeOffset.FromUnixTimeMilliseconds(segment.StartUtcMs).ToLocalTime();
            var to = DateTimeOffset.FromUnixTimeMilliseconds(segment.EndUtcMs).ToLocalTime();
            builder.AppendLine(
                $"{from:yyyy-MM-dd HH:mm:ss},{to:yyyy-MM-dd HH:mm:ss},{segment.DurationSeconds / 60:F1}," +
                $"{Csv(segment.App)},{segment.State},{segment.Weight:F2},{segment.RuleId}");
        }

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, $"FocusLedger-{date:yyyyMMdd}.csv");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"已导出到 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteAll()
    {
        var result = System.Windows.MessageBox.Show(
            "将删除全部采集数据（样本、判定段、标注），且无法恢复。\n\n确定继续吗？",
            "FocusLedger",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _store.DeleteAll();
        _recording.ReloadToday();
        RefreshStorageInfo();
        Status = "已删除全部数据。";
    }

    public void RefreshStorageInfo()
    {
        long count = _store.CountSamples();
        StorageInfo = $"数据库：{FocusLedgerStore.DefaultDatabasePath}\n样本总数：{count:N0} 条";
    }

    private void PersistExclusions()
    {
        _settings.ExcludedApps = Exclusions.ToList();
        _settings.Save();
        Status = $"排除列表已更新（{Exclusions.Count} 项）。";
    }

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
