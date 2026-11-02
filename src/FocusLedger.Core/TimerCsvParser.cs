using System.Globalization;
using System.Text;

namespace FocusLedger.Core;

/// <summary>外部计时器声称的一段"专注"。</summary>
public sealed record TimerClaim(
    long StartUtcMs,
    long EndUtcMs,
    string? Label,
    double ClaimedSeconds)
{
    public static TimerClaim Create(long startUtcMs, long endUtcMs, string? label)
    {
        double seconds = Math.Max(0, (endUtcMs - startUtcMs) / 1000.0);
        return new TimerClaim(startUtcMs, endUtcMs, label, seconds);
    }
}

public sealed record TimerParseResult(
    IReadOnlyList<TimerClaim> Claims,
    IReadOnlyList<string> Warnings)
{
    public double TotalClaimedSeconds => Claims.Sum(c => c.ClaimedSeconds);
}

/// <summary>
/// 外部计时器 CSV 解析。目标是"宽容"：各家导出格式不统一是常态，
/// 认不出的行跳过并给出警告，绝不因为一行脏数据就整份文件失败。
/// </summary>
public static class TimerCsvParser
{
    private static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm",
        "yyyy-M-d H:mm:ss", "yyyy-M-d H:mm",
        "yyyyMMddHHmmss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ",
        "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm"
    };

    private static readonly string[] TimeOnlyFormats = { "HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm" };

    private static readonly string[] DurationFormats = { "hh\\:mm\\:ss", "h\\:mm\\:ss", "mm\\:ss" };

    public static TimerParseResult Parse(string csvText, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(csvText);

        var zone = timeZone ?? TimeZoneInfo.Local;
        var warnings = new List<string>();

        var lines = csvText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0)
        {
            return new TimerParseResult(Array.Empty<TimerClaim>(), new[] { "文件是空的。" });
        }

        char delimiter = DetectDelimiter(lines[0]);
        var rows = lines.Select(l => SplitLine(l, delimiter)).ToList();
        rows[0][0] = rows[0][0].TrimStart('\uFEFF');

        var header = rows[0];
        bool hasHeader = header.Any(IsHeaderKeyword);

        int startColumn = -1, endColumn = -1, durationColumn = -1, dateColumn = -1, labelColumn = -1;

        if (hasHeader)
        {
            for (int i = 0; i < header.Count; i++)
            {
                string cell = header[i].Trim().ToLowerInvariant();

                if (startColumn < 0 && ContainsAny(cell, "开始", "起始", "start", "begin", "from"))
                {
                    startColumn = i;
                }
                else if (endColumn < 0 && ContainsAny(cell, "结束", "完成", "end", "finish", "截止"))
                {
                    endColumn = i;
                }
                else if (durationColumn < 0 && ContainsAny(cell, "时长", "耗时", "duration", "分钟"))
                {
                    durationColumn = i;
                }
                else if (dateColumn < 0 && ContainsAny(cell, "日期", "date", "day"))
                {
                    dateColumn = i;
                }
                else if (labelColumn < 0 && ContainsAny(cell, "任务", "标签", "名称", "备注", "task", "label", "name", "title", "project"))
                {
                    labelColumn = i;
                }
            }

            rows = rows.Skip(1).ToList();
        }
        else
        {
            startColumn = 0;
            endColumn = 1;
            warnings.Add("没有识别到表头，按「第一列=开始时间、第二列=结束时间」解析。");
        }

        if (startColumn < 0)
        {
            return new TimerParseResult(
                Array.Empty<TimerClaim>(),
                new[] { "找不到「开始时间」列。支持的表头关键字：开始 / start / 起始。" });
        }

        var claims = new List<TimerClaim>();
        int skipped = 0;

        foreach (var row in rows)
        {
            if (row.Count <= startColumn
                || !TryParseLocal(row[startColumn], out var startLocal, out bool startHasDate))
            {
                skipped++;
                continue;
            }

            // 只有 HH:mm 没有日期时，用「日期」列补上
            if (!startHasDate)
            {
                if (dateColumn < 0
                    || dateColumn >= row.Count
                    || !TryParseLocal(row[dateColumn], out var dateLocal, out _))
                {
                    skipped++;
                    continue;
                }

                startLocal = dateLocal.Date + startLocal.TimeOfDay;
            }

            long startUtc = ToUtc(startLocal, zone);

            string? label = labelColumn >= 0 && labelColumn < row.Count ? row[labelColumn].Trim() : null;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = null;
            }

            long endUtc;
            if (endColumn >= 0 && endColumn < row.Count
                && TryParseLocal(row[endColumn], out var endLocal, out bool endHasDate))
            {
                if (!endHasDate)
                {
                    endLocal = startLocal.Date + endLocal.TimeOfDay;
                    if (endLocal < startLocal)
                    {
                        endLocal = endLocal.AddDays(1); // 跨零点
                    }
                }

                endUtc = ToUtc(endLocal, zone);
            }
            else if (durationColumn >= 0 && durationColumn < row.Count
                     && TryParseDuration(row[durationColumn], out double minutes))
            {
                endUtc = startUtc + (long)(minutes * 60_000);
            }
            else
            {
                skipped++;
                continue;
            }

            if (endUtc <= startUtc)
            {
                skipped++;
                continue;
            }

            claims.Add(TimerClaim.Create(startUtc, endUtc, label));
        }

        if (skipped > 0)
        {
            warnings.Add($"跳过 {skipped} 行无法解析的记录。");
        }

        if (claims.Count == 0)
        {
            warnings.Add("没有解析出任何有效记录，请检查列名与时间格式。");
        }

        return new TimerParseResult(claims, warnings);
    }

    private static long ToUtc(DateTime local, TimeZoneInfo zone)
        => new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();

    private static bool ContainsAny(string value, params string[] keywords) =>
        keywords.Any(k => value.Contains(k, StringComparison.Ordinal));

    private static bool IsHeaderKeyword(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return false;
        }

        string value = cell.Trim().ToLowerInvariant();
        return ContainsAny(value, "开始", "结束", "时长", "日期", "任务", "start", "end", "duration", "date", "task", "label", "name");
    }

    private static char DetectDelimiter(string line) =>
        line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : ',';

    private static List<string> SplitLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static bool TryParseLocal(string raw, out DateTime local, out bool hasDate)
    {
        local = default;
        hasDate = false;

        string value = raw.Trim().Trim('"');
        if (value.Length == 0)
        {
            return false;
        }

        // 顺序很重要：必须先试「带日期」的严格格式，再试「只有时间」，
        // 最后才用宽松解析。否则 "14:00" 会被宽松解析当成「今天 14:00」，
        // 让「日期列 + 时间列」的组合逻辑永远走不到。
        if (DateTime.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            local = DateTime.SpecifyKind(exact, DateTimeKind.Unspecified);
            hasDate = true;
            return true;
        }

        if (DateTime.TryParseExact(value, TimeOnlyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timeOnly))
        {
            local = DateTime.Today.Add(timeOnly.TimeOfDay);
            hasDate = false;
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            local = DateTime.SpecifyKind(loose, DateTimeKind.Unspecified);
            hasDate = true;
            return true;
        }

        return false;
    }

    private static bool TryParseDuration(string raw, out double minutes)
    {
        minutes = 0;
        string value = raw.Trim().Trim('"');
        if (value.Length == 0)
        {
            return false;
        }

        if (TimeSpan.TryParseExact(value, DurationFormats, CultureInfo.InvariantCulture, out var span))
        {
            minutes = span.TotalMinutes;
            return true;
        }

        foreach (string suffix in new[] { "分钟", "分", "minutes", "min", "m" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].Trim();
                break;
            }
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            minutes = number;
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var loose))
        {
            minutes = loose.TotalMinutes;
            return true;
        }

        return false;
    }
}
