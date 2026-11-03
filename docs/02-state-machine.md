# 02 · 状态判定模型

> **⚠️ 本文档描述的是 v0.1 的七状态模型，已被 v0.2 的四状态模型取代。**
> 现行实现见 `src/FocusLedger.Core/StateMachine.cs`，变更说明见 [CHANGELOG.md](CHANGELOG.md)。
> 本文保留作为设计过程的记录，不再作为实现依据。

本文定义 FocusLedger 的核心算法。实现必须逐条对应，并有单元测试覆盖。

---

## 1. 采样与窗口

- **采样周期** `TICK = 5 s`
- **判定窗口** `WINDOW = 60 s`（12 个 tick）
- 每个 tick 采集一个原始样本；每个窗口聚合为一个特征向量，产生一个 `segment`

### 1.1 原始样本（tick 级）

| 字段 | 类型 | 说明 |
|---|---|---|
| `hwnd` | int64 | 前景窗口句柄 |
| `app` | string | 进程名（不含路径，如 `chrome.exe`） |
| `title` | string | 窗口标题 |
| `idle_s` | int | 距上次键鼠输入的秒数（`GetLastInputInfo`） |
| `keys` | int | 本 tick 内按键事件次数 |
| `mouse` | int | 本 tick 内鼠标事件次数 |
| `visual_hash` | uint64 | 前景窗口 9×8 灰度 dHash |
| `visual_delta` | int | 与上一 tick 的汉明距离（0–64） |
| `audio_peak` | float | 0–1 |
| `media_state` | enum | none / playing / paused |
| `media_app` | string | 媒体来源应用 |
| `locked` | bool | 会话是否锁定 |
| `ts_utc` | int64 | 单调递增时间戳（ms） |

---

## 2. 窗口级特征（60 s 聚合）

| 符号 | 计算 | 含义 |
|---|---|---|
| `A` | `clamp01( Σkeys / 20 + Σmouse / 10 )` | 输入活跃度 |
| `V` | `clamp01( mean(visual_delta) / 12 )` | 画面变化强度（汉明距离 12 ≈ 明显变化） |
| `S` | `max(audio_peak)` | 是否有声音 |
| `P` | 窗口内 `media_state == playing` 占比 ≥ 50% | 是否在播放媒体 |
| `C` | 规则引擎给出：1.0 任务 / 0.6 中性 / 0.0 分心 | 上下文匹配度 |
| `idle` | 窗口内最大 `idle_s` | 离席程度 |
| `appClass` | `reading` / `typing` / `media` / `other` | 应用类型，用于选空闲阈值 |

> `C` 取窗口内占比最高的类别的值；若窗口内发生应用切换，按时间加权平均后取整到 {1.0, 0.6, 0.0}。

---

## 3. 决策顺序（严格执行，顺序即优先级）

每个 60 s 窗口按下列顺序判定，命中即停：

```
 1. locked == true                       → LOCKED            （不计入任何时长）
 2. C == 0.0 and (A > 0.05 or P)         → OFF_TASK          w = 0
 3. P and C >= 0.6 and (S > 0.02 or V > 0.25)
                                         → PASSIVE_FOCUS     w = 0.85
 4. idle > IDLE_MAX and not P            → IDLE_AWAY         w = 0
 5. A > 0.10 and C == 1.0                → ACTIVE_FOCUS      w = 1.00
 6. A > 0.10 and C == 0.6                → ACTIVE_FOCUS      w = 0.60
 7. V > 0.25 and C >= 0.6                → PASSIVE_FOCUS     w = 0.70
 8. C >= 0.6                             → SILENT_FOCUS      w = 见 §3.2
 9. otherwise                            → AMBIGUOUS         w = 0.50
```

### 3.1 空闲阈值 `IDLE_MAX`

| `appClass` | `IDLE_MAX` | 例 |
|---|---|---|
| `reading` | 600 s | PDF 阅读器、Zotero、浏览器文章页 |
| `typing` | 300 s | 编辑器、终端、Office |
| `media` | 300 s | 播放器（通常走规则 3，不会到这里） |
| `other` | 300 s | 默认 |

### 3.2 `SILENT_FOCUS` 权重带

| `appClass` | `idle` 区间 | 权重 | 置信 |
|---|---|---|---|
| `reading` | 0 – 600 s | 0.70 | 低 |
| `typing` | 0 – 180 s | 0.70 | 中 |
| `typing` | 180 – 300 s | 0.50 | 低 |
| 其他 | 0 – `IDLE_MAX` | 0.50 | 低 |

> 设计理由：读文献时长时间不滚动是正常行为，不应与"离开"同罚；
> 打字类应用 3 分钟无输入则更可能是真的停手了，权重递减。

---

## 4. 两遍制：provisional 与 final

**问题**：窗口产生的瞬间，无法区分"人走了 6 分钟"和"人在思考 6 分钟"。
**解法**：不猜，等证据。

- **第一遍（实时，provisional）**：按 §3 直接判定，用于时间轴实时显示。
- **第二遍（日终或每小时增量，final）**：对相邻段做后处理，规则如下。

### 4.1 返回修正（Return Reclassification）

对每个 `idle_away` 段 `G = [t1, t2]`：

```
若  duration(G) <= 600 s
且  G 前后的前景应用相同（app 相同，或同属 reading 类）
且  G 结束后 60 s 内 A > 0.1（确实回来了并开始操作）
则  把 G 的中段（去掉首尾各 60 s）重判为 SILENT_FOCUS，权重 0.40，标记 corrected_by=return
否则  保持 IDLE_AWAY
```

效果：S5（思考 6 分钟又回来继续）会被认作静默专注；S1（离开 30 分钟）保持离席。

### 4.2 合并与碎片化

- 相邻同类段间隔 < 60 s 时合并，避免时间轴出现锯齿。
- 连续 `ACTIVE_FOCUS` ≥ 25 min 记为一次 **深度工作块（focus_block）**。

### 4.3 缺口

采集中断（休眠、崩溃、关机）产生的空档记为 `UNKNOWN_GAP`，不进入任何时长统计，
但在时间轴上以灰色虚线显示，且日报注明"有 X 分钟数据缺失"。

---

## 5. 置信区间

> **已废弃（v0.10，见 ADR-013）**：日报不再输出 `[w_lo, w_hi]` 区间。
> 每个 segment 仍保留 `w_lo / w_hi` 作为判定置信度的内部字段（表见下），
> 但不再把逐段权重聚合成一个对外数字——那个数字与「有效专注按原始时长计入」
> 的口径互相矛盾，而且区间宽度对用户没有可操作性。

每个状态给出 `[w_lo, w_hi]`：

| 状态 | `w` | `w_lo` | `w_hi` |
|---|---|---|---|
| ACTIVE_FOCUS (C=1.0) | 1.00 | 0.95 | 1.00 |
| ACTIVE_FOCUS (C=0.6) | 0.60 | 0.45 | 0.75 |
| PASSIVE_FOCUS (媒体) | 0.85 | 0.75 | 0.95 |
| PASSIVE_FOCUS (画面) | 0.70 | 0.55 | 0.85 |
| SILENT_FOCUS | 0.70 / 0.50 / 0.40 | w−0.20 | w+0.15 |
| AMBIGUOUS | 0.50 | 0.30 | 0.70 |
| OFF_TASK / IDLE_AWAY / LOCKED | 0 | 0 | 0 |

**日报计算**（现行口径）：

```
有效专注 = Σ (深度段分钟 + 静默段分钟)     ← 按原始时长，不乘 w
```

展示：`有效专注 4.2 h`——只给一个数，但这个数可以点开看到它由哪几段时间、哪条规则得来。
原「`4.2 h [3.8 – 4.6]` + 区间过宽时提示日终复核」的做法已按 ADR-013 取消。

---

## 6. 验收场景矩阵（必须全部通过）

| # | 场景 | 关键特征 | 期望状态 | 期望权重 |
|---|---|---|---|---|
| S1 | 番茄钟在跑，人离开 30 分钟，屏幕静止 | `idle=1800, P=false, C=1.0` | `idle_away` | 0 |
| S2 | 看 45 分钟网课视频，全程无键鼠 | `P=true, S>0.02, C=0.6, idle=1200` | `passive_focus` | 0.85 |
| S3 | 读 PDF 20 分钟，只滚动 3 次 | `appClass=reading, idle≈240, C=1.0` | `silent_focus` | 0.70 |
| S4 | 前台 Word，切到微信聊 8 分钟 | `C=0.0, A>0.1` | `off_task` | 0 |
| S5 | 前台 Word，低头思考 6 分钟无输入，随后回来继续 | `idle=360, C=1.0`, 之后同应用恢复输入 | provisional: `idle_away`；final: `silent_focus` | 0.40 |
| S6 | 深夜用 VLC 看电影 | `C=0.0, P=true` | `off_task` | 0 |
| S7 | 锁屏 2 小时、合盖睡眠过夜 | `locked=true` / 无样本 | `locked` / `unknown_gap` | 不计入 |
| S8 | 远程桌面、多屏、HDR、DPI 缩放、DRM 黑屏 | 见 §7 | 不崩溃、不误判 | — |

### 6.1 容易被实现错的三个点

1. **S2 与 S1 的次序**：媒体播放必须先于空闲判断。否则看网课会被判成离席。
2. **S6 与 S2 的分野**：唯一区别是 `C`。VLC 默认分心类，浏览器和课程平台默认中性类。
   因此**规则包的质量直接决定准确率**，规则包是核心资产，不是配置项。
3. **S5 与 S1 的分野**：必须靠第二遍的返回修正，第一遍不可能判对。

---

## 7. 边界与降级

| 情形 | 检测 | 处理 |
|---|---|---|
| DRM 黑帧 | 连续 10 帧 `visual_delta=0` 且 `P=true` | 以媒体状态为主，判 `passive_focus` |
| 全屏独占 | 窗口矩形 == 显示器矩形 | 采样降频至 15 s |
| 远程桌面 | 会话名含 `RDP-Tcp` | 暂停采集，标 `rds` |
| 钩子被拦截 | 连续 30 s 计数为 0 但 `idle` 在变化 | 降级为纯空闲模式，设置页提示 |
| 标题为空 | `title == ""` | 回退进程名，标 `title_empty` |
| 时间跳变 | 单调时钟与系统时钟偏差 > 5 s | 以单调时钟为准，记录 `clock_skew` 事件 |
| 内存压力 | 工作集 > 150 MB | 触发一次 GC 并写日志 |

---

## 8. 参考实现（伪代码）

```csharp
public Segment Classify(WindowFeatures f)
{
    if (f.Locked) return Seg(State.Locked, 0, 0, 0);

    if (f.C == 0.0 && (f.A > 0.05 || f.P))
        return Seg(State.OffTask, 0, 0, 0);

    if (f.P && f.C >= 0.6 && (f.S > 0.02 || f.V > 0.25))
        return Seg(State.PassiveFocus, 0.85, 0.75, 0.95);

    if (f.Idle > IdleMax(f.AppClass) && !f.P)
        return Seg(State.IdleAway, 0, 0, 0);

    if (f.A > 0.10 && f.C == 1.0)
        return Seg(State.ActiveFocus, 1.00, 0.95, 1.00);

    if (f.A > 0.10 && f.C == 0.6)
        return Seg(State.ActiveFocus, 0.60, 0.45, 0.75);

    if (f.V > 0.25 && f.C >= 0.6)
        return Seg(State.PassiveFocus, 0.70, 0.55, 0.85);

    if (f.C >= 0.6)
    {
        var w = SilentWeight(f.AppClass, f.Idle);
        return Seg(State.SilentFocus, w, w - 0.20, w + 0.15);
    }

    return Seg(State.Ambiguous, 0.50, 0.30, 0.70);
}
```

---

## 9. 参数表（集中管理，便于调参）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `TICK_SEC` | 5 | 采样周期 |
| `WINDOW_SEC` | 60 | 判定窗口 |
| `IDLE_MAX_TYPING` | 300 | 打字类空闲阈值（s） |
| `IDLE_MAX_READING` | 600 | 阅读类空闲阈值（s） |
| `INPUT_ACTIVE_THRESHOLD` | 0.10 | 判为"有输入"的 A 阈值 |
| `VISUAL_CHANGE_THRESHOLD` | 0.25 | 判为"画面在变"的 V 阈值 |
| `AUDIO_THRESHOLD` | 0.02 | 判为"有声音"的峰值阈值 |
| `RETURN_MAX_GAP` | 600 | 返回修正的最大间隔（s） |
| `FOCUS_BLOCK_MIN` | 1500 | 深度工作块最小长度（s） |
| `MERGE_GAP` | 60 | 同类段合并间隔（s） |

所有参数必须可在设置页调整，并持久化到 `settings` 表；调整后仅对未来的窗口生效。
