using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
                "report" => ReportCommand(databasePath, args),
                "import" => ImportCsv(databasePath, Option(args, "--file")),
                "reconcile" => Reconcile(databasePath, OptionDate(args, "--date")),
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
              focusledger report  --range 30d [--export md] [--out X.md]
                                                        生成一段时间的审计报告；
                                                        --export md 输出可发布的 Markdown
              focusledger import  --file X.csv           导入外部计时器记录
              focusledger reconcile [--date YYYY-MM-DD]  声称 vs 实证对账
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

    /// <summary>report 有两个入口：默认单日；给了 --range 就走区间报告。</summary>
    private static int ReportCommand(string databasePath, string[] args)
    {
        string? range = Option(args, "--range");
        if (string.IsNullOrWhiteSpace(range))
        {
            return Report(databasePath, OptionDate(args, "--date"), args.Contains("--verbose"));
        }

        int days = ParseRangeDays(range);
        if (days <= 0)
        {
            Console.WriteLine("用法：focusledger report --range 30d [--export md] [--out 报告.md]");
            return 1;
        }

        bool markdown = string.Equals(Option(args, "--export"), "md", StringComparison.OrdinalIgnoreCase);
        string? outPath = Option(args, "--out");

        var rows = CollectRange(databasePath, days);
        if (rows.TotalDaysWithData == 0)
        {
            Console.WriteLine($"最近 {days} 天没有采集数据。");
            return 0;
        }

        string text = markdown ? BuildMarkdown(rows) : BuildConsole(rows);

        if (markdown && !string.IsNullOrWhiteSpace(outPath))
        {
            File.WriteAllText(outPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"已写入 {outPath}");
            return 0;
        }

        Console.Write(text);
        return 0;
    }

    /// <summary>--range 接受 30d / 30 / 7d。</summary>
    private static int ParseRangeDays(string value)
    {
        string digits = value.Trim().TrimEnd('d', 'D');
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0
            ? Math.Min(days, 3660)
            : -1;
    }

    private sealed record RangeRow(DateOnly Date, DailySummary Summary, int SampleCount);

    private sealed record RangeData(
        DateOnly From,
        DateOnly To,
        IReadOnlyList<RangeRow> Rows,
        int TotalDaysWithData,
        long TotalSamples,
        double Effective,
        double Active,
        double Silent,
        double OffTask,
        double Away,
        double Weak,
        int FocusBlocks,
        double LongestBlock,
        int AppSwitches,
        IReadOnlyList<AppUsage> TopApps,
        IReadOnlyList<AppUsage> TopDistractions);

    /// <summary>逐天取数：30 天的样本一次读进内存会有几十万条，按天分段读更省。</summary>
    private static RangeData CollectRange(string databasePath, int days)
    {
        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        var builder = new SegmentBuilder();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rows = new List<RangeRow>();
        var apps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var distractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        long samples = 0;

        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var (start, end) = DailyAggregator.DayRangeUtc(date, TimeZoneInfo.Local);
            var daySamples = store.LoadSamples(start, end);
            samples += daySamples.Count;

            var segments = builder.Build(daySamples);
            var summary = DailyAggregator.Aggregate(segments, date, TimeZoneInfo.Local);
            rows.Add(new RangeRow(date, summary, daySamples.Count));

            foreach (var app in summary.AppTotals)
            {
                if (app.App == "__other__")
                {
                    continue;
                }

                apps[app.App] = apps.TryGetValue(app.App, out double seen) ? seen + app.Seconds : app.Seconds;
            }

            foreach (var item in summary.TopDistractions)
            {
                distractions[item.App] = distractions.TryGetValue(item.App, out double seen)
                    ? seen + item.Seconds
                    : item.Seconds;
            }
        }

        return new RangeData(
            rows[0].Date,
            rows[^1].Date,
            rows,
            rows.Count(r => r.SampleCount > 0),
            samples,
            rows.Sum(r => r.Summary.EffectiveSeconds),
            rows.Sum(r => r.Summary.ActiveSeconds),
            rows.Sum(r => r.Summary.SilentSeconds),
            rows.Sum(r => r.Summary.OffTaskSeconds),
            rows.Sum(r => r.Summary.AwaySeconds),
            rows.Sum(r => r.Summary.WeakEvidenceSeconds),
            rows.Sum(r => r.Summary.FocusBlocks),
            rows.Max(r => r.Summary.LongestFocusBlockSeconds),
            rows.Sum(r => r.Summary.AppSwitches),
            apps.OrderByDescending(kv => kv.Value).Take(10).Select(kv => new AppUsage(kv.Key, kv.Value)).ToList(),
            distractions.OrderByDescending(kv => kv.Value).Take(5).Select(kv => new AppUsage(kv.Key, kv.Value)).ToList());
    }

    /// <summary>终端里的区间报告：逐日表格 + 合计。</summary>
    private static string BuildConsole(RangeData data)
    {
        var sb = new StringBuilder();
        int active = data.TotalDaysWithData;

        sb.AppendLine();
        sb.AppendLine($"{data.From:yyyy-MM-dd} → {data.To:yyyy-MM-dd}    {data.Rows.Count} 天，其中 {active} 天有数据");
        sb.AppendLine();
        sb.AppendLine("  日期          有效     深度     静默     分心     离席   深度块");
        sb.AppendLine("  " + new string('-', 66));

        foreach (var row in data.Rows)
        {
            if (row.SampleCount == 0)
            {
                continue;
            }

            var s = row.Summary;
            sb.AppendLine(
                $"  {row.Date:MM-dd ddd}  {Dur(s.EffectiveSeconds),7}  {Dur(s.ActiveSeconds),7}  " +
                $"{Dur(s.SilentSeconds),7}  {Dur(s.OffTaskSeconds),7}  {Dur(s.AwaySeconds),7}  {s.FocusBlocks,5}");
        }

        sb.AppendLine("  " + new string('-', 66));
        sb.AppendLine(
            $"  合计        {Dur(data.Effective),7}  {Dur(data.Active),7}  {Dur(data.Silent),7}  " +
            $"{Dur(data.OffTask),7}  {Dur(data.Away),7}  {data.FocusBlocks,5}");
        sb.AppendLine();
        sb.AppendLine($"  日均有数据时    {Hours(data.Effective / Math.Max(1, active))}");
        sb.AppendLine($"  深度工作块      {data.FocusBlocks} 次，最长 {data.LongestBlock / 60:F0} 分钟");
        sb.AppendLine($"  应用切换        {data.AppSwitches} 次（日均 {data.AppSwitches / Math.Max(1, active):F0}）");
        sb.AppendLine($"  样本            {data.TotalSamples:N0} 条");

        if (data.TopApps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  软件用时 Top" + data.TopApps.Count + "：" +
                string.Join(" / ", data.TopApps.Select(a => $"{a.App} {Dur(a.Seconds)}")));
        }

        if (data.TopDistractions.Count > 0)
        {
            sb.AppendLine("  分心 Top" + data.TopDistractions.Count + "：" +
                string.Join(" / ", data.TopDistractions.Select(a => $"{a.App} {Dur(a.Seconds)}")));
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>可发布的 Markdown：总体 / 逐日 / 节律 / 软件 / 分心 / 怎么读。</summary>
    private static string BuildMarkdown(RangeData data)
    {
        var sb = new StringBuilder();
        int active = data.TotalDaysWithData;
        double avgEffective = data.Effective / Math.Max(1, active);

        sb.AppendLine($"# 注意力审计 · {data.From:yyyy-MM-dd} → {data.To:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"> {data.Rows.Count} 天里有用数据的是 **{active} 天**，共 {data.TotalSamples:N0} 条样本。" +
                      "全部数据由 FocusLedger 在本机采集，未上传任何内容。");
        sb.AppendLine();

        sb.AppendLine("## 一、总体");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 有效专注合计 | **{Hours(data.Effective)}** |");
        sb.AppendLine($"| 日均（按有数据的天） | {Hours(avgEffective)} |");
        sb.AppendLine($"| 深度 / 静默 | {Hours(data.Active)} / {Hours(data.Silent)} |");
        sb.AppendLine($"| 分心 / 离席 | {Hours(data.OffTask)} / {Hours(data.Away)} |");
        sb.AppendLine($"| 深度工作块 | {data.FocusBlocks} 次（最长 {data.LongestBlock / 60:F0} 分钟） |");
        sb.AppendLine($"| 应用切换 | {data.AppSwitches:N0} 次（日均 {data.AppSwitches / Math.Max(1, active):F0}） |");
        sb.AppendLine();

        sb.AppendLine("## 二、逐日");
        sb.AppendLine();
        sb.AppendLine("| 日期 | 有效专注 | 深度 | 静默 | 分心 | 离席 | 深度块 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in data.Rows)
        {
            if (row.SampleCount == 0)
            {
                continue;
            }

            var s = row.Summary;
            sb.AppendLine(
                $"| {row.Date:MM-dd} {WeekdayName(row.Date.DayOfWeek)} | {Dur(s.EffectiveSeconds)} | " +
                $"{Dur(s.ActiveSeconds)} | {Dur(s.SilentSeconds)} | {Dur(s.OffTaskSeconds)} | " +
                $"{Dur(s.AwaySeconds)} | {s.FocusBlocks} |");
        }

        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine($"每格 = 当天有效专注（{Sparkline(data.Rows)}）");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## 三、周内节律");
        sb.AppendLine();
        sb.AppendLine("| 星期 | 有数据天数 | 日均有效专注 |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var group in data.Rows
                     .Where(r => r.SampleCount > 0)
                     .GroupBy(r => r.Date.DayOfWeek)
                     .OrderBy(g => (int)g.Key))
        {
            double sum = group.Sum(r => r.Summary.EffectiveSeconds);
            sb.AppendLine($"| {WeekdayName(group.Key)} | {group.Count()} | {Hours(sum / group.Count())} |");
        }

        sb.AppendLine();

        if (data.TopApps.Count > 0)
        {
            sb.AppendLine("## 四、软件用时");
            sb.AppendLine();
            sb.AppendLine("| 软件 | 合计 | 占比 |");
            sb.AppendLine("|---|---:|---:|");
            double total = data.TopApps.Sum(a => a.Seconds);
            foreach (var app in data.TopApps)
            {
                sb.AppendLine($"| {app.App} | {Dur(app.Seconds)} | {app.Seconds / Math.Max(1, total):P0} |");
            }

            sb.AppendLine();
        }

        if (data.TopDistractions.Count > 0)
        {
            sb.AppendLine("## 五、分心");
            sb.AppendLine();
            sb.AppendLine("| 应用 | 时长 |");
            sb.AppendLine("|---|---:|");
            foreach (var item in data.TopDistractions)
            {
                sb.AppendLine($"| {item.App} | {Dur(item.Seconds)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 怎么读这份报告");
        sb.AppendLine();
        sb.AppendLine("- **有效专注** = 深度专注 + 静默专注，按原始时长计入；两者区别在于有没有键鼠输入。");
        sb.AppendLine("- **静默专注** 是「手不动但在看/在听」；和深度专注一样按原始时长计入，只单独标成弱证据段。");
        sb.AppendLine("- **离席** 不是休息，是「没有正面证据」——锁屏、走开、发呆都会落到这里。");
        sb.AppendLine("- 只有**有数据的天**才计入日均，没开机的那天不会稀释平均值。");
        sb.AppendLine();
        sb.AppendLine($"> 生成于 {DateTime.Now:yyyy-MM-dd HH:mm}，FocusLedger · 专注账本");

        return sb.ToString();
    }

    /// <summary>用方块字符把每天的有效专注画成一条迷你柱状图。</summary>
    private static string Sparkline(IReadOnlyList<RangeRow> rows)
    {
        const string bars = "▁▂▃▄▅▆▇█";
        var withData = rows.Where(r => r.SampleCount > 0).ToList();
        if (withData.Count == 0)
        {
            return string.Empty;
        }

        double max = Math.Max(1, withData.Max(r => r.Summary.EffectiveSeconds));
        var sb = new StringBuilder(withData.Count);
        foreach (var row in withData)
        {
            int index = (int)Math.Round(row.Summary.EffectiveSeconds / max * (bars.Length - 1));
            sb.Append(bars[Math.Clamp(index, 0, bars.Length - 1)]);
        }

        return sb.ToString();
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>小时级展示：0.7h 这种。</summary>
    private static string Hours(double seconds) => $"{seconds / 3600:F1}h";

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
        Console.WriteLine($"{date:yyyy-MM-dd}   有效专注 {Dur(summary.EffectiveSeconds)}");
        Console.WriteLine();
        Console.WriteLine($"  深度 {Dur(summary.ActiveSeconds)}  |  静默 {Dur(summary.SilentSeconds)}");
        Console.WriteLine($"  分心 {Dur(summary.OffTaskSeconds)}  |  离席 {Dur(summary.AwaySeconds)}");
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

    // ---------- import / reconcile ----------

    private static int ImportCsv(string databasePath, string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.WriteLine("用法：focusledger import --file X.csv");
            return 1;
        }

        string text = File.ReadAllText(file);
        var parsed = TimerCsvParser.Parse(text);

        foreach (string warning in parsed.Warnings)
        {
            Console.WriteLine($"  提示：{warning}");
        }

        if (parsed.Claims.Count == 0)
        {
            Console.WriteLine("没有可导入的记录。");
            return 1;
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        var (_, inserted, duplicate) = store.SaveTimerImport(Path.GetFileName(file), hash, parsed.Claims);

        if (duplicate)
        {
            Console.WriteLine("这份文件已经导入过了（按内容哈希判重）。");
            return 0;
        }

        Console.WriteLine($"已导入 {inserted} 条声明，合计 {Dur(parsed.TotalClaimedSeconds)}。");
        return 0;
    }

    private static int Reconcile(string databasePath, DateOnly date)
    {
        using var store = new FocusLedgerStore(databasePath);
        store.Initialize();

        var (start, end) = DailyAggregator.DayRangeUtc(date, TimeZoneInfo.Local);
        var claims = store.LoadTimerClaims(start, end);

        if (claims.Count == 0)
        {
            Console.WriteLine($"{date:yyyy-MM-dd} 还没有导入过计时器记录。");
            Console.WriteLine("先运行：focusledger import --file 你的导出.csv");
            return 0;
        }

        var segments = new SegmentBuilder().Build(store.LoadSamples(start, end));
        var result = Reconciler.Compute(claims, segments);

        Console.WriteLine();
        Console.WriteLine($"{date:yyyy-MM-dd}  对账");
        Console.WriteLine();
        Console.WriteLine(
            $"  声称 {Dur(result.ClaimedSeconds)}    实证 {Dur(result.VerifiedSeconds)}    " +
            $"差额 {Dur(result.GapSeconds)}（{1 - result.VerifiedRatio:P0}）");
        Console.WriteLine();
        Console.WriteLine($"  离席   {Dur(result.AwaySeconds)}");
        Console.WriteLine($"  分心   {Dur(result.OffTaskSeconds)}");
        Console.WriteLine($"  未采集 {Dur(result.UntrackedSeconds)}");
        Console.WriteLine($"  打折   {Dur(result.WeightLossSeconds)}  ← 确实在学，但属被动/静默，按权重折算");
        Console.WriteLine($"  ------------------------------------");
        Console.WriteLine($"  合计   {Dur(result.AttributedGapSeconds)}");
        Console.WriteLine();
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
