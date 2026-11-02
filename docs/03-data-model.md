# 03 · 数据模型

SQLite 单文件，WAL 模式。时间统一存 **UTC 毫秒**，展示时按本地时区转换。

---

## 1. 完整 DDL

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;

-- 元信息：schema 版本、安装 ID、设置项
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- 原始样本（tick 级，5 秒一条）
CREATE TABLE IF NOT EXISTS samples (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc      INTEGER NOT NULL,
    hwnd        INTEGER NOT NULL,
    app         TEXT    NOT NULL,
    title       TEXT    NOT NULL DEFAULT '',
    category    TEXT    NOT NULL DEFAULT 'unknown',
    c_value     REAL    NOT NULL DEFAULT 0.6,
    idle_s      INTEGER NOT NULL,
    key_count   INTEGER NOT NULL DEFAULT 0,
    mouse_count INTEGER NOT NULL DEFAULT 0,
    visual_hash INTEGER NOT NULL DEFAULT 0,
    visual_delta INTEGER NOT NULL DEFAULT 0,
    audio_peak  REAL    NOT NULL DEFAULT 0,
    media_state TEXT    NOT NULL DEFAULT 'none',
    media_app   TEXT,
    media_title TEXT,
    locked      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts_utc);
CREATE INDEX IF NOT EXISTS idx_samples_app_ts ON samples(app, ts_utc);

-- 判定段（60 秒一条，或按应用切换切分）
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
    confidence        TEXT    NOT NULL DEFAULT 'medium',  -- high|medium|low
    rule_hit          TEXT,          -- 触发的规则标识，用于解释
    features_json     TEXT,          -- 特征快照，用于下钻
    corrected_by      TEXT,          -- NULL | 'return' | 'user'
    intent_id         INTEGER REFERENCES intents(id) ON DELETE SET NULL,
    UNIQUE(start_utc, app)
);
CREATE INDEX IF NOT EXISTS idx_segments_start ON segments(start_utc);

-- 意图声明
CREATE TABLE IF NOT EXISTS intents (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    start_utc         INTEGER NOT NULL,
    planned_end_utc   INTEGER NOT NULL,
    label             TEXT    NOT NULL,
    expected_category TEXT,
    status            TEXT    NOT NULL DEFAULT 'running', -- running|completed|abandoned
    adherence         REAL,                               -- 0..1，结束时计算
    created_utc       INTEGER NOT NULL
);

-- 用户改判（优先级高于模型）
CREATE TABLE IF NOT EXISTS labels (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    start_utc   INTEGER NOT NULL,
    end_utc     INTEGER NOT NULL,
    state       TEXT    NOT NULL,
    note        TEXT,
    created_utc INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_labels_start ON labels(start_utc);

-- 分类规则
CREATE TABLE IF NOT EXISTS rules (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    match_type TEXT    NOT NULL,       -- process | title_contains | title_regex
    pattern    TEXT    NOT NULL,
    category   TEXT    NOT NULL,       -- task | neutral | distraction
    c_value    REAL    NOT NULL,
    app_class  TEXT    NOT NULL,       -- reading | typing | media | other
    priority   INTEGER NOT NULL DEFAULT 100,
    enabled    INTEGER NOT NULL DEFAULT 1,
    is_builtin INTEGER NOT NULL DEFAULT 0,
    label      TEXT
);
CREATE INDEX IF NOT EXISTS idx_rules_priority ON rules(priority);

-- 日聚合
CREATE TABLE IF NOT EXISTS daily (
    date_local    TEXT PRIMARY KEY,    -- YYYY-MM-DD
    active_s      INTEGER NOT NULL DEFAULT 0,
    passive_s     INTEGER NOT NULL DEFAULT 0,
    silent_s      INTEGER NOT NULL DEFAULT 0,
    off_task_s    INTEGER NOT NULL DEFAULT 0,
    idle_s        INTEGER NOT NULL DEFAULT 0,
    locked_s      INTEGER NOT NULL DEFAULT 0,
    ambiguous_s   INTEGER NOT NULL DEFAULT 0,
    untracked_s   INTEGER NOT NULL DEFAULT 0,
    effective_lo_s INTEGER NOT NULL DEFAULT 0,
    effective_hi_s INTEGER NOT NULL DEFAULT 0,
    focus_blocks  INTEGER NOT NULL DEFAULT 0,
    app_switches  INTEGER NOT NULL DEFAULT 0,
    computed_utc  INTEGER NOT NULL
);

-- 外部计时器导入
CREATE TABLE IF NOT EXISTS timer_imports (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    source       TEXT    NOT NULL,      -- pomodoro | forest | toggl | csv
    imported_utc INTEGER NOT NULL,
    file_hash    TEXT    NOT NULL UNIQUE,
    row_count    INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS timer_claims (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    import_id  INTEGER NOT NULL REFERENCES timer_imports(id) ON DELETE CASCADE,
    start_utc  INTEGER NOT NULL,
    end_utc    INTEGER NOT NULL,
    label      TEXT,
    claimed_s  INTEGER NOT NULL
);

-- 采集健康度
CREATE TABLE IF NOT EXISTS health (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc      INTEGER NOT NULL,
    ticks       INTEGER NOT NULL,
    dropped     INTEGER NOT NULL,
    errors      INTEGER NOT NULL,
    degraded    TEXT
);

-- 计划：用户自己排的日程，只用于展示与提醒，不参与专注判定
CREATE TABLE IF NOT EXISTS plans (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    date_local  TEXT    NOT NULL,
    hour        INTEGER NOT NULL,              -- 冗余字段：start_min / 60，兼容最早按小时排版的版本
    title       TEXT    NOT NULL,
    location    TEXT    NOT NULL DEFAULT '',   -- 地点，纯文本
    url         TEXT    NOT NULL DEFAULT '',   -- 关联链接
    remind      INTEGER NOT NULL DEFAULT 1,    -- 0/1，是否到时提醒
    done        INTEGER NOT NULL DEFAULT 0,
    created_utc INTEGER NOT NULL,
    start_min   INTEGER NOT NULL DEFAULT -1,   -- 距零点分钟数；-1 表示待迁移，由 hour 回填
    end_min     INTEGER NOT NULL DEFAULT -1
);
CREATE INDEX IF NOT EXISTS idx_plans_date ON plans(date_local);

-- 节假日（含法定假期与调休），按地区存储
CREATE TABLE IF NOT EXISTS holidays (
    date_local TEXT NOT NULL,
    region     TEXT NOT NULL DEFAULT 'HK',
    name       TEXT NOT NULL,
    PRIMARY KEY (date_local, region)
);
```

---

## 2. 保留策略

| 数据 | 默认保留 | 可配 |
|---|---|---|
| `samples` | 14 天 | 7 / 14 / 30 / 永不 |
| `segments` | 1 年 | 1 年 / 永久 |
| `daily` | 永久 | — |
| `labels` | 永久 | — |
| `health` | 30 天 | — |

清理任务每日 03:00 本地时间执行，单次删除上限 50,000 行，避免长事务卡住 UI。
**用户已被 `labels` 覆盖的区间，其对应 `samples` 不得被清理**（保留改判依据）。

---

## 3. 写入性能

- 批量写入：每 60 s 一次性写入 12 条 `samples` + 1 条 `segments`，单事务。
- 使用预编译语句，避免每次解析 SQL。
- 预计日增约 1.7 万条样本，单个事务写入耗时应 < 20 ms。

---

## 4. 时间处理规则

1. 所有 `*_utc` 字段为 **Unix 毫秒**。
2. 采样间隔使用 `Stopwatch`（单调时钟）；禁止用 `DateTime.Now` 做差值。
3. 系统时间被回拨时，写一条 `health` 记录 `clock_skew`，并以单调时钟续算。
4. `date_local` 由 `start_utc` 转换到本地时区得到；跨零点的段按本地日期切分。
5. 夏令时/时区变更：以段的 `start_utc` 所在时区为准，不追溯重算历史。
6. 任何情况下不得出现负时长；`end_utc < start_utc` 视为脏数据，丢弃并计数。

---

## 5. 导出格式

### 5.1 CSV

`segments.csv`：

```
start_utc,end_utc,start_local,end_local,app,title,category,state,w,confidence
```

`daily.csv`：

```
date,active_h,passive_h,silent_h,off_task_h,idle_h,ambiguous_h,effective_lo_h,effective_hi_h,focus_blocks
```

导出时默认对 `title` 脱敏：只保留前 20 字符，并把 `@`、连续 6 位以上数字替换为 `***`。

### 5.2 JSON

```json
{
  "schema_version": 1,
  "exported_utc": 1730000000000,
  "timezone": "Asia/Shanghai",
  "days": [ { "date": "2026-11-01", "effective_lo_s": 13680, "effective_hi_s": 16560 } ],
  "segments": [ { "start": 0, "end": 0, "app": "", "state": "", "w": 0 } ]
}
```

---

## 6. 迁移

- `meta.schema_version` 为整数，从 1 开始。
- 每次升级：备份 `data.db` → 事务内执行迁移脚本 → 写入新版本号。
- 迁移失败：回滚事务，保留备份，UI 提示"数据库升级失败，已回退"，并禁用写入避免二次损坏。

### 6.1 加列的硬性规则（v0.4 起）

`CREATE TABLE IF NOT EXISTS` 对**已存在的表不生效**。新增字段必须同时改两处：

1. `Schema` 里的建表语句 —— 管新装用户；
2. `MigratePlans()` 等迁移方法里的条件 `ALTER TABLE` —— 管老库。

只改建表语句的后果：老库 `SELECT` 新列时抛 `no such column`，而且发生在启动路径上，
表现为"双击图标没反应"。v0.4 的 `plans.location/url/remind` 就踩过这个坑，见 ADR-012。

迁移方法必须**可重复执行**（先查 `pragma_table_info` 再决定是否 `ALTER`），
因为每次启动都会跑一遍。

---

## 7. 内置规则包（初始版本，需用真实数据迭代）

| 匹配 | 类别 | C | app_class |
|---|---|---|---|
| `code.exe` `devenv.exe` `pycharm64.exe` `rstudio.exe` `idea64.exe` `sublime_text.exe` `notepad++.exe` `cursor.exe` | task | 1.0 | typing |
| `winword.exe` `excel.exe` `powerpnt.exe` `wps.exe` | task | 1.0 | typing |
| `zotero.exe` `acrobat.exe` `acrord32.exe` `sumatrapdf.exe` `foxitreader.exe` `mupdf.exe` | task | 1.0 | reading |
| `windowsterminal.exe` `powershell.exe` `cmd.exe` `putty.exe` | task | 1.0 | typing |
| `stata.exe` `stata-mp.exe` `spss.exe` | task | 1.0 | typing |
| 标题匹配 `Overleaf|LaTeX|arXiv|Google Scholar|PubMed|Web of Science` | task | 1.0 | reading |
| `chrome.exe` `msedge.exe` `firefox.exe`（未命中其他规则） | neutral | 0.6 | reading |
| 标题匹配 `哔哩哔哩|bilibili|YouTube|Netflix|爱奇艺|腾讯视频|Twitch` | distraction | 0.0 | media |
| 标题匹配 `微博|知乎|小红书|抖音|Reddit|Twitter|X\.com` | distraction | 0.0 | other |
| `wechat.exe` `qq.exe` `telegram.exe` `discord.exe` `slack.exe` `feishu.exe` | distraction | 0.0 | other |
| `steam.exe` `vlc.exe` `potplayermini64.exe` `mpc-hc64.exe` | distraction | 0.0 | media |
| `explorer.exe` `searchhost.exe` | neutral | 0.6 | other |
| 其他 | unknown | 0.6 | other |

> 规则顺序即优先级：先匹配 `task`，再匹配 `distraction`，最后落到 `neutral` 默认。
> 浏览器是最难的一类：同一个 Chrome 既可能是写论文也可能是刷视频，**必须靠标题规则 + 意图声明**区分。
> 这一块需要 M1 采集到真实数据后持续迭代，是产品的核心资产。
