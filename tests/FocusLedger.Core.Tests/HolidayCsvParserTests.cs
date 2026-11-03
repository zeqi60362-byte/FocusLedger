using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

/// <summary>
/// 节假日 CSV 解析。设置页的「导入 CSV」依赖它：表头、坏行都要跳过，
/// 分隔符要容错，日期要归一成 yyyy-MM-dd。
/// </summary>
public sealed class HolidayCsvParserTests
{
    [Fact]
    public void ParsesCommaSeparatedRows()
    {
        var result = HolidayCsvParser.Parse("2026-01-01,元旦\n2026-02-17,农历年初一\n");

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(("2026-01-01", "元旦"), result.Days[0]);
        Assert.Equal(("2026-02-17", "农历年初一"), result.Days[1]);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void SkipsHeaderAndBadRows()
    {
        string text =
            "日期,名称\n" +
            "2026-05-01,劳动节\n" +
            "这一行没有逗号\n" +
            "2026-13-01,月份不合法\n" +
            "2026-07-01,\n" +
            "2026-10-01,国庆日\n";

        var result = HolidayCsvParser.Parse(text);

        Assert.Equal(2, result.Days.Count);
        Assert.Equal("劳动节", result.Days[0].Name);
        Assert.Equal("国庆日", result.Days[1].Name);
        Assert.Equal(4, result.Skipped);   // 表头 + 无分隔符 + 坏月份 + 空名称
    }

    [Theory]
    [InlineData("2026-01-01,HK\n2026-01-02,CN")]
    [InlineData("2026-01-01\tHK\n2026-01-02\tCN")]
    [InlineData("2026-01-01;HK\n2026-01-02;CN")]
    public void AcceptsCommonSeparators(string text)
    {
        var result = HolidayCsvParser.Parse(text);

        Assert.Equal(2, result.Days.Count);
        Assert.Equal("2026-01-01", result.Days[0].Date);
    }

    [Theory]
    [InlineData("2026/1/1", "2026-01-01")]
    [InlineData("2026.1.1", "2026-01-01")]
    [InlineData("2026-1-1", "2026-01-01")]
    public void NormalizesDateFormats(string input, string expected)
    {
        var result = HolidayCsvParser.Parse($"{input},元旦");

        Assert.Single(result.Days);
        Assert.Equal(expected, result.Days[0].Date);
    }

    [Fact]
    public void HandlesCrLfAndQuotes()
    {
        var result = HolidayCsvParser.Parse("\"2026-04-03\",\"耶稣受难节\"\r\n");

        Assert.Single(result.Days);
        Assert.Equal(("2026-04-03", "耶稣受难节"), result.Days[0]);
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        Assert.Empty(HolidayCsvParser.Parse(null).Days);
        Assert.Empty(HolidayCsvParser.Parse("   \n\n").Days);
    }
}
