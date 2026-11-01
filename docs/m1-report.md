# M1 完成报告

**日期**：2026-11-01
**状态**：✅ 交付完成，等待验收
**范围**：判定引擎 + Windows 采集器 + SQLite 存储 + 命令行日报

---

## 1. 交付内容

### 1.1 工程结构

```
FocusLedger.sln
src/
  FocusLedger.Core/              判定引擎（纯逻辑，零系统依赖）
    Models.cs                    Sample / WindowFeatures / Segment / 七状态枚举
    InferenceParams.cs           全部阈值集中管理
    RuleEngine.cs                规则匹配 + 内置规则包（约 100 条）
    FeatureAggregator.cs         60 秒窗口 → 特征向量
    StateMachine.cs              七状态判定（顺序即优先级）
    SegmentPostProcessor.cs      第二遍：返回修正 + 合并
    SegmentBuilder.cs            管线入口（样本流 → 判定段）
    DailyAggregator.cs           判定段 → 日报
  FocusLedger.Collector.Windows/ 采集器
    Native.cs                    Win32 P/Invoke + 前景窗口探针
    InputCounter.cs              低级键鼠钩子（仅计数）
    VisualHasher.cs              9×8 灰度 dHash
    MediaWatcher.cs              WinRT SMTC 媒体状态
    CollectorService.cs          采集循环
  FocusLedger.Storage/           SQLite 持久层（Dapper）
  FocusLedger.Cli/               命令行入口
tests/
  FocusLedger.Core.Tests/        24 个测试
```

### 1.2 需求覆盖

| 需求 | 状态 | 说明 |
|---|---|---|
| FR-A2 空闲采集 | ✅ | `GetLastInputInfo`，1 秒精度 |
| FR-A3 键鼠计数 | ✅ | 低级钩子，回调内只做 `Interlocked.Increment` |
| FR-A4 画面变化 | ✅ | dHash + 汉明距离，已处理最小化/越界窗口 |
| FR-A5 媒体播放 | ✅ | SMTC，实测可用 |
| FR-A7 锁屏 | ✅ | `OpenInputDesktop` 轮询 |
| FR-B1 分类规则 | ✅ | 约 100 条内置规则，浏览器按标题区分 |
| FR-B2 特征聚合 | ✅ | 见 `FeatureAggregator` |
| FR-B3 状态机 | ✅ | 8 个验收场景全过 |
| FR-B4 两遍制 | ✅ | `Build(finalize: true/false)` |
| FR-B5 返回修正 | ✅ | S5 通过，掐头去尾各 60 秒 |
| FR-C1 存储 | ✅ | WAL 模式，批量写入 |
| FR-C2 保留策略 | ✅ | `focusledger prune --days N` |
| FR-C3 导出 | ⏳ | M3（CSV/JSON 导出） |
| FR-A1 窗口事件驱动 | ⚠️ 改轮询 | 见 ADR-011 |
| FR-A6 音频峰值 | ❌ 未做 | 见 §5 |

---

## 2. 测试结果

```
dotnet test
已通过! - 失败: 0，通过: 24，已跳过: 0，总计: 24，持续时间: 19 ms
```

8 个验收场景逐条对应：

| 编号 | 测试方法 | 结果 |
|---|---|---|
| S1 | `S1_TimerRunningButUserAway_IsIdleAway` | ✅ 权重 0 |
| S2 | `S2_WatchingLectureWithoutInput_IsPassiveFocus` | ✅ 权重 0.85 |
| S3 | `S3_ReadingPdfWithoutScrolling_IsSilentFocusAtReadingWeight` | ✅ 权重 0.70 |
| S4 | `S4_SwitchingToWechat_IsOffTask` | ✅ 权重 0 |
| S5 | `S5_ShortGapThenReturn_IsReclassifiedAsSilentFocus` | ✅ 日终修正为 0.40 |
| S6 | `S6_WatchingMovieInVlc_IsOffTask` | ✅ 权重 0 |
| S7 | `S7_LockedScreen_ProducesNoTime` | ✅ 无时长 |
| S8 | `S8_EdgeInputs_DoNotThrow` | ✅ 空输入/单样本/重复时间戳 |

构建：5 个项目，**0 警告 0 错误**（Core 开启了 `TreatWarningsAsErrors`）。

---

## 3. 实机验证

### 3.1 探针

```
FocusLedger · 信号探针（不写库）
键盘钩子: 已安装   媒体信号: 可用

  时间      应用                    空闲   变化  媒体        标题
  14:52:11  msedge.exe                0s     0  None        Vibe Coding工作站解析 - DeepSeek 和…
  14:52:16  msedge.exe                0s     5  None        Vibe Coding工作站解析 - DeepSeek 和…
```

### 3.2 采集 3 分钟

36 条样本，全程 `msedge.exe`，空闲秒数从 0 增长到 89（用户中途离开）。

### 3.3 报告

```
2026-11-01   有效专注 2m [1m–2m]

  深度 2m  |  被动 0s  |  静默 1m
  分心 0s  |  离席 0s  |  锁屏 0s  |  存疑 0s

  深度工作块 0 次，最长 0 分钟
  应用切换 0 次
  样本 36 条，覆盖 3m
```

**判定解释**：浏览器的上下文系数是 0.6（中性），所以有输入的 2 分钟按
`ActiveFocus w=0.60` 计；最后 1 分钟无输入、画面静止，按 `SilentFocus w=0.70` 计。
有效时长 = 121s×0.6 + 61s×0.7 ≈ 115s ≈ 2 分钟，区间 [1m–2m] 与权重带一致。

**这组数据恰好说明了产品的主张**：同样这 3 分钟，番茄钟会记 3 分钟"学习"，
而 FocusLedger 给出的是 2 分钟、并且明说置信区间是 1–2 分钟。

---

## 4. 怎么用

```powershell
# 实时看信号（不写库）
.\tools\dev.ps1        # 或直接用 exe

# 采集（默认写 %LOCALAPPDATA%\FocusLedger\data.db）
focusledger capture --minutes 60

# 出报告
focusledger report
focusledger report --date 2026-11-01
focusledger report --verbose     # 打印每一段的判定与权重

# 清理 14 天前的原始样本
focusledger prune --days 14
```

---

## 5. 已知缺口（不隐藏）

| 缺口 | 影响 | 计划 |
|---|---|---|
| **音频峰值未采集** | 纯音频内容（播客、无画面的讲座）无法用"有声音"这一路证据，只能落到静默专注 0.70，而非被动专注 0.85 | M1.5：接 `IAudioMeterInformation` |
| **前景窗口按 tick 取值** | 5 秒内的快速切换不可见（ADR-011），已接受 | M2 若需要秒级精度再加事件流 |
| **segments / daily 表未使用** | 报告每次从 samples 重算。好处是永远与最新规则一致，坏处是数据量大后变慢（当前 1.7 万样本/天，重算 < 100ms） | M2 做当天缓存 |
| **规则包未校准** | 内置规则是凭经验写的，浏览器尤其难分。需要真实使用 1–2 周后按误判调整 | 持续 |
| **视觉归一化分母 12 仍是估计值** | 见 m0-findings §3.2 | 需要真实 delta 分布数据 |
| **无导出功能** | FR-C3 延后 | M3 |

---

## 6. 下一步（M2）

托盘常驻 + 今日时间轴 + 日报界面 + 规则编辑 + 排除列表。

M2 的验收标准是"你愿意每天打开它"，所以视觉与交互的权重高于功能数量。
如果 WPF 的自绘时间轴达不到要求，按 `04-architecture.md` §7 切换到 WebView2 + React，
后端一行不改。

---

## 7. 待你确认

1. 报告里"深度 2m / 静默 1m"这种分类，和你的直觉是否一致？
2. 浏览器的中性权重 0.6 是否偏低？如果你认为自己 80% 的浏览器时间确实是工作，
   可以把 `ContextLevel.Neutral` 的系数从 0.6 调到 0.8——这会显著改变日报数字。
3. 是否需要我先把「音频峰值」补上再进 M2？
