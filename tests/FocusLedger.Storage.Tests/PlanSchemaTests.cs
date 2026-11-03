using Microsoft.Data.Sqlite;

namespace FocusLedger.Storage.Tests;

/// <summary>
/// 计划表的迁移与读写。
/// 回归用例：只改建表语句、不写迁移时，老库启动会在 <c>LoadPlans</c> 抛
/// <c>SQLite Error 1: 'no such column: location'</c>，进程直接崩在 OnStartup。
/// </summary>
public sealed class PlanSchemaTests : IDisposable
{
    private readonly string _directory;

    public PlanSchemaTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "FocusLedger.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    private string NewDatabasePath() => Path.Combine(_directory, "data.db");

    /// <summary>最早期的 plans 表：没有 start_min/end_min，也没有地点/网址/提醒。</summary>
    private static void CreateLegacyDatabase(string path, string date, string title)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE plans (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date_local  TEXT    NOT NULL,
                hour        INTEGER NOT NULL,
                title       TEXT    NOT NULL,
                done        INTEGER NOT NULL DEFAULT 0,
                created_utc INTEGER NOT NULL
            );
            INSERT INTO plans (date_local, hour, title, done, created_utc)
            VALUES ($d, 9, $t, 0, 0);
            """;
        command.Parameters.AddWithValue("$d", date);
        command.Parameters.AddWithValue("$t", title);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void LegacyDatabase_GetsLocationUrlRemind_AndKeepsRows()
    {
        string path = NewDatabasePath();
        CreateLegacyDatabase(path, "2026-11-02", "旧计划");

        using var store = new FocusLedgerStore(path);
        store.Initialize();

        var plan = Assert.Single(store.LoadPlans(new DateOnly(2026, 11, 2)));

        Assert.Equal("旧计划", plan.title);
        Assert.Equal(string.Empty, plan.location);
        Assert.Equal(string.Empty, plan.url);
        Assert.Equal(string.Empty, plan.repeat);
        Assert.True(plan.Remind);

        // 同一轮迁移里，旧数据还要被补成「整点开始、一小时长」
        Assert.Equal(9 * 60, plan.start_min);
        Assert.Equal(10 * 60, plan.end_min);
    }

    [Fact]
    public void AddPlanFull_StoresRepeatToken()
    {
        using var store = new FocusLedgerStore(NewDatabasePath());
        store.Initialize();

        var date = new DateOnly(2026, 11, 3);
        store.AddPlanFull(date, 9 * 60, 10 * 60, "晨跑", null, null, true, "weekly:1,3,5");
        store.AddPlanFull(date.AddDays(1), 20 * 60, 21 * 60, "读书", null, null, false);

        var weekly = Assert.Single(store.LoadPlans(date));
        Assert.Equal("weekly:1,3,5", weekly.repeat);

        var single = Assert.Single(store.LoadPlans(date.AddDays(1)));
        Assert.Equal(string.Empty, single.repeat);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        string path = NewDatabasePath();
        CreateLegacyDatabase(path, "2026-11-02", "旧计划");

        using (var first = new FocusLedgerStore(path))
        {
            first.Initialize();
        }

        using var second = new FocusLedgerStore(path);
        second.Initialize();          // 第二次启动不得再抛「duplicate column name」
        second.Initialize();

        Assert.Single(second.LoadPlans(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void AddPlanFull_RoundTripsAllFields()
    {
        using var store = new FocusLedgerStore(NewDatabasePath());
        store.Initialize();

        var date = new DateOnly(2026, 11, 3);
        long id = store.AddPlanFull(date, 8 * 60 + 30, 10 * 60, "写论文", "图书馆 3F", "https://example.com/x", false);

        var plan = Assert.Single(store.LoadPlans(date));

        Assert.Equal(id, plan.id);
        Assert.Equal("写论文", plan.title);
        Assert.Equal("图书馆 3F", plan.location);
        Assert.Equal("https://example.com/x", plan.url);
        Assert.False(plan.Remind);
        Assert.Equal(8 * 60 + 30, plan.start_min);
        Assert.Equal(10 * 60, plan.end_min);
        Assert.Equal(8, plan.hour);
    }

    [Fact]
    public void AddPlanFull_WithEmptyDetails_StoresEmptyStrings()
    {
        using var store = new FocusLedgerStore(NewDatabasePath());
        store.Initialize();

        var date = new DateOnly(2026, 11, 4);
        store.AddPlanFull(date, 13 * 60, 14 * 60, "开会", null, null, true);

        var plan = Assert.Single(store.LoadPlans(date));
        Assert.Equal(string.Empty, plan.location);
        Assert.Equal(string.Empty, plan.url);
        Assert.True(plan.Remind);
    }

    [Fact]
    public void UpdatePlanDetails_ChangesOnlyDetailColumns()
    {
        using var store = new FocusLedgerStore(NewDatabasePath());
        store.Initialize();

        var date = new DateOnly(2026, 11, 5);
        long id = store.AddPlanFull(date, 9 * 60, 11 * 60, "初稿", "家", "https://a.example", true);

        store.UpdatePlanDetails(id, "二稿", "实验室", "https://b.example", false);

        var plan = Assert.Single(store.LoadPlans(date));
        Assert.Equal("二稿", plan.title);
        Assert.Equal("实验室", plan.location);
        Assert.Equal("https://b.example", plan.url);
        Assert.False(plan.Remind);

        // 时间不该被这次更新碰到
        Assert.Equal(9 * 60, plan.start_min);
        Assert.Equal(11 * 60, plan.end_min);
    }

    [Fact]
    public void LoadPlansInRange_ReturnsDetailsForWeekView()
    {
        using var store = new FocusLedgerStore(NewDatabasePath());
        store.Initialize();

        var start = new DateOnly(2026, 11, 2);
        store.AddPlanFull(start, 9 * 60, 10 * 60, "周一", "A", "https://a.example", true);
        store.AddPlanFull(start.AddDays(6), 15 * 60, 16 * 60, "周日", "B", null, false);

        var week = store.LoadPlansInRange(start, start.AddDays(6));

        Assert.Equal(2, week.Count);
        Assert.Equal("A", week[0].location);
        Assert.False(week[1].Remind);
    }
}
