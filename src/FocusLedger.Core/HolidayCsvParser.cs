namespace FocusLedger.Core;

/// <summary>节假日 CSV 的解析结果。</summary>
public sealed record HolidayCsvResult(IReadOnlyList<(string Date, string Name)> Days, int Skipped);

/// <summary>
/// 「日期,名称」两列的节假日 CSV。分隔符兼容逗号 / 制表符 / 分号，
/// 表头与坏行一律跳过并计数。日期统一归一成 yyyy-MM-dd。
/// </summary>
public static class HolidayCsvParser
{
    public static HolidayCsvResult Parse(string? text)
    {
        var days = new List<(string Date, string Name)>();
        int skipped = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new HolidayCsvResult(days, skipped);
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t', '\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(new[] { ',', '\t', ';' }, 2);
            if (parts.Length < 2)
            {
                skipped++;
                continue;
            }

            string date = parts[0].Trim().Trim('"');
            string name = parts[1].Trim().Trim('"');

            if (name.Length == 0 || !TryParseDate(date, out var parsed))
            {
                skipped++;
                continue;
            }

            days.Add((parsed.ToString("yyyy-MM-dd"), name));
        }

        return new HolidayCsvResult(days, skipped);
    }

    /// <summary>容忍几种常见写法：2026-01-01 / 2026/1/1 / 2026.01.01。</summary>
    private static bool TryParseDate(string value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Replace('/', '-').Replace('.', '-');

        return DateOnly.TryParseExact(normalized, "yyyy-M-d",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out date)
               || DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out date);
    }
}
