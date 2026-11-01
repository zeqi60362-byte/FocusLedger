using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

public sealed class TimerCsvParserTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Parses_StartAndEndColumns()
    {
        const string csv = """
                           开始时间,结束时间,任务
                           2026-11-01 14:00:00,2026-11-01 14:25:00,写论文
                           2026-11-01 14:30:00,2026-11-01 15:00:00,读文献
                           """;

        var result = TimerCsvParser.Parse(csv, Utc);

        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(1500, result.Claims[0].ClaimedSeconds, 0);
        Assert.Equal("写论文", result.Claims[0].Label);
        Assert.Equal(3300, result.TotalClaimedSeconds, 0);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parses_DurationColumn()
    {
        const string csv = """
                           开始时间,时长,任务
                           2026-11-01 14:00,25分钟,写论文
                           2026-11-01 15:00,1:30:00,读书
                           """;

        var result = TimerCsvParser.Parse(csv, Utc);

        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(1500, result.Claims[0].ClaimedSeconds, 0);
        Assert.Equal(5400, result.Claims[1].ClaimedSeconds, 0);
    }

    [Fact]
    public void CombinesDateColumnWithTimeOnly()
    {
        const string csv = """
                           日期,开始时间,结束时间,任务
                           2026-11-01,14:00,14:25,写论文
                           """;

        var result = TimerCsvParser.Parse(csv, Utc);

        var claim = Assert.Single(result.Claims);
        Assert.Equal(1500, claim.ClaimedSeconds, 0);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), claim.StartUtcMs);
    }

    [Fact]
    public void SkipsUnparseableRowsButKeepsGoodOnes()
    {
        const string csv = """
                           开始时间,结束时间
                           not-a-date,also-bad
                           2026-11-01 14:00:00,2026-11-01 14:25:00
                           """;

        var result = TimerCsvParser.Parse(csv, Utc);

        Assert.Single(result.Claims);
        Assert.Contains(result.Warnings, w => w.Contains("跳过 1 行", StringComparison.Ordinal));
    }

    [Fact]
    public void HandlesTabSeparated()
    {
        string csv = "开始时间\t结束时间\t任务\n2026-11-01 14:00\t2026-11-01 14:25\t写论文";

        var result = TimerCsvParser.Parse(csv, Utc);

        var claim = Assert.Single(result.Claims);
        Assert.Equal(1500, claim.ClaimedSeconds, 0);
    }

    [Fact]
    public void EmptyFile_ReturnsWarning()
    {
        var result = TimerCsvParser.Parse("   \n  \n", Utc);

        Assert.Empty(result.Claims);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void RejectsEndBeforeStart()
    {
        const string csv = """
                           开始时间,结束时间
                           2026-11-01 15:00:00,2026-11-01 14:00:00
                           """;

        var result = TimerCsvParser.Parse(csv, Utc);
        Assert.Empty(result.Claims);
    }
}
