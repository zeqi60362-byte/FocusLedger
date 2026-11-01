# AGENTS.md · 实现契约

本文件面向**实现本项目的 AI 编码代理**。项目负责人不写代码，所有交付由你完成。
请把本文件当作硬约束，而不是建议。

---

## 0. 开工前必读（按顺序）

1. `docs/01-spec.md` —— 要做什么，功能编号 FR-*
2. `docs/02-state-machine.md` —— 核心算法，含 8 个验收场景
3. `docs/04-architecture.md` —— 怎么组织代码
4. `docs/03-data-model.md` —— 表结构
5. `docs/06-decisions.md` —— 已经拍板的决策，不要重新讨论

读完先做一件事：用 **≤ 500 字**复述你的理解，列出 **3–5 个你认为最不确定的点**向负责人提问。
**不要在没有澄清的情况下开始写代码。**

---

## 1. 工作节奏

- 严格按 `docs/05-roadmap.md` 的 M0 → M4 推进。
- **一次只做一个里程碑**。做完停下来，按 §6 的格式汇报，等待验收。
- 不要"顺手"实现下一个里程碑的功能。
- 不要在没有运行测试的情况下声称完成。

---

## 2. 环境

```powershell
dotnet --list-sdks        # 需要 8.0.x；没有则先装
dotnet build FocusLedger.sln
dotnet test
dotnet run --project src/FocusLedger.App
```

- 目标框架：`net8.0`（Core / Storage）、`net8.0-windows10.0.19041.0`（Collector / App / Cli on Windows）
- 语言版本：`latest`，启用 `Nullable`、`TreatWarningsAsErrors`（Core 项目强制）

---

## 3. 代码约束（违反即打回）

### 3.1 分层
- `FocusLedger.Core` **不得**引用 `System.Windows`、`System.Drawing`、任何 P/Invoke、任何 Windows 专属 API。
- Core 只接受 `Sample` 序列，输出 `Segment` 序列。所有时间通过 `IClock` 抽象注入。
- UI 不得直接访问数据库，只能通过 Core/Storage 暴露的查询接口。

### 3.2 隐私（最高优先级）
- 禁止任何出站网络调用（`HttpClient`、`WebRequest`、`Socket`、`WebClient`）。
- 禁止记录按键内容、鼠标坐标、剪贴板、文件内容。
- 低级钩子回调内**只允许** `Interlocked.Increment`；不允许日志、LINQ、锁、字符串拼接。
- 画面采样只允许保留 64 位哈希，禁止把 `Bitmap` 写入磁盘或日志。

### 3.3 稳定性
- 采集器中任何异常都必须被捕获并降级，**不得让进程退出**。
- 所有后台任务使用 `CancellationToken`，退出时必须在 3 秒内完成 flush。
- 禁止 `async void`（事件处理器除外）。
- 禁止在 UI 线程做 I/O。

### 3.4 可测试性
- 核心算法必须能在不启动 Windows 的情况下单测。
- 任何涉及时间的逻辑，用注入的 `IClock`，不要直接调 `DateTime.Now`。
- 时间比较使用单调时钟。

---

## 4. 测试要求

- 8 个验收场景（`docs/02-state-machine.md` §6）必须写成 `[Theory]` 数据驱动测试。
- 新增功能必须有对应测试；**修复 bug 必须先写一个失败的测试**。
- `dotnet test` 必须全绿才算完成。
- Core 测试覆盖率目标 ≥ 80%（`coverlet`）。

---

## 5. 文档要求

- 改动判定逻辑 → 同步更新 `docs/02-state-machine.md` 与场景矩阵。
- 改动数据库 → 新增迁移并更新 `docs/03-data-model.md`。
- 做出任何架构级取舍 → 在 `docs/06-decisions.md` 追加 ADR（不修改历史条目）。
- 完成里程碑 → 更新 `docs/05-roadmap.md` 的状态标记。

---

## 6. 汇报格式（每个里程碑结束时）

```markdown
## Mx 完成报告

### 做了什么
- FR-A1 ... （对应需求编号）

### 怎么验证
1. 命令：`dotnet run --project src/FocusLedger.Cli -- report`
2. 预期输出：……

### 测试
- `dotnet test`：xx passed / 0 failed
- 覆盖的场景：S1–S8

### 已知缺陷
- ……

### 需要你确认的
- ……

### 下一步
- Mx+1 计划（等人确认后才开工）
```

---

## 7. 遇到不确定时的处理

| 情况 | 做法 |
|---|---|
| 平台 API 行为不确定（DRM 黑屏、多屏 DPI、RDP） | 先写一个 20 行的探测脚本跑一遍，把实测结果贴给负责人，不要猜 |
| 需求有歧义 | 停下来问，不要自行选择一种解释然后闷头做 |
| 与既有决策冲突 | 引用 ADR 编号说明冲突，提出替代方案，等人拍板 |
| 性能不达标 | 先给测量数据，再谈优化，不要凭空重构 |
| 第三方库选择 | 优先不加依赖；必须加时说明理由、体积、许可证 |

---

## 8. 绝对不要做的事

- 不要为了"看起来完成"而写空实现或 `TODO` 占位。
- 不要修改 `docs/` 里的产品定义来迁就实现。
- 不要引入任何联网、遥测、崩溃上报 SDK。
- 不要做 `docs/01-spec.md` §8「非目标」里列的任何功能。
- 不要把"判定不准"用"多采集一点数据"来解决（那是隐私红线）。
- 不要在未经确认的情况下更换技术栈或 UI 框架。

---

## 9. 提交规范

- 提交信息：`<type>(<scope>): <subject>`，type ∈ feat/fix/test/docs/chore/refactor。
- 一个提交只做一件事。
- 每个里程碑的最后一个提交需更新 `docs/05-roadmap.md` 状态。
- 提交前必须 `dotnet test` 通过。
