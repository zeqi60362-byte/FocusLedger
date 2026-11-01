using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLedger.App.Controls;
using FocusLedger.App.Services;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.ViewModels;

public sealed record GapRow(string Label, string Value, string Share, Brush Brush);

public sealed partial class ReconcileViewModel : ObservableObject
{
    private readonly FocusLedgerStore _store;
    private readonly RuleService _rules;

    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    private IReadOnlyList<TimerClaim> _claims = Array.Empty<TimerClaim>();

    [ObservableProperty]
    private string dateText = string.Empty;

    [ObservableProperty]
    private string claimed = "—";

    [ObservableProperty]
    private string verified = "—";

    [ObservableProperty]
    private string gap = "—";

    [ObservableProperty]
    private string ratio = string.Empty;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private IReadOnlyList<BarSlice> splitSlices = Array.Empty<BarSlice>();

    [ObservableProperty]
    private IReadOnlyList<BarSlice> gapSlices = Array.Empty<BarSlice>();

    public ObservableCollection<GapRow> GapRows { get; } = new();

    public ReconcileViewModel(FocusLedgerStore store, RuleService rules)
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
    private void ImportCsv()
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
                : $"已导入 {inserted} 条声明。{string.Join(" ", parsed.Warnings)}";

            Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"读取失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearClaims()
    {
        var answer = System.Windows.MessageBox.Show(
            "将清除所有已导入的计时器记录（不影响采集数据）。确定吗？",
            "FocusLedger",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _store.ClearTimerClaims();
        Status = "已清除导入的计时器记录。";
        Load();
    }

    public void Load()
    {
        DateText = _date.ToString("yyyy 年 M 月 d 日");

        var (start, end) = DailyAggregator.DayRangeUtc(_date, TimeZoneInfo.Local);
        _claims = _store.LoadTimerClaims(start, end);

        GapRows.Clear();
        SplitSlices = Array.Empty<BarSlice>();
        GapSlices = Array.Empty<BarSlice>();

        if (_claims.Count == 0)
        {
            HasData = false;
            Claimed = "—";
            Verified = "—";
            Gap = "—";
            Ratio = string.Empty;
            Status = string.Empty;
            return;
        }

        var segments = new SegmentBuilder(_rules.Current).Build(_store.LoadSamples(start, end));
        var result = Reconciler.Compute(_claims, segments);

        HasData = true;
        Claimed = Fmt.Duration(result.ClaimedSeconds);
        Verified = Fmt.Duration(result.VerifiedSeconds);
        Gap = Fmt.Duration(result.GapSeconds);
        Ratio = $"实证率 {result.VerifiedRatio:P0}";

        SplitSlices = new[]
        {
            new BarSlice(Math.Max(0.001, result.VerifiedSeconds), Brush("StateActive")),
            new BarSlice(Math.Max(0.001, result.GapSeconds), Brush("StateIdle"))
        };

        var gapParts = new (string Label, double Seconds, string BrushKey)[]
        {
            ("离席", result.IdleSeconds, "StateIdle"),
            ("分心", result.OffTaskSeconds, "StateOffTask"),
            ("存疑", result.AmbiguousSeconds, "StateAmbiguous"),
            ("未采集", result.UntrackedSeconds, "StateLocked"),
            ("打折", result.WeightLossSeconds, "StateSilent")
        };

        GapSlices = gapParts
            .Where(p => p.Seconds >= 1)
            .Select(p => new BarSlice(p.Seconds, Brush(p.BrushKey)))
            .ToArray();

        double totalGap = Math.Max(1, result.GapSeconds);
        foreach (var (label, seconds, brushKey) in gapParts)
        {
            string share = result.GapSeconds > 0 ? $"{seconds / totalGap:P0}" : "—";
            GapRows.Add(new GapRow(label, Fmt.Duration(seconds), share, Brush(brushKey)));
        }
    }

    private static Brush Brush(string key)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromRgb(0x5B, 0x5B, 0x66));
    }
}
