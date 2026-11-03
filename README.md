# FocusLedger · 专注账本

> **专注账本 · 每一分钟都对得上**
>
> Focus ledger · every minute accounted for

你自报今天专注了 6 小时，账本上只有 3 小时，而且你说不清另外 3 小时去了哪里。
计时器只记得你按过开始；FocusLedger 记录你实际面对哪个窗口、画面有没有变化、有没有键鼠输入、
媒体在不在播放，把时间判成四种状态，再把你**声称的**和证据支持的**实证**摆在一起对账。
记录回答「多久」，判定回答「值不值」，对账回答「差在哪」。

**当前状态**：v0.9 开发版（2026-11-03）。源码可构建运行；尚未提供签名安装包。

## 关键特性

| 能力 | 说明 |
|---|---|
| 本地优先 | 应用进程不发起任何网络请求；无账号、无遥测、无崩溃上报（ADR-010） |
| 四态判定 | 深度 / 静默 / 分心 / 离席；没有正面证据一律算离席 |
| 对账 | 导入计时器 CSV，把「声称的专注区间」与「实证状态」按时间轴对齐，差额拆成离席、分心、未采集、打折 |
| 计划页 | 周视图拖拽新建；月历、重复规律、时间冲突拒绝、地点与链接备注 |
| 中英双语 | 界面即时切换，偏好保存在本机 |

## 和同类工具的区别

| 工具 | 记录了什么 | 判断是否投入 | 声称 vs 实证 |
|---|---|---|---|
| RescueTime | 应用 / 网站用时 | 按应用分类推测，不判断实际投入 | 无 |
| Toggl | 你手动按下的计时 | 无 | 无 |
| ActivityWatch | 应用 / 窗口 / 键鼠空闲 | 只区分是否 AFK | 无 |
| **FocusLedger** | 应用 + 窗口标题 + 画面变化 + 媒体播放 + 键鼠 + 锁屏 | **四态判定，带置信区间** | **有，且能拆分差额** |

护城河不在记录。记录层会被开源项目做平；判定与对账不会。

## 四种状态

| 状态 | 判据 | 统计方式 |
|---|---|---|
| 深度专注 | 有键鼠输入，且前台是工作类应用 | 计入有效专注 |
| 静默专注 | 手不动，但在看 / 听学习内容（媒体在播或画面在变） | 计入有效专注 |
| 分心 | 娱乐、社交、购物等 | 单列分心时长 |
| 离席 | 无输入、无画面变化、无媒体；含锁屏与睡眠 | 单列离席时长 |

规则很简单：**没有正面证据，就算离席。** 宁可少算，也不把「人不在」算成「在思考」——
那正是番茄钟高估的老路。

有效专注按**原始时长**计入（深度 + 静默，不再乘权重）；每个数字都能点开，看到它由哪几段时间、哪条规则得来。

## 对账

先把计时器导出的 CSV 导入本机，再按时间轴对齐。差额不是一句「效率不高」，而是拆开的三四项：

```
声称    6.0h
实证    3.1h
差额    2.9h  =  离席 1.4h  +  分心 0.9h  +  未采集 0.4h  +  打折 0.2h
```

（示意数字。）「打折」指确实在看 / 听学习内容，但属于被动或静默，按权重折算，不与离席混为一谈。

```powershell
$cli = ".\src\FocusLedger.Cli\bin\Debug\net8.0-windows10.0.19041.0\focusledger.exe"

& $cli import --file .\toggl.csv
& $cli reconcile
```

## 安装与运行

目前只有源码构建，没有安装包。

**环境**

- Windows 10 2004（19041）或更高
- .NET 8 SDK

**注意本机有两个 dotnet**：`C:\Program Files\dotnet` 在 PATH 中抢先，且没有 SDK。
不要直接敲 `dotnet`，统一走 `tools\dev.ps1`；脚本会优先使用 `%USERPROFILE%\.dotnet` 下的 SDK。

```powershell
git clone <repo-url>
cd FocusLedger

powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 build
powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 run
powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 test
```

发布单文件（self-contained，输出到 `dist\`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 publish
```

## 命令行

```powershell
$cli = ".\src\FocusLedger.Cli\bin\Debug\net8.0-windows10.0.19041.0\focusledger.exe"

& $cli report                                  # 今天的日报
& $cli capture --minutes 60                    # 采集 1 小时后立刻出报告
& $cli import --file .\toggl.csv               # 导入计时器记录（按内容哈希去重）
& $cli reconcile                               # 声称 vs 实证
& $cli report --range 30d --export md --out report-30d.md
```

`report --range 30d --export md` 生成可发布的 Markdown 报告：总体、逐日、周内节律、软件用时与分心 Top。
导出内容只有汇总与应用名，**不含窗口标题**。不传 `--out` 时输出到终端。

## 隐私

- **不联网**：应用进程没有出站网络代码；无账号、无遥测、无崩溃上报。
- **不记录内容**：键盘只计数，不存键码；鼠标只计数，不存坐标；不读剪贴板、文件内容、浏览器历史。
- **画面只留哈希**：每 5 秒把前景窗口缩成 9×8 灰度图，算一个 64 位哈希；图像缓冲用完即弃，不落盘。
- **数据在本机**：`%LOCALAPPDATA%\FocusLedger\data.db`。原始样本默认保留 14 天，可在设置页调整。
- **标题只存本机**：窗口标题用于分类，可能包含文件名或邮件主题；导出默认不带标题。
- **随时可控**：托盘一键暂停、按进程排除、设置页「删除全部数据」。

这个品类里，信任只能靠可验证的设计，不能靠声明。完整说明见 [PRIVACY.md](PRIVACY.md)；
安全问题见 [SECURITY.md](SECURITY.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/00-vision.md](docs/00-vision.md) | 定位、用户、差异化、商业模式 |
| [docs/01-spec.md](docs/01-spec.md) | 产品规格（要做什么） |
| [docs/02-state-machine.md](docs/02-state-machine.md) | 判定模型、阈值、验收场景 |
| [docs/04-architecture.md](docs/04-architecture.md) | 架构、技术选型、模块边界 |
| [docs/05-roadmap.md](docs/05-roadmap.md) | M0–M4 里程碑与发布清单 |
| [docs/06-decisions.md](docs/06-decisions.md) | 决策记录（ADR） |
| [docs/07-privacy.md](docs/07-privacy.md) | 隐私红线、威胁模型、合规 |
| [PRIVACY.md](PRIVACY.md) | 对外隐私说明 |
| [SECURITY.md](SECURITY.md) | 漏洞上报与支持范围 |
| [AGENTS.md](AGENTS.md) | 实现契约（给编码代理） |

## 许可证

[Mozilla Public License 2.0](LICENSE)，选择理由见 ADR-003。MPL 是文件级 copyleft：
修改 FocusLedger 的源文件必须以 MPL 公开；它也可以被更大或闭源的产品引用。

**名称与 logo 不在许可范围内。** 「FocusLedger」「专注账本」、应用图标与 logo 的商标权由作者保留。
fork 或再分发时请更换名称与图标，避免用户混淆。

## 参与

问题与建议走 GitHub Issues；安全漏洞不要开公开 issue，见 [SECURITY.md](SECURITY.md)。
本项目不做员工监控版：使用者必须是被记录者本人。
