# 04 · 架构与技术选型

## 1. 结论

**Windows 优先：.NET 8 + WPF（MVVM）+ SQLite + Win32/WinRT 直调。**

放弃原定的 Tauri + Rust，原因见 §2。

---

## 2. 为什么不是 Tauri / Electron

| 维度 | Tauri + Rust | Electron + TS | **.NET 8 + WPF** |
|---|---|---|---|
| 本机现状 | ❌ 无 rustc、无 MSVC Build Tools | ✅ Node 24 已装 | ⚠️ 有 8.0.8 运行时，缺 SDK（约 220 MB） |
| 额外环境成本 | ~3–4 GB，可能需管理员 | 0 | ~220 MB，用户级安装，无需管理员 |
| Win32 调用 | 优秀（`windows` crate） | 差（需 koffi/ffi，结构体易错） | **优秀（P/Invoke）** |
| WinRT 媒体接口 | 良好 | 差（要绕 PowerShell） | **优秀（`net8.0-windows10.0.19041.0`）** |
| 常驻内存 | 低 | 高（150–400 MB） | 中（80–150 MB） |
| 安装包 | 小（15 MB） | 大（120 MB+） | 中（60–90 MB 自包含） |
| AI 生成可靠性 | 中（Rust 编译期错误多） | 高 | 高 |
| 长时间运行稳定性 | 高 | 中 | **高** |
| 托盘 / 自启 / 单实例 | 需插件 | 需插件 | 原生 |

**决定性理由**：本机没有 Rust 与 MSVC，装齐是 3–4 GB 的绕路；而媒体播放检测（SMTC）在 .NET 里是一等公民，
在 Electron 里要么走 PowerShell 子进程（脆弱），要么走原生插件（又回到编译工具链）。对一个 7×24 常驻、
电池敏感、且全部由 AI 交付的桌面工具，.NET 的综合风险最低。

**代价**：UI 表现力弱于 Web 技术栈。缓解措施见 §7（UI 可替换）。

---

## 3. 解决方案结构

```
FocusLedger/
├─ FocusLedger.sln
├─ src/
│  ├─ FocusLedger.Core/              # 纯逻辑，net8.0，零系统依赖，100% 可单测
│  │   ├─ Model/                     # Sample, Segment, State, Features, Settings
│  │   ├─ Inference/                 # FeatureAggregator, StateMachine, PostProcessor
│  │   ├─ Rules/                     # RuleEngine, BuiltinRulePack
│  │   ├─ Reconcile/                 # TimerImport, Alignment, Attribution
│  │   └─ Time/                      # MonotonicClock 抽象
│  ├─ FocusLedger.Collector.Windows/ # net8.0-windows10.0.19041.0
│  │   ├─ ForegroundWatcher.cs       # SetWinEventHook
│  │   ├─ InputCounter.cs            # WH_KEYBOARD_LL / WH_MOUSE_LL（仅计数）
│  │   ├─ IdleProbe.cs               # GetLastInputInfo
│  │   ├─ VisualHasher.cs            # PrintWindow + dHash
│  │   ├─ MediaWatcher.cs            # SMTC
│  │   ├─ AudioMeter.cs              # IAudioMeterInformation
│  │   └─ SessionWatcher.cs          # WTS + PowerModeChanged
│  ├─ FocusLedger.Storage/           # Microsoft.Data.Sqlite + Dapper
│  │   ├─ Migrations/
│  │   ├─ Repositories/
│  │   └─ Retention/
│  ├─ FocusLedger.App/               # WPF，托盘、时间轴、日报、设置
│  │   ├─ Views/
│  │   ├─ ViewModels/
│  │   ├─ Controls/TimelineControl.cs # 自绘时间轴（DrawingVisual）
│  │   └─ Tray/
│  └─ FocusLedger.Cli/               # 无界面，日报打印与导出，便于自动化验证
├─ tests/
│  ├─ FocusLedger.Core.Tests/        # xUnit，覆盖 8 个验收场景
│  └─ FocusLedger.Storage.Tests/
├─ tools/
│  └─ probe.ps1                      # M0 信号探针
└─ docs/
```

**硬约束**：`FocusLedger.Core` 不得引用任何 `System.Windows`、`System.Drawing`、P/Invoke 或 Windows 专属 API。
它只吃 `Sample` 流，吐 `Segment` 流。这条约束保证算法可测、可移植、可开源。

---

## 4. 数据流

```
[Collector] --Sample(5s)--> [Channel<Sample>] --batch--> [Aggregator]
                                                            │
                                                     WindowFeatures(60s)
                                                            │
                                                      [StateMachine]
                                                            │
                                                       Segment
                                                            │
                             ┌──────────────────────────────┤
                             ▼                              ▼
                     [Storage: samples]            [Storage: segments]
                             │                              │
                             └──────────► [UI / CLI / Report]
```

- 采集与判定跑在**后台线程**（`BackgroundService`），UI 只读。
- 用 `System.Threading.Channels` 做有界队列（容量 1024），队列满时丢弃最旧样本并计数（`health.dropped`）。
- 判定结果通过事件推送到 UI；UI 用 250 ms 节流刷新，避免高频重绘。

---

## 5. 关键实现约定

### 5.1 采集
- 低级钩子回调中**只做 `Interlocked.Increment`**，任何 I/O、锁、LINQ 都不允许，否则会卡全局输入。
- 钩子必须设置超时卸载（`LowLevelHooksTimeout`），避免拖垮系统。
- 画面采样用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`；失败回退 `BitBlt`；再失败则跳过并计数。
- dHash：缩放到 9×8 灰度 → 相邻像素比较 → 64 位整数。全程复用同一个 `Bitmap` 缓冲，避免 GC 抖动。

### 5.2 生命周期
- 单实例：命名 Mutex；二次启动时激活已有窗口。
- 开机自启：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，路径带引号。
- 退出：先停止采集 → flush 队列 → 关闭数据库 → 释放钩子。
- 崩溃恢复：启动时检测 `samples` 最后时间戳，缺口记为 `unknown_gap`，不补数据。

### 5.3 错误处理
- 采集器任何异常都不得导致进程退出：捕获 → 记日志 → 该信号降级 → 继续。
- 日志：Serilog 滚动文件，`%LOCALAPPDATA%\FocusLedger\logs\`，保留 7 天，单文件 10 MB。
- 崩溃转储：开启 `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` 记录。

### 5.4 网络
- 应用层**禁止任何出站请求**。可加一个"网络守护"单元测试：反射扫描 `HttpClient`/`WebRequest` 使用点，命中即测试失败。

---

## 6. 依赖清单（尽量少）

| 用途 | 包 |
|---|---|
| MVVM | `CommunityToolkit.Mvvm` |
| DI / Host | `Microsoft.Extensions.Hosting` |
| 数据库 | `Microsoft.Data.Sqlite` + `Dapper` |
| 日志 | `Serilog` + `Serilog.Sinks.File` |
| 托盘 | `H.NotifyIcon.Wpf`（或手写 `Shell_NotifyIcon` P/Invoke） |
| 图表 | 时间轴自绘；统计图用 `ScottPlot.WPF`（可选） |
| 更新 | `Velopack` |
| 测试 | `xUnit` + `FluentAssertions` |

UI 主题：`WPF-UI`（Fluent 风格）或纯手写样式。禁止引入重量级 UI 框架。

---

## 7. UI 可替换性（重要）

时间轴是这个产品的"脸面"，WPF 自绘能达到 80 分的水平，但若 M2 验收时视觉不达标，
允许把 `FocusLedger.App` 的**呈现层**替换为 **WebView2 + React**（WebView2 运行时本机已装 156.x），
后端进程与 Core 完全不动。迁移成本应控制在 3 天内。

为此，UI 与 Core 之间只通过**只读查询接口 + 事件**交互，不允许 UI 直接读数据库。

---

## 8. 环境准备（已完成，2026-11-01）

本机实测状态：

| 组件 | 状态 |
|---|---|
| .NET SDK | ✅ 8.0.427 @ `C:\Users\L\.dotnet`（用户级安装，无需管理员） |
| .NET 运行时 | ✅ 8.0.8 @ `C:\Program Files\dotnet`（系统自带） |
| WebView2 | ✅ 156.0.4314.58 |
| Rust / MSVC | ❌ 未安装（不再需要） |

**两个 dotnet 根目录的坑**：

```
C:\Program Files\dotnet   → 只有运行时，无 SDK，且在 PATH 中抢先
C:\Users\L\.dotnet        → SDK 8.0.427
```

`dotnet` 命令会命中前者，`dotnet build` 失败；`DOTNET_ROOT` 对 SDK 解析无效（已实测）。
因此**所有构建必须走 `tools/dev.ps1`**：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 build
powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 test
```

首次安装 SDK 的原始命令（留档）：

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& $env:TEMP\dotnet-install.ps1 -Channel 8.0 -NoPath
```

---

## 9. 构建与发布

```powershell
# 调试运行
dotnet run --project src/FocusLedger.App

# 单文件自包含发布
dotnet publish src/FocusLedger.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist

# 打包安装器（Velopack）
vpk pack --packId FocusLedger --packVersion 0.1.0 --packDir dist
```

---

## 10. 测试策略

| 层级 | 范围 | 工具 |
|---|---|---|
| 单元 | Core 全部（特征聚合、状态机、返回修正、对账） | xUnit，无 I/O，毫秒级 |
| 场景 | `02-state-machine.md` §6 的 8 个场景，用构造的样本序列回放 | xUnit `[Theory]` |
| 集成 | SQLite 读写、迁移、保留清理 | 临时文件库 |
| 手工 | 真实桌面行为（锁屏、休眠、多屏、RDP） | 检查清单 |

**规则**：任何 bug 修复必须先写一个失败的测试。没有测试的 PR 不合入。
