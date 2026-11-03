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
        MigratePlans();
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

    // ---------- 外部计时器对账 ----------

    /// <summary>保存一次导入。同一份文件（按内容哈希）只导入一次。</summary>
    public (long ImportId, int Inserted, bool Duplicate) SaveTimerImport(
        string source,
        string fileHash,
        IReadOnlyList<TimerClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        long? existing = _connection.ExecuteScalar<long?>(
            "SELECT id FROM timer_imports WHERE file_hash = @hash;",
            new { hash = fileHash });

        if (existing.HasValue)
        {
            return (existing.Value, 0, true);
        }

        using var transaction = _connection.BeginTransaction();

        long importId = _connection.ExecuteScalar<long>(
            """
            INSERT INTO timer_imports (source, imported_utc, file_hash, row_count)
            VALUES (@source, @imported, @hash, @rows);
            SELECT last_insert_rowid();
            """,
            new { source, imported = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = fileHash, rows = claims.Count },
            transaction);

        foreach (var claim in claims)
        {
            _connection.Execute(
                """
                INSERT INTO timer_claims (import_id, start_utc, end_utc, label, claimed_s)
                VALUES (@importId, @start, @end, @label, @seconds);
                """,
                new
                {
                    importId,
                    start = claim.StartUtcMs,
                    end = claim.EndUtcMs,
                    label = claim.Label,
                    seconds = claim.ClaimedSeconds
                },
                transaction);
        }

        transaction.Commit();
        return (importId, claims.Count, false);
    }

    public IReadOnlyList<TimerClaim> LoadTimerClaims(long startUtcMs, long endUtcMs)
    {
        var rows = _connection.Query<TimerClaimRow>(
            """
            SELECT start_utc, end_utc, label, claimed_s
            FROM timer_claims
            WHERE end_utc > @start AND start_utc < @end
            ORDER BY start_utc;
            """,
            new { start = startUtcMs, end = endUtcMs });

        return rows
            .Select(r => new TimerClaim(r.start_utc, r.end_utc, r.label, r.claimed_s))
            .ToList();
    }

    public int CountTimerClaims()
        => (int)_connection.ExecuteScalar<long>("SELECT COUNT(1) FROM timer_claims;");

    public int ClearTimerClaims()
    {
        int removed = _connection.Execute("DELETE FROM timer_claims;");
        _connection.Execute("DELETE FROM timer_imports;");
        return removed;
    }

    internal sealed class TimerClaimRow
    {
        public long start_utc { get; set; }
        public long end_utc { get; set; }
        public string? label { get; set; }
        public double claimed_s { get; set; }
    }

    // ---------- 意图声明 ----------

    public long CreateIntent(long startUtcMs, long plannedEndUtcMs, string label)
        => _connection.ExecuteScalar<long>(
            """
            INSERT INTO intents (start_utc, planned_end_utc, label, status, created_utc)
            VALUES (@start, @end, @label, 'running', @created);
            SELECT last_insert_rowid();
            """,
            new
            {
                start = startUtcMs,
                end = plannedEndUtcMs,
                label,
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

    public FocusIntent? LoadRunningIntent()
        => MapIntent(_connection.QueryFirstOrDefault<IntentRow>(
            """
            SELECT id, start_utc, planned_end_utc, label, status, adherence
            FROM intents WHERE status = 'running'
            ORDER BY start_utc DESC LIMIT 1;
            """));

    public FocusIntent? LoadLastIntent()
        => MapIntent(_connection.QueryFirstOrDefault<IntentRow>(
            """
            SELECT id, start_utc, planned_end_utc, label, status, adherence
            FROM intents ORDER BY start_utc DESC LIMIT 1;
            """));

    public IReadOnlyList<FocusIntent> LoadIntents(long startUtcMs, long endUtcMs)
        => _connection.Query<IntentRow>(
                """
                SELECT id, start_utc, planned_end_utc, label, status, adherence
                FROM intents WHERE start_utc >= @start AND start_utc < @end
                ORDER BY start_utc;
                """,
                new { start = startUtcMs, end = endUtcMs })
            .Select(MapIntent)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

    public void FinishIntent(int id, IntentStatus status, double adherence)
        => _connection.Execute(
            "UPDATE intents SET status = @status, adherence = @adherence WHERE id = @id;",
            new { id, status = status.ToString(), adherence });

    private static FocusIntent? MapIntent(IntentRow? row)
    {
        if (row is null)
        {
            return null;
        }

        var status = Enum.TryParse<IntentStatus>(row.status, ignoreCase: true, out var parsed)
            ? parsed
            : IntentStatus.Completed;

        return new FocusIntent(
            (int)row.id,
            row.start_utc,
            row.planned_end_utc,
            row.label ?? string.Empty,
            status,
            row.adherence);
    }

    internal sealed class IntentRow
    {
        public long id { get; set; }
        public long start_utc { get; set; }
        public long planned_end_utc { get; set; }
        public string? label { get; set; }
        public string? status { get; set; }
        public double? adherence { get; set; }
    }

    // ---------- 计划与节假日 ----------

    public long CountSamplesInRange(long startUtcMs, long endUtcMs)
        => _connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM samples WHERE ts_utc >= @start AND ts_utc < @end;",
            new { start = startUtcMs, end = endUtcMs });

    public IReadOnlyList<PlanRow> LoadPlans(DateOnly date)
        => _connection.Query<PlanRow>(
                """
                SELECT id, date_local, hour, title, done, start_min, end_min, location, url, remind, repeat
                FROM plans WHERE date_local = @d ORDER BY start_min, id;
                """,
                new { d = date.ToString("yyyy-MM-dd") })
            .ToList();

    public IReadOnlyList<PlanRow> LoadPlansInRange(DateOnly from, DateOnly to)
        => _connection.Query<PlanRow>(
                """
                SELECT id, date_local, hour, title, done, start_min, end_min, location, url, remind, repeat
                FROM plans WHERE date_local >= @a AND date_local <= @b
                ORDER BY date_local, hour, id;
                """,
                new { a = from.ToString("yyyy-MM-dd"), b = to.ToString("yyyy-MM-dd") })
            .ToList();

    public long AddPlan(DateOnly date, int hour, string title)
        => _connection.ExecuteScalar<long>(
            """
            INSERT INTO plans (date_local, hour, title, done, created_utc)
            VALUES (@d, @h, @t, 0, @now);
            SELECT last_insert_rowid();
            """,
            new
            {
                d = date.ToString("yyyy-MM-dd"),
                h = hour,
                t = title,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

    public long AddPlanRange(DateOnly date, int startMin, int endMin, string title)
        => _connection.ExecuteScalar<long>(
            """
            INSERT INTO plans (date_local, hour, title, done, created_utc, start_min, end_min)
            VALUES (@d, @h, @t, 0, @now, @s, @e);
            SELECT last_insert_rowid();
            """,
            new
            {
                d = date.ToString("yyyy-MM-dd"),
                h = startMin / 60,
                t = title,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                s = startMin,
                e = endMin
            });

    /// <summary>一次写入完整信息。地点/网址留空存空串，避免 NULL 让上层到处判空。</summary>
    /// <param name="repeat">周期规则标记（daily / weekday / weekend / weekly:1,3,5），空串表示不重复。</param>
    public long AddPlanFull(
        DateOnly date, int startMin, int endMin, string title, string? location, string? url, bool remind,
        string? repeat = null)
        => _connection.ExecuteScalar<long>(
            """
            INSERT INTO plans (date_local, hour, title, done, created_utc, start_min, end_min, location, url, remind, repeat)
            VALUES (@d, @h, @t, 0, @now, @s, @e, @loc, @url, @r, @rep);
            SELECT last_insert_rowid();
            """,
            new
            {
                d = date.ToString("yyyy-MM-dd"),
                h = startMin / 60,
                t = title,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                s = startMin,
                e = endMin,
                loc = location ?? string.Empty,
                url = url ?? string.Empty,
                r = remind ? 1 : 0,
                rep = repeat ?? string.Empty
            });

    public void UpdatePlanDetails(long id, string title, string? location, string? url, bool remind)
        => _connection.Execute(
            """
            UPDATE plans
            SET title = @t, location = @loc, url = @url, remind = @r
            WHERE id = @id;
            """,
            new { id, t = title, loc = location ?? string.Empty, url = url ?? string.Empty, r = remind ? 1 : 0 });

    public void UpdatePlanRange(long id, int startMin, int endMin)
        => _connection.Execute(
            "UPDATE plans SET start_min = @s, end_min = @e, hour = @h WHERE id = @id;",
            new { id, s = startMin, e = endMin, h = startMin / 60 });

    public void UpdatePlanTitle(long id, string title)
        => _connection.Execute("UPDATE plans SET title = @t WHERE id = @id;", new { id, t = title });

    public void SetPlanDone(long id, bool done)
        => _connection.Execute("UPDATE plans SET done = @d WHERE id = @id;", new { id, d = done ? 1 : 0 });

    public void DeletePlan(long id)
        => _connection.Execute("DELETE FROM plans WHERE id = @id;", new { id });

    public IReadOnlyDictionary<string, string> LoadHolidays(string region)
        => _connection.Query<HolidayRow>(
                "SELECT date_local, name FROM holidays WHERE region = @r;",
                new { r = region })
            .ToDictionary(h => h.date_local, h => h.name, StringComparer.Ordinal);

    public int CountHolidays(string region)
        => (int)_connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM holidays WHERE region = @r;", new { r = region });

    public int ReplaceHolidays(string region, IEnumerable<(string Date, string Name)> items)
    {
        var list = items.ToList();
        using var transaction = _connection.BeginTransaction();
        _connection.Execute("DELETE FROM holidays WHERE region = @r;", new { r = region }, transaction);

        foreach (var (date, name) in list)
        {
            _connection.Execute(
                "INSERT OR REPLACE INTO holidays (date_local, region, name) VALUES (@d, @r, @n);",
                new { d = date, r = region, n = name },
                transaction);
        }

        transaction.Commit();
        return list.Count;
    }

    public sealed class PlanRow
    {
        public long id { get; set; }
        public string? date_local { get; set; }
        public int hour { get; set; }
        public string? title { get; set; }
        public long done { get; set; }
        public int start_min { get; set; }
        public int end_min { get; set; }
        public string? location { get; set; }
        public string? url { get; set; }
        public long remind { get; set; }
        public string? repeat { get; set; }

        public bool Remind => remind != 0;
    }

    internal sealed class HolidayRow
    {
        public string? date_local { get; set; }
        public string? name { get; set; }
    }

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

    /// <summary>
    /// 老库补列。<c>CREATE TABLE IF NOT EXISTS</c> 对已存在的表无能为力，
    /// 所以每次加列都必须在这里补一条 <c>ALTER TABLE</c>，否则老库 SELECT 新列会直接抛
    /// <c>no such column</c>，启动即崩。
    /// </summary>
    private void MigratePlans()
    {
        var columns = _connection.Query<string>("SELECT name FROM pragma_table_info('plans');").ToList();

        // 每列只判一次，加完立刻记进 columns。
        // 用本地快照时要特别小心：同一列若被判断两次，第二次会照着过期快照再 ALTER 一遍，
        // 直接抛 "duplicate column name"（v0.5 就是这么崩的）。
        void Ensure(string name, string definition)
        {
            if (columns.Contains(name))
            {
                return;
            }

            _connection.Execute($"ALTER TABLE plans ADD COLUMN {definition};");
            columns.Add(name);
        }

        Ensure("start_min", "start_min INTEGER NOT NULL DEFAULT -1");
        Ensure("end_min", "end_min INTEGER NOT NULL DEFAULT -1");
        Ensure("location", "location TEXT NOT NULL DEFAULT ''");
        Ensure("url", "url TEXT NOT NULL DEFAULT ''");
        Ensure("remind", "remind INTEGER NOT NULL DEFAULT 1");
        Ensure("repeat", "repeat TEXT NOT NULL DEFAULT ''");

        _connection.Execute(
            "UPDATE plans SET start_min = hour * 60, end_min = (hour + 1) * 60 WHERE start_min < 0 OR end_min <= start_min;");
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

        CREATE TABLE IF NOT EXISTS timer_imports (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            source       TEXT    NOT NULL,
            imported_utc INTEGER NOT NULL,
            file_hash    TEXT    NOT NULL UNIQUE,
            row_count    INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS timer_claims (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            import_id INTEGER NOT NULL REFERENCES timer_imports(id) ON DELETE CASCADE,
            start_utc INTEGER NOT NULL,
            end_utc   INTEGER NOT NULL,
            label     TEXT,
            claimed_s REAL    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_claims_start ON timer_claims(start_utc);

        CREATE TABLE IF NOT EXISTS intents (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            start_utc       INTEGER NOT NULL,
            planned_end_utc INTEGER NOT NULL,
            label           TEXT    NOT NULL DEFAULT '',
            status          TEXT    NOT NULL DEFAULT 'running',
            adherence       REAL,
            created_utc     INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_intents_start ON intents(start_utc);

        CREATE TABLE IF NOT EXISTS plans (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            date_local  TEXT    NOT NULL,
            hour        INTEGER NOT NULL,
            title       TEXT    NOT NULL,
            location    TEXT    NOT NULL DEFAULT '',
            url         TEXT    NOT NULL DEFAULT '',
            remind      INTEGER NOT NULL DEFAULT 1,
            repeat      TEXT    NOT NULL DEFAULT '',
            done        INTEGER NOT NULL DEFAULT 0,
            created_utc INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_plans_date ON plans(date_local);

        CREATE TABLE IF NOT EXISTS holidays (
            date_local TEXT NOT NULL,
            region     TEXT NOT NULL DEFAULT 'HK',
            name       TEXT NOT NULL,
            PRIMARY KEY (date_local, region)
        );
        """;
}
