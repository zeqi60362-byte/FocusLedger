using Dapper;
using FocusLedger.Core;
using Microsoft.Data.Sqlite;

namespace FocusLedger.Storage;

/// <summary>
/// SQLite 持久层。M1 只落盘原始样本（samples），判定段在读取时重算 ——
/// 这样报告永远与最新规则一致，也不会出现"段表与样本表不同步"的经典问题。
/// 段表（segments）与日聚合表（daily）留给 M2 做缓存用。
/// </summary>
public sealed class FocusLedgerStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;

    public FocusLedgerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
    }

    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusLedger",
            "data.db");

    public void Initialize()
    {
        _connection.Execute("PRAGMA journal_mode=WAL;");
        _connection.Execute("PRAGMA synchronous=NORMAL;");
        _connection.Execute(Schema);
        _connection.Execute(
            "INSERT INTO meta(key, value) VALUES('schema_version', @v) ON CONFLICT(key) DO NOTHING;",
            new { v = SchemaVersion.ToString() });
    }

    public int AppendSamples(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return 0;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSampleSql;

        var pTs = command.Parameters.Add("$ts", SqliteType.Integer);
        var pHwnd = command.Parameters.Add("$hwnd", SqliteType.Integer);
        var pApp = command.Parameters.Add("$app", SqliteType.Text);
        var pTitle = command.Parameters.Add("$title", SqliteType.Text);
        var pCategory = command.Parameters.Add("$category", SqliteType.Text);
        var pC = command.Parameters.Add("$c", SqliteType.Real);
        var pIdle = command.Parameters.Add("$idle", SqliteType.Integer);
        var pKeys = command.Parameters.Add("$keys", SqliteType.Integer);
        var pMouse = command.Parameters.Add("$mouse", SqliteType.Integer);
        var pHash = command.Parameters.Add("$hash", SqliteType.Integer);
        var pDelta = command.Parameters.Add("$delta", SqliteType.Integer);
        var pAudio = command.Parameters.Add("$audio", SqliteType.Real);
        var pMedia = command.Parameters.Add("$media", SqliteType.Text);
        var pMediaApp = command.Parameters.Add("$media_app", SqliteType.Text);
        var pMediaTitle = command.Parameters.Add("$media_title", SqliteType.Text);
        var pLocked = command.Parameters.Add("$locked", SqliteType.Integer);

        foreach (var s in samples)
        {
            pTs.Value = s.TsUtcMs;
            pHwnd.Value = s.Hwnd;
            pApp.Value = s.App;
            pTitle.Value = s.Title;
            pCategory.Value = s.Context.ToString();
            pC.Value = s.ContextValue;
            pIdle.Value = s.IdleSeconds;
            pKeys.Value = s.KeyCount;
            pMouse.Value = s.MouseCount;
            pHash.Value = unchecked((long)s.VisualHash);
            pDelta.Value = s.VisualDelta;
            pAudio.Value = s.AudioPeak;
            pMedia.Value = s.MediaState.ToString();
            pMediaApp.Value = (object?)s.MediaApp ?? DBNull.Value;
            pMediaTitle.Value = (object?)s.MediaTitle ?? DBNull.Value;
            pLocked.Value = s.Locked ? 1 : 0;

            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return samples.Count;
    }

    public IReadOnlyList<Sample> LoadSamples(long startUtcMs, long endUtcMs)
    {
        var rows = _connection.Query<SampleRow>(
            """
            SELECT ts_utc, hwnd, app, title, category, c_value, idle_s,
                   key_count, mouse_count, visual_hash, visual_delta, audio_peak,
                   media_state, media_app, media_title, locked
            FROM samples
            WHERE ts_utc >= @start AND ts_utc < @end
            ORDER BY ts_utc;
            """,
            new { start = startUtcMs, end = endUtcMs });

        var result = new List<Sample>();
        foreach (var r in rows)
        {
            result.Add(new Sample(
                r.ts_utc,
                r.hwnd,
                r.app ?? string.Empty,
                r.title ?? string.Empty,
                ParseEnum(r.category, ContextLevel.Neutral),
                r.c_value,
                AppClass.Other, // 读取时不信任历史分类，由 SegmentBuilder 按当前规则重算
                r.idle_s,
                r.key_count,
                r.mouse_count,
                unchecked((ulong)r.visual_hash),
                r.visual_delta,
                r.audio_peak,
                ParseEnum(r.media_state, MediaPlaybackState.None),
                r.media_app,
                r.media_title,
                r.locked != 0));
        }

        return result;
    }

    public long CountSamples()
        => _connection.ExecuteScalar<long>("SELECT COUNT(1) FROM samples;");

    // ---------- 规则 ----------

    /// <summary>用户自定义规则的 id 偏移，避免与内置规则（1–999）冲突。</summary>
    public const int UserRuleIdOffset = 100_000;

    public IReadOnlyList<Rule> LoadUserRules()
    {
        var rows = _connection.Query<RuleRow>(
            """
            SELECT id, match_type, pattern, category, app_class, priority, enabled, label
            FROM rules
            WHERE is_builtin = 0
            ORDER BY priority, id;
            """);

        var result = new List<Rule>(rows.Count());
        foreach (var row in rows)
        {
            result.Add(new Rule(
                UserRuleIdOffset + (int)row.id,
                ParseRuleMatchType(row.match_type),
                row.pattern ?? string.Empty,
                ParseContext(row.category),
                ParseAppClass(row.app_class),
                row.priority,
                row.enabled != 0,
                IsBuiltin: false,
                row.label));
        }

        return result;
    }

    public Rule InsertUserRule(Rule rule)
    {
        long id = _connection.ExecuteScalar<long>(
            """
            INSERT INTO rules (match_type, pattern, category, c_value, app_class, priority, enabled, is_builtin, label)
            VALUES (@matchType, @pattern, @category, @cValue, @appClass, @priority, @enabled, 0, @label);
            SELECT last_insert_rowid();
            """,
            new
            {
                matchType = RuleMatchTypeText(rule.MatchType),
                pattern = rule.Pattern,
                category = ContextText(rule.Context),
                cValue = rule.ContextValue,
                appClass = AppClassText(rule.AppClass),
                priority = rule.Priority,
                enabled = rule.Enabled ? 1 : 0,
                label = rule.Label
            });

        return rule with { Id = UserRuleIdOffset + (int)id };
    }

    public void UpdateUserRule(Rule rule)
    {
        _connection.Execute(
            """
            UPDATE rules
            SET match_type = @matchType, pattern = @pattern, category = @category,
                c_value = @cValue, app_class = @appClass, priority = @priority,
                enabled = @enabled, label = @label
            WHERE id = @id AND is_builtin = 0;
            """,
            new
            {
                id = rule.Id - UserRuleIdOffset,
                matchType = RuleMatchTypeText(rule.MatchType),
                pattern = rule.Pattern,
                category = ContextText(rule.Context),
                cValue = rule.ContextValue,
                appClass = AppClassText(rule.AppClass),
                priority = rule.Priority,
                enabled = rule.Enabled ? 1 : 0,
                label = rule.Label
            });
    }

    public void DeleteUserRule(int ruleId)
        => _connection.Execute(
            "DELETE FROM rules WHERE id = @id AND is_builtin = 0;",
            new { id = ruleId - UserRuleIdOffset });

    public IReadOnlySet<int> LoadDisabledBuiltinIds()
    {
        string? json = _connection.ExecuteScalar<string?>(
            "SELECT value FROM meta WHERE key = 'rules.disabled_builtin';");

        if (string.IsNullOrWhiteSpace(json))
        {
            return new HashSet<int>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<int[]>(json)?.ToHashSet() ?? new HashSet<int>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new HashSet<int>();
        }
    }

    public void SaveDisabledBuiltinIds(IEnumerable<int> ids)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(ids.Distinct().OrderBy(i => i).ToArray());
        _connection.Execute(
            """
            INSERT INTO meta(key, value) VALUES('rules.disabled_builtin', @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """,
            new { v = json });
    }

    private static string RuleMatchTypeText(RuleMatchType type) => type switch
    {
        RuleMatchType.Process => "process",
        RuleMatchType.TitleContains => "title_contains",
        _ => "title_regex"
    };

    private static RuleMatchType ParseRuleMatchType(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "title_contains" => RuleMatchType.TitleContains,
        "title_regex" => RuleMatchType.TitleRegex,
        _ => RuleMatchType.Process
    };

    private static string ContextText(ContextLevel level) => level switch
    {
        ContextLevel.Task => "task",
        ContextLevel.Neutral => "neutral",
        _ => "distraction"
    };

    private static ContextLevel ParseContext(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "task" => ContextLevel.Task,
        "distraction" => ContextLevel.Distraction,
        _ => ContextLevel.Neutral
    };

    private static string AppClassText(AppClass appClass) => appClass switch
    {
        AppClass.Reading => "reading",
        AppClass.Typing => "typing",
        AppClass.Media => "media",
        _ => "other"
    };

    private static AppClass ParseAppClass(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "reading" => AppClass.Reading,
        "typing" => AppClass.Typing,
        "media" => AppClass.Media,
        _ => AppClass.Other
    };

    internal sealed class RuleRow
    {
        public long id { get; set; }
        public string? match_type { get; set; }
        public string? pattern { get; set; }
        public string? category { get; set; }
        public string? app_class { get; set; }
        public int priority { get; set; }
        public long enabled { get; set; }
        public string? label { get; set; }
    }

    /// <summary>删除全部数据（FR-C4）。不可恢复，调用方必须先二次确认。</summary>
    public void DeleteAll()
    {
        _connection.Execute("DELETE FROM samples;");
        _connection.Execute("DELETE FROM segments;");
        _connection.Execute("DELETE FROM labels;");
        _connection.Execute("DELETE FROM daily;");
        _connection.Execute("VACUUM;");
    }

    public int PruneSamples(long olderThanUtcMs)
        => _connection.Execute(
            """
            DELETE FROM samples
            WHERE id IN (SELECT id FROM samples WHERE ts_utc < @cutoff LIMIT 50000);
            """,
            new { cutoff = olderThanUtcMs });

    public void Dispose() => _connection.Dispose();

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private const string InsertSampleSql =
        """
        INSERT INTO samples
            (ts_utc, hwnd, app, title, category, c_value, idle_s, key_count, mouse_count,
             visual_hash, visual_delta, audio_peak, media_state, media_app, media_title, locked)
        VALUES
            ($ts, $hwnd, $app, $title, $category, $c, $idle, $keys, $mouse,
             $hash, $delta, $audio, $media, $media_app, $media_title, $locked);
        """;

    /// <summary>查询行。app_class 不落库，读取时由规则重新推导。</summary>
    internal sealed class SampleRow
    {
        public long ts_utc { get; set; }
        public long hwnd { get; set; }
        public string? app { get; set; }
        public string? title { get; set; }
        public string? category { get; set; }
        public double c_value { get; set; }
        public int idle_s { get; set; }
        public int key_count { get; set; }
        public int mouse_count { get; set; }
        public long visual_hash { get; set; }
        public int visual_delta { get; set; }
        public double audio_peak { get; set; }
        public string? media_state { get; set; }
        public string? media_app { get; set; }
        public string? media_title { get; set; }
        public long locked { get; set; }
    }

    private const string Schema =
        """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS samples (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_utc       INTEGER NOT NULL,
            hwnd         INTEGER NOT NULL,
            app          TEXT    NOT NULL,
            title        TEXT    NOT NULL DEFAULT '',
            category     TEXT    NOT NULL DEFAULT 'unknown',
            c_value      REAL    NOT NULL DEFAULT 0.6,
            idle_s       INTEGER NOT NULL,
            key_count    INTEGER NOT NULL DEFAULT 0,
            mouse_count  INTEGER NOT NULL DEFAULT 0,
            visual_hash  INTEGER NOT NULL DEFAULT 0,
            visual_delta INTEGER NOT NULL DEFAULT 0,
            audio_peak   REAL    NOT NULL DEFAULT 0,
            media_state  TEXT    NOT NULL DEFAULT 'none',
            media_app    TEXT,
            media_title  TEXT,
            locked       INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts_utc);
        CREATE INDEX IF NOT EXISTS idx_samples_app_ts ON samples(app, ts_utc);

        CREATE TABLE IF NOT EXISTS segments (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            start_utc         INTEGER NOT NULL,
            end_utc           INTEGER NOT NULL,
            app               TEXT    NOT NULL,
            title             TEXT,
            category          TEXT    NOT NULL,
            app_class         TEXT    NOT NULL DEFAULT 'other',
            state_provisional TEXT    NOT NULL,
            state_final       TEXT,
            w                 REAL    NOT NULL,
            w_lo              REAL    NOT NULL,
            w_hi              REAL    NOT NULL,
            confidence        TEXT    NOT NULL DEFAULT 'medium',
            rule_hit          TEXT,
            features_json     TEXT,
            corrected_by      TEXT,
            intent_id         INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_segments_start ON segments(start_utc);

        CREATE TABLE IF NOT EXISTS labels (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            start_utc   INTEGER NOT NULL,
            end_utc     INTEGER NOT NULL,
            state       TEXT    NOT NULL,
            note        TEXT,
            created_utc INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_labels_start ON labels(start_utc);

        CREATE TABLE IF NOT EXISTS rules (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            match_type TEXT    NOT NULL,
            pattern    TEXT    NOT NULL,
            category   TEXT    NOT NULL,
            c_value    REAL    NOT NULL,
            app_class  TEXT    NOT NULL,
            priority   INTEGER NOT NULL DEFAULT 100,
            enabled    INTEGER NOT NULL DEFAULT 1,
            is_builtin INTEGER NOT NULL DEFAULT 0,
            label      TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_rules_priority ON rules(priority);

        CREATE TABLE IF NOT EXISTS daily (
            date_local     TEXT PRIMARY KEY,
            active_s       INTEGER NOT NULL DEFAULT 0,
            passive_s      INTEGER NOT NULL DEFAULT 0,
            silent_s       INTEGER NOT NULL DEFAULT 0,
            off_task_s     INTEGER NOT NULL DEFAULT 0,
            idle_s         INTEGER NOT NULL DEFAULT 0,
            locked_s       INTEGER NOT NULL DEFAULT 0,
            ambiguous_s    INTEGER NOT NULL DEFAULT 0,
            untracked_s    INTEGER NOT NULL DEFAULT 0,
            effective_lo_s INTEGER NOT NULL DEFAULT 0,
            effective_hi_s INTEGER NOT NULL DEFAULT 0,
            focus_blocks   INTEGER NOT NULL DEFAULT 0,
            app_switches   INTEGER NOT NULL DEFAULT 0,
            computed_utc   INTEGER NOT NULL DEFAULT 0
        );
        """;
}
