# DiskThinner

一个轻量的 Windows C 盘空间分析与清理工具，要回答四个问题：哪里大、哪里在涨、是谁的、能不能删。只在本机运行，不联网，不上传任何信息。

当前版本 **0.1.0**（阶段 1 完成）：查看 C 盘哪里占用最大，每个位置是什么、归哪个软件、风险多大、该怎么处理，也可以按软件或按类别汇总；保存快照后能看到哪些目录在变大、按这个速度多少天写满；在清理计划里确认后删除或运行官方清理命令，每一项都写入操作记录。写入监控（是谁写的）和 AI 分析在后续版本提供。

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
| `settings.json` | 设置（每日快照、快照阈值和保留时长、临时文件的最小年龄、被占用文件的处理、追加的保护目录） |
| `snapshots.db` | 快照：每个 C 盘快照只记 50 MB 以上的目录和 100 MB 以上的文件，几十 KB |
| `user.db` | 操作记录：每次删除、运行命令、手动清理和拦截，只追加不修改，可以导出 CSV |
| `logs\` | `app.log`、`snapshot.log`，出错时的 `crash-*.txt` |
| `rules\` | 内置规则库（程序自带，首次运行时释放） |

扫描整个盘后，距离上次快照超过 6 小时会自动保存一次；也可以点"创建快照"，或在设置 / 增长分析页开启每日快照（Windows 计划任务）。命令行：

```powershell
DiskThinner.exe snapshot [--drive C:]                 # 扫描并保存一次快照
DiskThinner.exe snapshot --register [--time 12:00]    # 注册每日快照；以管理员身份注册时任务用最高权限运行（MFT 扫描）
DiskThinner.exe snapshot --unregister                 # 移除每日快照
DiskThinner.exe --scan D:                             # 启动后扫描指定磁盘（也可以是一个文件夹）
```

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

MFT 扫描和删除类验收需要管理员，用 `tools/verify/` 下的脚本在本机跑（删除类验收在新建的 VHDX 测试盘上进行）。

GitHub Actions：

- `ci.yml`：推送到 main 和 PR 时跑规则校验、构建、测试；推送到 main 时再打包并冒烟测试。
- `package.yml`（被另外两个调用）：打两个 zip，在全新的 windows-2022 和 windows-2025 上解压运行 `tools/verify/smoke.ps1`：扫描测试文件夹、以管理员身份用 MFT 扫描整个系统盘并生成清理计划（只读，不执行清理）、跑 snapshot 子命令。自包含版在装 .NET 之前测，依赖运行库的版本装好 .NET 10 之后测。截图和报告作为构建产物保存。
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
- 验收报告：[阶段 0](docs/phase0-report.md)、[M1](docs/m1-report.md)、[M2 上半](docs/m2a-report.md)、[M2 下半](docs/m2b-report.md)、[M3](docs/m3-report.md)、[M4](docs/m4-report.md)、[M5](docs/m5-report.md)
- 发布说明：[0.1.0](docs/release-notes-0.1.0.md)
- 规则格式：[rules/README.md](rules/README.md)
- 界面原型：[prototype/index.html](prototype/index.html)（双击用浏览器打开）

## 目录

| 目录 | 内容 |
|---|---|
| `src/` | 产品代码 |
| `tests/` | 单元测试 |
| `tools/` | 规则工具 dtrule、验收和打包脚本 |
| `rules/` | 内置规则（YAML） |
| `spikes/` | 阶段 0 的技术验证程序，不进入产品 |
| `docs/` | 设计文档和验证报告 |
| `prototype/` | HTML 界面原型 |

## 许可证

MIT
