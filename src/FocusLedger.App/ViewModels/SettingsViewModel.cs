using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;


public sealed record ProfileOption(string Id, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<ProfileOption> Profiles { get; } = RulePacks.All
        .Select(p => new ProfileOption(p.Id, Loc.T("Profile_" + p.Id)))
        .ToList();

    [ObservableProperty]
    private string profile = "general";

    [RelayCommand]
    private void SetProfile(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        Profile = RulePacks.Normalize(id);
        _settings.Profile = Profile;
        _settings.Save();
        _rules.SetProfile(Profile);
        Status = Loc.T("Settings_ProfileSaved");
    }
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

    [ObservableProperty]
    private bool isEnglish;

    [ObservableProperty]
    private string currentTheme = "Dark";

    /// <summary>节假日地区代码，导入 CSV 时写入这个地区。</summary>
    [ObservableProperty]
    private string holidayRegion = Holidays.DefaultRegion;

    /// <summary>节假日导入的结果提示。</summary>
    [ObservableProperty]
    private string holidayStatus = string.Empty;

    /// <summary>节假日数据被替换后通知计划页重新读一遍。</summary>
    public event EventHandler? HolidaysChanged;


    public event EventHandler? LanguageChanged;

    public ObservableCollection<string> Exclusions { get; } = new();

    public SettingsViewModel(AppSettings settings, FocusLedgerStore store, RecordingService recording, RuleService rules)
    {
        _settings = settings;
        _store = store;
        _recording = recording;
        _rules = rules;

        StartWithWindows = AutoStart.IsEnabled();
        RetentionDays = settings.RetentionDays;
        IsEnglish = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase);
        CurrentTheme = settings.Theme;
        HolidayRegion = string.IsNullOrWhiteSpace(settings.HolidayRegion)
            ? Holidays.DefaultRegion
            : settings.HolidayRegion;

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
        Status = Loc.T(value ? "Settings_AutoStartOn" : "Settings_AutoStartOff");
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

    partial void OnIsEnglishChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        Localization.Localization.Instance.SetLanguage(value ? AppLanguage.English : AppLanguage.Chinese);
        _settings.Language = value ? "en" : "zh";
        _settings.Save();

        RefreshStorageInfo();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        CurrentTheme = name;
        _settings.Theme = name;
        _settings.Save();
        ThemeService.Apply(ThemeService.Parse(name), null);
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

    partial void OnHolidayRegionChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.HolidayRegion = string.IsNullOrWhiteSpace(value) ? Holidays.DefaultRegion : value.Trim();
        _settings.Save();
        HolidaysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 导入节假日 CSV（日期,名称）。分隔符支持逗号/制表符/分号，
    /// 表头和坏行跳过；整批替换该地区已有的数据。
    /// </summary>
    [RelayCommand]
    private void ImportHolidays()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "CSV",
            Filter = "CSV / TXT|*.csv;*.txt|All files|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var parsed = HolidayCsvParser.Parse(File.ReadAllText(dialog.FileName));
            if (parsed.Days.Count == 0)
            {
                HolidayStatus = Loc.T("Holiday_Empty");
                return;
            }

            string region = string.IsNullOrWhiteSpace(HolidayRegion)
                ? Holidays.DefaultRegion
                : HolidayRegion.Trim().ToUpperInvariant();

            _store.ReplaceHolidays(region, parsed.Days);
            HolidayStatus = Loc.T("Holiday_Imported", parsed.Days.Count, parsed.Skipped);
            HolidaysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HolidayStatus = Loc.T("Settings_ExportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportTimerCsv()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "CSV",
            Filter = "CSV / TXT|*.csv;*.txt|All files|*.*"
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
                Status = parsed.Warnings.Count > 0 ? string.Join(" ", parsed.Warnings) : "—";
                return;
            }

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var (_, inserted, duplicate) = _store.SaveTimerImport(Path.GetFileName(dialog.FileName), hash, parsed.Claims);

            Status = duplicate ? Loc.T("Settings_ImportDuplicate") : Loc.T("Settings_Imported", inserted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("Settings_ExportFailed", ex.Message);
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
            Status = Loc.T("Settings_ExportEmpty");
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
            Status = Loc.T("Settings_Exported", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("Settings_ExportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteAll()
    {
        var result = System.Windows.MessageBox.Show(
            Loc.T("Settings_DeleteConfirm"),
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
        Status = Loc.T("Settings_Deleted");
    }

    public void RefreshStorageInfo()
    {
        long count = _store.CountSamples();
        StorageInfo = Loc.T("Settings_Storage", FocusLedgerStore.DefaultDatabasePath, count);
    }

    private void PersistExclusions()
    {
        _settings.ExcludedApps = Exclusions.ToList();
        _settings.Save();
        Status = Loc.T("Settings_ExcludedUpdated", Exclusions.Count);
    }

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}