using System.Globalization;
using FocusLedger.Collector.Windows;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.Cli;

internal static class Program
{
    private const int FlushBatch = 12; // 12 × 5s = 60s

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string databasePath = Option(args, "--db") ?? FocusLedgerStore.DefaultDatabasePath;

        try
        {
            return command switch
            {
                "probe" => await ProbeAsync(OptionInt(args, "--seconds", 20)),
                "capture" => await CaptureAsync(databasePath, OptionInt(args, "--minutes", 10)),
                "report" => Report(databasePath, OptionDate(args, "--date"), args.Contains("--verbose")),
                "prune" => Prune(databasePath, OptionInt(args, "--days", 14)),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            focusledger — 本地注意力审计

            用法:
              focusledger probe   [--seconds N]          实时打印信号，不写数据库
              focusledger capture [--minutes N] [--db P] 采集一段时间并立刻出报告
              focusledger report  [--date YYYY-MM-DD]    生成某天的对账报告
              focusledger prune   [--days N]             清理 N 天前的原始样本

            数据库默认位置:
              %LOCALAPPDATA%\FocusLedger\data.db
            """);
        return 0;
    }

    // ---------- probe ----------

    private static async Task<int> ProbeAsync(int seconds)
    {
        using var collector = new CollectorService();
        await collector.StartAsync();

        Console.WriteLine();
        Console.WriteLine("FocusLedger · 信号探针（不写库）");
        Console.WriteLine($"键盘钩子: {(collector.HooksInstalled ? "已安装" : "未安装（降级模式）")}   媒体信号: {(collector.MediaAvailable ? "可用" : "不可用")}");
        Console.WriteLine();
        Console.WriteLine("  时间      应用                    空闲   变化  媒体        标题");
        Console.WriteLine("  " + new string('-', 96));

        await foreach (var sample in collector.CollectAsync(TimeSpan.FromSeconds(seconds)))
        {
            Console.WriteLine(
                $"  {Local(sample.TsUtcMs):HH:mm:ss}  {Trim(sample.App, 22),-22} {sample.IdleSeconds,4}s {sample.VisualDelta,5}  {sample.MediaState,-10}  {Trim(sample.Title, 30)}");
        }

        Console.WriteLine();
        return 0;
    }

    // ---------- capture ----------

    private static async Task<int> CaptureAsync(string databasePath, int minutes)
    {
        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        using var collector = new CollectorService();
        await collector.StartAsync();

        Console.WriteLine();
        Console.WriteLine($"开始采集 {minutes} 分钟 → {databasePath}");
        Console.WriteLine($"键盘钩子: {(collector.HooksInstalled ? "已安装" : "未安装（降级模式）")}   媒体信号: {(collector.MediaAvailable ? "可用" : "不可用")}");
        Console.WriteLine();

        var buffer = new List<Sample>();
        int total = 0;

        await foreach (var sample in collector.CollectAsync(TimeSpan.FromMinutes(minutes)))
        {
            buffer.Add(sample);
            total++;

            Console.WriteLine(
                $"  {Local(sample.TsUtcMs):HH:mm:ss}  {Trim(sample.App, 20),-20} idle={sample.IdleSeconds,4}s " +
                $"键={sample.KeyCount,3} 鼠={sample.MouseCount,3} 变化={sample.VisualDelta,3} {sample.MediaState}");

            if (buffer.Count >= FlushBatch)
            {
                store.AppendSamples(buffer);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            store.AppendSamples(buffer);
        }

        Console.WriteLine();
        Console.WriteLine($"采集结束，共写入 {total} 条样本。");
        Console.WriteLine();

        return Report(databasePath, DateOnly.FromDateTime(DateTime.Now), verbose: false);
    }

    // ---------- report ----------

    private static int Report(string databasePath, DateOnly date, bool verbose)
    {
        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        var (start, end) = DailyAggregator.DayRangeUtc(date, TimeZoneInfo.Local);
        var samples = store.LoadSamples(start, end);

        if (samples.Count == 0)
        {
            Console.WriteLine($"{date:yyyy-MM-dd} 没有采集数据。先运行 focusledger capture 采集一段时间。");
            return 0;
        }

        var segments = new SegmentBuilder().Build(samples);
        var summary = DailyAggregator.Aggregate(segments, date, TimeZoneInfo.Local);

        if (verbose)
        {
            Console.WriteLine($"  [debug] 本地时区 {TimeZoneInfo.Local.Id}");
            Console.WriteLine($"  [debug] 日区间 UTC {Local(start):yyyy-MM-dd HH:mm:ss} → {Local(end):yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  [debug] 样本 {samples.Count} 条，首 {Local(samples[0].TsUtcMs):HH:mm:ss} 末 {Local(samples[^1].TsUtcMs):HH:mm:ss}");
            foreach (var s in segments.Take(12))
            {
                Console.WriteLine($"  [debug] {Local(s.StartUtcMs):HH:mm:ss}-{Local(s.EndUtcMs):HH:mm:ss} {s.App,-14} {s.State,-13} w={s.Weight:F2}");
            }
        }

        double coveredSeconds = segments.Sum(s => s.DurationSeconds);

        Console.WriteLine();
        Console.WriteLine($"{date:yyyy-MM-dd}   有效专注 {Dur(summary.EffectiveSeconds)} [{Dur(summary.EffectiveLowSeconds)}–{Dur(summary.EffectiveHighSeconds)}]");
        Console.WriteLine();
        Console.WriteLine($"  深度 {Dur(summary.ActiveSeconds)}  |  被动 {Dur(summary.PassiveSeconds)}  |  静默 {Dur(summary.SilentSeconds)}");
        Console.WriteLine($"  分心 {Dur(summary.OffTaskSeconds)}  |  离席 {Dur(summary.IdleSeconds)}  |  锁屏 {Dur(summary.LockedSeconds)}  |  存疑 {Dur(summary.AmbiguousSeconds)}");
        Console.WriteLine();
        Console.WriteLine($"  深度工作块 {summary.FocusBlocks} 次，最长 {summary.LongestFocusBlockSeconds / 60:F0} 分钟");
        Console.WriteLine($"  应用切换 {summary.AppSwitches} 次");
        Console.WriteLine($"  样本 {samples.Count} 条，覆盖 {Dur(coveredSeconds)}");

        if (summary.TopDistractions.Count > 0)
        {
            string top = string.Join(" / ", summary.TopDistractions.Select(d => $"{d.App} {d.Seconds / 60:F0}m"));
            Console.WriteLine($"  分心 Top{summary.TopDistractions.Count}：{top}");
        }

        Console.WriteLine();
        return 0;
    }

    private static int Prune(string databasePath, int days)
    {
        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        long cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        int deleted = store.PruneSamples(cutoff);

        Console.WriteLine($"已删除 {deleted} 条早于 {days} 天的样本。");
        return 0;
    }

    // ---------- helpers ----------

    /// <summary>自适应时长：不足 1 小时显示分钟，避免几十分钟被舍成 0.0h。</summary>
    private static string Dur(double seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds:F0}s";
        }

        return seconds < 3600
            ? $"{seconds / 60:F0}m"
            : $"{seconds / 3600:F1}h";
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static DateTime Local(long utcMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().DateTime;

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int OptionInt(string[] args, string name, int fallback)
        => int.TryParse(Option(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static DateOnly OptionDate(string[] args, string name)
        => DateOnly.TryParseExact(Option(args, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : DateOnly.FromDateTime(DateTime.Now);
}
