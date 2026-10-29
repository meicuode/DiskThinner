# DiskThinner

一个轻量的 Windows C 盘空间分析与清理工具，要回答四个问题：哪里大、哪里在涨、是谁的、能不能删。只在本机运行，不联网，不上传任何信息。

当前版本 **0.2.0**（阶段 1 和阶段 2a 完成）：查看 C 盘哪里占用最大，每个位置是什么、归哪个软件、风险多大、该怎么处理，也可以按软件或按类别汇总；保存快照后能看到哪些目录在变大、按这个速度多少天写满；以管理员身份运行时可以开 1 到 24 小时的写入监控，看到是哪个程序在哪些目录新建、删除、写入了文件；在清理计划里确认后删除或运行官方清理命令，每一项都写入操作记录。常驻监控服务（阶段 2b）和 AI 分析在后续版本提供。

## 下载和运行

| 文件 | 说明 |
|---|---|
| `DiskThinner-<版本>-win-x64.zip` | 体积小，需要先装 [.NET 10 桌面运行库（x64）](https://dotnet.microsoft.com/download/dotnet/10.0)；没装时双击会提示去下载 |
| `DiskThinner-<版本>-win-x64-selfcontained.zip` | 单个 exe，不需要装运行库 |

解压到任意目录，运行 `DiskThinner.exe`。支持 Windows 10 / 11 64 位。程序不需要安装，删除解压出来的目录和 `%LOCALAPPDATA%\DiskThinner` 即可完全卸载（如果开启了每日快照，先在设置里关掉）。

## 权限

- 默认以普通权限运行：逐个目录遍历，C 盘要一两分钟，结果为估算（识别不了硬链接）。
- 以管理员身份运行时直接读取 NTFS 主文件表（MFT），C 盘几秒扫完，能识别硬链接。界面上可以一键"以管理员身份重启"。
- 用另一个管理员账户提权时（登录的是 A，提权成 B），`%USERPROFILE%` 指向 B：这时不清理用户目录、不运行官方命令，只清理系统位置，界面上会说明。
- 写入监控（阶段 2a）需要管理员：普通权限下点"开启诊断"，对话框的按钮是"以管理员身份重启并开始"。

## 写入监控（阶段 2a，诊断模式）

- 工具栏右侧的"写入监控"或增长分析页的"开启诊断"：选时长（1 / 4 / 8 / 24 小时）和模式。轻量模式只记新建、删除、重命名；精确模式还记每次写入，能估算每个程序让目录涨了多少，编译、npm install 时开销更大。默认精确，空闲时监控本身的 CPU 约 0.1%。
- 监控期间保持 DiskThinner 打开（可以最小化）；到时间、点"停止"或关闭窗口都会停止。为了在程序被强制结束时也能停下来，开始时会临时添加一个计划任务 `\DiskThinner\StopDiagnostic`，监控结束后删除，过期后也会自己删除。
- 只在本机记录，按"目录 × 进程（写入进程 ← 顶层应用）× 小时"汇总；不记录文件名和文件内容，不联网，不修改、不打开被写入的文件。
- 看不到的情况：监控开始前就打开的文件（数据库、虚拟磁盘）的写入对不上目录；虚拟磁盘里面是谁写的；缓存回写记为"系统"。详见 [docs/known-issues.md](docs/known-issues.md)。
- 设置里可以把写入监控改成"关闭"（不再提示开启诊断），也可以改默认模式、默认时长和保留天数。

## 清理是怎么做的

- 清理只在"清理计划"页由你确认后执行：先弹出确认框，列出删除后无法恢复的内容。**删除是永久的，不经过回收站**（C 盘的回收站不释放 C 盘空间）。
- 凭据、浏览器登录数据、加密货币钱包、程序和系统目录在内置保护名单里，任何规则都清理不了；可以在设置里追加自己的保护目录。
- 执行前逐项复核：路径上不能有 junction 或符号链接、再查一次保护名单、规则仍然命中当初的位置。删除时不跟随链接，按句柄确认后再删；被占用的文件跳过，不强制结束程序。
- 每一项都可以改成"手动清理（打开所在位置）"：工具只用资源管理器打开目录，不删除任何东西；处理完点"重新测量"记下释放了多少。
- 官方清理命令（uv、npm、pip、dotnet nuget 等）只运行内置白名单里的，程序必须是本机探测到的那个，参数固定。
- 判断"归哪个软件"时只在本机读取注册表卸载项、商店应用列表、PATH 里的程序和正在运行的程序路径。

## 数据和日志

都在 `%LOCALAPPDATA%\DiskThinner\` 下：

| 文件 | 内容 |
|---|---|
| `settings.json` | 设置（写入监控、每日快照、快照阈值和保留时长、临时文件的最小年龄、被占用文件的处理、追加的保护目录） |
| `snapshots.db` | 快照：每个 C 盘快照只记 50 MB 以上的目录和 100 MB 以上的文件，几十 KB |
| `monitor.db` | 写入监控的汇总：按会话、小时、目录、进程链；默认保留 90 天，不超过 64 MB。删掉这个文件就清掉了全部监控数据 |
| `user.db` | 操作记录：每次删除、运行命令、手动清理和拦截，只追加不修改，可以导出 CSV |
| `logs\` | `app.log`、`snapshot.log`、`monitor.log`，出错时的 `crash-*.txt` |
| `rules\` | 内置规则库（程序自带，首次运行时释放） |

扫描整个盘后，距离上次快照超过 6 小时会自动保存一次；也可以点"创建快照"，或在设置 / 增长分析页开启每日快照（Windows 计划任务）。命令行：

```powershell
DiskThinner.exe snapshot [--drive C:]                 # 扫描并保存一次快照
DiskThinner.exe snapshot --register [--time 12:00]    # 注册每日快照；以管理员身份注册时任务用最高权限运行（MFT 扫描）
DiskThinner.exe snapshot --unregister                 # 移除每日快照
DiskThinner.exe --scan D:                             # 启动后扫描指定磁盘（也可以是一个文件夹）
```

写入监控的无界面子命令（诊断和验证用，主程序是窗口程序，结果写在 `--report` 指定的文件和 `logs\monitor.log` 里）：

```powershell
# 监控 N 秒（10 到 86400，需要管理员）；--verify-parser 同时用系统的解码核对解析器
DiskThinner.exe monitor --seconds 600 [--mode light|precise] [--db monitor.db] [--report r.txt] [--verify-parser]
DiskThinner.exe monitor --cleanup [--db monitor.db]   # 停掉上次没有正常结束的会话（管理员），标出没有结束的记录
DiskThinner.exe monitor --status [--report r.txt]     # 有没有窗口在监控、有没有残留会话（不需要管理员）
```

退出码：0 成功，1 出错，2 不是管理员，3 另一个实例正在监控，4 参数不对。

## 构建

需要 Windows 10/11 和 .NET 10 SDK（版本见 `global.json`）。

```powershell
dotnet build DiskThinner.slnx
dotnet test DiskThinner.slnx
dotnet run --project src/DiskThinner.App

# 规则维护
dotnet run --project tools/dtrule -- validate rules
dotnet run --project tools/dtrule -- probe rules --drive C:\ --out probe.md

# 在本机打包两个发布 zip 到 artifacts\release\<版本>\（不带 -Version 时用 Directory.Build.props 里的版本号）
pwsh -File tools/release/build-release.ps1 [-Version 0.2.0]
```

主程序构建时会自动用 dtrule 校验 `rules/` 并编译成内置规则库；规则有错误时构建失败。

界面文字写成 `L.T("清理计划")`、`L.F("扫描 {0} 项", count)`，源代码里的中文就是 `src/DiskThinner.Core/Strings.resx` 的键。新写或改了界面文字后运行 `dotnet run --project tools/i18n -- extract src src/DiskThinner.Core/Strings.resx` 更新 resx，CI 会检查（见 [tools/i18n/README.md](tools/i18n/README.md)）。

MFT 扫描和删除类验收需要管理员，用 `tools/verify/` 下的脚本在本机跑（删除类验收和扫描结果验收 `scan-elevated.ps1` 在新建的 VHDX 测试盘上进行）。

`tools/verify/run.ps1 build|test|release` 以低于正常的优先级构建和测试，测试项目逐个跑，完整输出写到 `artifacts\logs\`。

写入监控的验收（阶段 2a）需要管理员，`Start-Process pwsh -Verb RunAs` 启动 `tools/verify/phase2a-elevated.ps1 -OutDir <目录> -Part <部分>`（会弹 UAC；可以用逗号连着写几个部分，只弹一次）：

| 部分 | 内容 |
|---|---|
| `bench` / `bench2` | ETW 开销实测（2a.1） |
| `accept` | `monitor` 子命令的到时停止、强制结束后清理、兜底计划任务（`-SkipWatchdog` 跳过，要等几分钟）、负载下的精确和轻量模式，再跑 `etw` |
| `etw` | 以 `DT_ETW_TESTS=1` 跑 `EtwSessionTests`（开关会话、到时停止、外部停止、残留清理、WriteLoad 负载的逐项比对）。平时和 CI 上这些测试跳过 |
| `ui` | 调用 `tools/verify/phase2a-ui.ps1`：有监控数据时各页面的截图，`--monitor-seconds` 到时停止，关窗口时停止 |
| `perf` | 调用 `tools/verify/phase2a-perf.ps1 -Before <旧发布目录> -After <新发布目录>`：冷启动、发布包大小、"监控中 + MFT 扫描"的峰值内存 |

所有验收都把库、设置和报告写在 `-OutDir` 下，不碰默认位置的数据；只删除自己在 `%TEMP%\DiskThinner-2a-<guid>` 下建的目录。构建前先用普通权限 `dotnet build -c Release`。

GitHub Actions：

- `ci.yml`：推送到 main 和 PR 时跑规则校验、构建、测试；推送到 main 时再打包并冒烟测试。
- `package.yml`（被另外两个调用）：打两个 zip（打包时检查没有带 TraceEvent 和它的原生 DLL，并写出文件清单），在全新的 windows-2022 和 windows-2025 上解压运行 `tools/verify/smoke.ps1`：扫描测试文件夹、以管理员身份用 MFT 扫描整个系统盘并生成清理计划（只读，不执行清理）、跑 snapshot 子命令、跑 15 秒的精确模式 `monitor --verify-parser`（要求收到事件、解析器和系统解码一致、不留会话）。自包含版在装 .NET 之前测，依赖运行库的版本装好 .NET 10 之后测。截图和报告作为构建产物保存。
- `release.yml`：推送 `v主.次.修订` 形式的标签（如 `v0.2.0`，或 `v0.2.0-beta.1` 发布为预发布）时，跑同样的检查和冒烟测试，都通过后才创建 GitHub Release 并上传 zip 和 `SHA256SUMS.txt`。版本号以标签为准；发布说明取 `docs/release-notes-<版本>.md`，没有就用通用说明。

发布新版本：

```powershell
# 1. 改 Directory.Build.props 里的 <Version>，写 docs/release-notes-<版本>.md，提交
# 2. 打标签并推送，Release 工作流会自动构建和发布
git tag v0.2.0
git push origin v0.2.0
```

## 文档

- 研发方案：[docs/dev-plan-v0.2.md](docs/dev-plan-v0.2.md)
- 验收报告：[阶段 0](docs/phase0-report.md)、[M1](docs/m1-report.md)、[M2 上半](docs/m2a-report.md)、[M2 下半](docs/m2b-report.md)、[M3](docs/m3-report.md)、[M4](docs/m4-report.md)、[M5](docs/m5-report.md)、[阶段 2a](docs/phase2a-report.md)
- 阶段 2a 设计（写入监控的诊断模式）：[docs/phase2a-design.md](docs/phase2a-design.md)
- 发布说明：[0.2.0](docs/release-notes-0.2.0.md)、[0.1.0](docs/release-notes-0.1.0.md)
- 已知问题和讲述人人工检查清单：[docs/known-issues.md](docs/known-issues.md)
- 规则格式：[rules/README.md](rules/README.md)
- 界面原型：[prototype/index.html](prototype/index.html)（双击用浏览器打开）

## 目录

| 目录 | 内容 |
|---|---|
| `src/` | 产品代码 |
| `tests/` | 单元测试；`tests/fixtures/` 是规则匹配和热点分解的 JSON 用例（目录树加期望结果，格式见其中的 README） |
| `tools/` | 规则工具 dtrule、验收和打包脚本 |
| `rules/` | 内置规则（YAML） |
| `spikes/` | 技术验证程序（阶段 0 的扫描对比、阶段 2a 的 ETW 实测 EtwBench），不进入产品 |
| `docs/` | 设计文档和验证报告 |
| `prototype/` | HTML 界面原型 |

## 许可证

MIT
