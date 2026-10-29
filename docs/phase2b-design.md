# 阶段 2b 设计：常驻监控服务

状态：决策点按推荐采用（2026-10-29，用户回复"继续后续的"）：E1–E15 取第 10 节的推荐项；E16 取 B（不建测试账户，只靠单元测试和受限令牌的测试），用户要做手工检查时再建。
依据：
- `docs/dev-plan-v0.2.md`（3.7 节、第 5 节"阶段 2"）；
- `docs/phase2a-design.md` 和 `docs/phase2a-report.md`（2a 的实测、D1–D15，尤其 D3、D11、D15）；
- `prototype/index.html`；
- 现有代码（main `dd0cd73`）；
- 2b.0 的可行性验证 `spikes/AotProbe`（第 2.1 节）。

## 0. 摘要

2a 只在 DiskThinner 窗口开着、以管理员身份运行时监控，覆盖通常只有几个小时。2b 提供一个可选安装的 Windows 服务 `DiskThinner.Agent`，以 SYSTEM 身份持续记录同样的数据：哪个进程在哪个目录新建、删除、重命名、写入了文件。界面通过一个设了访问权限的命名管道查询这些数据。服务只监控、只写自己的库，不联网、不删除任何文件。

主要做法：

1. **服务直接调用 ETW API，不依赖 TraceEvent**。按 Native AOT 编译，复用 2a 的 `DiskThinner.Monitor`（解析、进程归属、估算增长、汇总、存储）。2b.0 验证过：Monitor 加 Microsoft.Data.Sqlite 做 AOT 发布没有警告，exe 3.5 MB，另带 e_sqlite3.dll 1.98 MB；带访问权限的命名管道在 AOT 下也能用。
2. **新的 ETW 消费者也用在 2a 的诊断会话上**（决策点 E6）。这样只有一条 ETW 代码路径，发布包里也不再带 TraceEvent（约 3.7 MB），加上服务后依赖运行库版仍在 10 MB 以内。
3. **安全**：
   - 服务程序复制到只有管理员能写的 `Program Files\DiskThinner\Agent\`，从那里运行，不从用户解压的目录运行 SYSTEM 程序；
   - 数据放在 `ProgramData\DiskThinner\Agent\`，设成不继承的访问权限。ProgramData 默认允许普通用户新建目录，本机已核对，所以要防"预先放好的目录"；
   - 普通用户查询时，看不到其他用户个人目录下的记录。
4. **会话按天轮换**：服务里的 ETW 会话一直开着，但会话行每天换一条。这样保留策略能按天删除，2a 的"按会话删除"不用改。
5. **不做 USN 轻量模式**（决策点 E5）：2a.1 实测 ETW 轻量模式空闲时 CPU 只有 0.06%；USN 看不到写入进程，Restart Manager 只能查到此刻打开着文件的程序，补不上这一点。

## 1. 需求

### 1.1 功能需求

- **FR-1 安装**：设置里"常驻服务"可以选。普通权限下按钮是"以管理员身份重启并安装"；管理员下弹确认框，说明服务做什么、装在哪、数据在哪、怎么卸载，确认后：
  1. 复制服务程序；
  2. 注册服务（自动启动、延迟启动、失败自动重启）；
  3. 写配置；
  4. 启动服务；
  5. 等服务通过命名管道应答。
  每一步失败都回滚（第 5.3 节）。
- **FR-2 持续记录**：服务开机后自动开始记录，每小时落盘，停止服务时最后落盘一次。模式和保留天数来自服务配置（安装时取设置里的值，之后在设置里改）。被外部停止（`logman stop`）后，过 1 分钟重新开始，并在会话里标明。
- **FR-3 查询**：界面在任何权限下都能通过命名管道查询服务的数据，和自己的诊断数据（2a 的 monitor.db）合在一起显示。
  - 显示的位置和 2a 一样：增长分析、概览、空间浏览的 ETW 证据。
  - 会话表里的服务时段标"常驻"，诊断时段标"诊断"。
  - 普通用户看不到其他用户个人目录下的记录（E4）。
- **FR-4 状态**：
  - 工具栏胶囊加两种状态："写入监控：常驻（精确）· 已记录 6 天"，以及"写入监控：常驻服务没有运行 · 启动"。
  - 状态栏同理。
  - 设置里显示服务的状态、版本、开始记录的时间、库的占用、最近的错误。
- **FR-5 控制**（管理员）：启动和停止服务；改模式和保留天数（通过管道发给服务，服务写自己的配置）。普通权限下这几个按钮是"以管理员身份重启"。
- **FR-6 更新**：
  - 界面发现服务版本和自己不同时：管理员下在设置里显示"更新服务"；普通权限下显示提示。
  - 更新时停止服务，按版本号放新程序，校验后切换并启动；启动失败就退回旧版本。
- **FR-7 卸载**：确认框里有"同时删除监控数据（ProgramData 下的 monitor.db）"，默认不勾选。卸载会停止服务、删除服务、删除程序目录；勾选时再删除数据目录。卸载后设置回到"临时诊断"。
- **FR-8 和诊断模式共存**（E12）：服务在记录时，"开启诊断"入口改成"常驻服务正在记录"，不再开第二个会话。服务装了但没运行时，诊断照常可用。
- **FR-9 子命令**：`DiskThinner.exe agent --install | --uninstall [--delete-data] | --status [--report 路径] | --update`，供验收脚本和高级用户用。退出码同 `monitor` 子命令。
- **FR-10 ETW 作为归属证据**（E11，回应 2a 的 D15）：对于规则和推断都不认识的位置，如果近 30 天常驻监控覆盖不少于 7 天，而且主要写入者占估算增长的 80% 以上，归属显示为"实测：uv.exe（Code.exe）"，标签是"实测"。规则写明的归属和推断都不被覆盖。

### 1.2 非功能需求

- **开销**：服务空闲时 CPU ≤ 0.5%（整机平均）。工作集 ≤ 96 MB：AOT 基线约 15 MB，加上 2a 的 64 MB 上限。精确模式会话缓冲区 32 MB，和 2a 一样。服务进程以低于正常的优先级运行。
- **发布包**：依赖运行库版发布包（解压后）≤ 10 MB，含服务（E6、E7）。
- **数据量**：服务的 monitor.db 不超过保留天数和 256 MB 上限（E9），任何一个先到就删除最早的会话（按天）。
- **安全**：
  - 服务以 SYSTEM 运行，只读写自己的程序目录和数据目录、注册表里自己的服务项，不打开被写入的文件；
  - 管道只接受本机连接，有访问权限；查询以外的命令只接受管理员（已提权的令牌）；
  - 不联网（项目注释写明，评审时检查没有 HttpClient 之类的引用）。
- **一定停止**：停止服务、卸载、服务崩溃时，ETW 会话都会停：前两种由服务自己停；崩溃时会话残留，由失败重启后的服务按名字清理；不再重启时由下次打开界面清理。

### 1.3 验收标准

1. 管理员下安装后：
   - `sc qc DiskThinnerAgent` 显示 LocalSystem、自动（延迟）启动、程序在 `Program Files\DiskThinner\Agent\` 下；
   - `sc qfailure` 显示失败重启；
   - `logman query -ets` 里有且只有一个 `DiskThinner-Agent` 会话；
   - 管道应答版本号。
2. 记录：WriteLoad 默认参数在精确模式下逐项比对通过，口径同 2a 的 1.3 节第 7 条：服务的数据经管道查询，和负载报告一致。
3. 普通权限的界面能查到数据；另一个普通用户看不到其他用户个人目录下的行。另一个用户只用单元测试加一次手工检查（开发机只有一个账户，第 8.3 节写明）。
4. 外部 `logman stop DiskThinner-Agent -ets` 后 1 到 2 分钟内会话重新开始，会话行标"被外部停止"。
5. 用 `taskkill /F` 结束服务进程后：
   - SCM 按失败动作重启服务，新服务清掉残留会话再开新会话；
   - 被强制结束的那一行标"程序意外退出"，结束时间取最后一次落盘。
6. 停止服务后会话消失；再启动后继续记录（新的会话行）。
7. 更新：
   - 装一个旧版本（测试用的版本号），再用新版本更新，服务版本变化、数据保留；
   - 新版本启动失败时（测试注入），退回旧版本并报错。
8. 卸载：服务、程序目录、计划外的会话都没有了；不勾选"删除数据"时数据目录保留，勾选时删除。卸载之后再安装，能接着用保留的数据。
9. 安全检查：
   - 数据目录不继承访问权限，只有 SYSTEM 和 Administrators 能访问；
   - 安装前如果有人预先建好了 `ProgramData\DiskThinner\Agent`，安装时收回所有权、重设权限，并在报告里写明；
   - 程序目录只有管理员能写；
   - 用普通用户身份发控制命令被拒绝。
10. 2a 的诊断改用新的消费者后：
    - `EtwSessionTests` 全部通过；
    - 冒烟测试的 `monitor --verify-parser`（改用 TDH 按名字取字段核对）`parse_mismatch=0`；
    - 发布包里没有 TraceEvent。
11. 发布包：两个 zip 都带 `Agent\`；依赖运行库版解压后 ≤ 10 MB。冒烟测试（CI 的 runner 是管理员）安装服务、跑负载、经管道查到数据、卸载，不留服务、会话和程序目录。
12. 界面：
    - 设置的"常驻服务"一组各种状态都有截图：没安装、运行中、停止、需要更新、出错；
    - 工具栏和状态栏的两种新状态有截图；
    - 增长分析的会话表有"常驻"行；
    - 有 ETW 实测归属的未识别目录在空间浏览里显示"实测"归属（E11）；
    - `UiAutomationTests`、`ContrastTests` 通过。
13. 全量测试、Release 构建、i18n check、AOT 发布没有警告。
14. 开销实测写进 `docs/phase2b-report.md`：服务空闲 30 分钟的 CPU 和工作集、负载下的丢失率；没有达标时写明原因。

### 1.4 不做的事

- USN 轻量模式（E5）、读事件、网络盘、单个文件的写入历史、导出监控数据。
- 托盘图标和通知。服务没有界面；状态只在打开 DiskThinner 时看。
- 自动静默更新服务：更新需要管理员，只在界面里由用户点。
- 多台机器、远程查询：管道只接受本机连接。
- 代码签名：没有证书。服务程序的完整性靠只有管理员能写的安装目录，加上安装和更新时的哈希校验（第 5.9 节）。

## 2. 查证过的事实和待实测的问题

### 2.1 已查证

| 项目 | 结果 | 来源 |
|---|---|---|
| Monitor 能否 AOT | `spikes/AotProbe` 引用 `DiskThinner.Monitor`，`dotnet publish -r win-x64 -p:PublishAot=true` 0 个 IL2xxx / IL3xxx 警告。用 MonitorStore 建库、写一次会话、`LoadIndex` 读回都正常 | 本机实测（2026-10-29） |
| AOT 体积 | `AotProbe.exe` 3,480,576 字节 + `e_sqlite3.dll` 1,978,880 字节 | 同上 |
| 带访问权限的命名管道 | `NamedPipeServerStreamAcl.Create` 加 `PipeSecurity`（SYSTEM、Administrators、当前用户）在 AOT 下能建、能连、能收发 | 同上 |
| C++ 工具链 | 本机有 Visual Studio 2026 Enterprise 的 VC 工具（AOT 链接需要）；GitHub 的 windows 镜像自带 | `vswhere` |
| ProgramData 的默认权限 | `BUILTIN\Users` 有"读取和执行"，还有对子目录生效的 `Write`（能新建目录和文件）；`CREATOR OWNER` 对自己建的内容有完全控制 | 本机 `Get-Acl $env:ProgramData` |
| Program Files 的默认权限 | 普通用户只有读取和执行 | 本机 `Get-Acl $env:ProgramFiles` |
| 2a 的开销 | 精确模式空闲 CPU 0.07%、工作集增加 22 MB、不丢事件；轻量 0.06% | `docs/phase2a-report.md` |
| 去掉 TraceEvent 能省多少 | 依赖运行库版里 TraceEvent 3.33 MB、FastSerialization 0.08 MB、NETCore.Client 0.15 MB、两个 Extensions 抽象库 0.13 MB、Monitor.Etw 0.05 MB，共约 3.7 MB | `artifacts\release\0.2.0-dev.5\files-DiskThinner.txt` |

### 2.2 只能实测确认的问题（2b.1 逐条核对，结果写进报告）

- W1 自己写的 ETW 消费者：
  - `StartTraceW` + `EnableTraceEx2` + `OpenTraceW`（`PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD`）+ `ProcessTrace`，`EventRecordCallback` 用 `[UnmanagedCallersOnly]` 函数指针，在 AOT 下收到的事件，和同时开着的 TraceEvent 会话收到的逐个一致：提供程序、事件号、版本、进程号、线程号、载荷字节。
  - 时间戳：`WNODE_HEADER.ClientContext = 2`（系统时间）时直接是 FILETIME。
  - 指针大小取 `EVENT_HEADER_FLAG_64_BIT_HEADER`。
- W2 `ControlTraceW(EVENT_TRACE_CONTROL_QUERY)` 查到的 `EventsLost`、`BuffersWritten`，和 TraceEvent 的 `EventsLost` 一致。
- W3 `TdhGetPropertySize` / `TdhGetProperty` 按名字取字段（`FileKey`、`FileName`、`ByteOffset`、`IOSize`、`IOFlags`……），足够代替 TraceEvent 的 `PayloadByName` 做 `--verify-parser` 的差分核对；哨兵偏移按原始字节比较。
- W4 服务：
  - `StartServiceCtrlDispatcherW` + `RegisterServiceCtrlHandlerExW` 在 AOT 下能跑；
  - `SERVICE_CONTROL_STOP`、`SHUTDOWN`、`PRESHUTDOWN` 的处理时间：`PRESHUTDOWN` 默认 3 分钟，我们设 15 秒；
  - 关机时最后一次落盘能完成。
- W5 管道的调用者身份：`GetNamedPipeClientProcessId` → `OpenProcessToken`，或者 `ImpersonateNamedPipeClient` → `OpenThreadToken`，拿到调用者的 SID 和"是否已提权的管理员"。在 AOT 下核对两种做法，选一种。
- W6 失败重启：
  - `ChangeServiceConfig2W(SERVICE_CONFIG_FAILURE_ACTIONS)` 设"1 分钟后重启、5 分钟后重启、之后不再重启，24 小时后计数清零"，`taskkill /F` 后确实重启；
  - `SERVICE_CONFIG_FAILURE_ACTIONS_FLAG` 设为非崩溃的错误退出也算失败。
- W7 两个会话同时开着时（服务开着，验收脚本再开一个测试会话），各自收到的事件完整，丢失为 0。这是 E12 的前提之一。验收里也用到。
- W8 去掉 TraceEvent 后，冷启动到窗口显示有没有变化（2a 报告里留下的问题）。

## 3. 原型核对

### 3.1 原型里已有的

| 位置 | 内容 | 2a 之后的实现 |
|---|---|---|
| 设置"写入监控（ETW）" | 三档：关闭、临时诊断（推荐）、常驻服务（后续版本，置灰） | 同原型，常驻服务置灰 |
| 架构图（原型说明） | Agent 服务 | 没有 |

### 3.2 原型需要改的内容（决策确认后改，第 9 节 2b.0）

- **Q1 设置"写入监控（ETW）"**："常驻服务"不再置灰，说明改成"开机自动记录，不需要打开 DiskThinner。以 SYSTEM 身份运行，只监控、不联网、不删除文件，可以随时卸载。"选中后在下面展开服务一组：
  - 状态："运行中 · 精确 · 版本 0.3.0 · 从 10-20 开始记录 · 数据 12.4 MB"，或者"已停止"、"没有安装"、"需要更新（服务 0.2.0，程序 0.3.0）"、"最近出错：……"；
  - 按钮："停止服务"或"启动服务"、"更新服务"、"卸载"；普通权限下按钮是"以管理员身份重启"；
  - 记录模式、保留天数改成作用于服务（标题注"服务的设置，所有用户共用"）；默认时长只对临时诊断有效。
- **Q2 安装确认框（新对话框"安装常驻监控服务"）**：
  - 说明："服务开机后自动记录哪个程序在本机磁盘上新建、删除、重命名、写入了文件，按目录和程序汇总。只在本机，不联网，不记录文件名和文件内容，不修改、不删除任何文件。"
  - 列出：
    - 程序装到 `C:\Program Files\DiskThinner\Agent\`；
    - 数据在 `C:\ProgramData\DiskThinner\Agent\`；
    - 空闲时 CPU 约 0.1%，内存不超过约 100 MB；
    - 在设置里随时停止或卸载。
  - 模式（轻量 / 精确）、保留天数。
  - 按钮"安装并启动"或"以管理员身份重启并安装"、"取消"。
- **Q3 卸载确认框**："卸载常驻监控服务？"，勾选框"同时删除监控数据（12.4 MB）"默认不勾选，按钮"卸载""取消"。
- **Q4 工具栏胶囊**：原型说明里加两种状态，"写入监控：常驻（精确）· 已记录 6 天"（圆点绿色，点击打开设置的写入监控一组）和"写入监控：常驻服务没有运行 · 启动"（圆点黄色）。
- **Q5 增长分析**：
  - 覆盖时段的会话表：常驻服务的行写"10-20 – 进行中 · 常驻 · 精确 · 丢失 0.0%"，按天轮换的会话合成一行连续的时段显示（只要中间没有断开）；
  - "开启诊断"按钮在服务运行时换成说明"常驻服务正在记录"；
  - "写入监控覆盖"卡片脚注"常驻服务记录中"。
- **Q6 空间浏览详情**：
  - ETW 证据的文字："近 30 天常驻监控（共 26 天）中，uv.exe 在这里新建了 8,921 个文件，估算增长 1.20 GB；进程链：……"；
  - 归属行的例子"实测：uv.exe（Code.exe）"加"实测"标签，提示里写"规则和推断都不认识这里；近 30 天常驻监控里 92% 的增长来自这个进程"。
- **Q7 普通用户看不到的行**：原型说明里写"其他用户个人目录下的写入，普通用户的界面看不到（管理员能看到）"，增长分析的写入进程排行里合成一行"其他用户的目录（需要管理员）"。

## 4. 总体设计

### 4.1 工程结构

| 项目 | 内容 | 依赖 | AOT |
|---|---|---|---|
| `DiskThinner.Monitor`（已有） | 加 `Etw/`：直接调用 ETW API 的会话和消费者 `NativeSession`、`TdhVerifier`。原有的纯逻辑和存储不变 | Core、Microsoft.Data.Sqlite | 是 |
| `DiskThinner.Monitor.Etw`（已有，E6 选 A 时删除） | 2a 的 `DiagnosticSession`、`MonitorRunner`、`MonitorCleanup` 搬进 Monitor（改用 `NativeSession`），这个项目和 TraceEvent 依赖一起去掉 | — | — |
| `DiskThinner.Agent`（新） | 服务入口（SCM 调度）、`AgentRecorder`（长期运行的会话、按天轮换、外部停止后重开）、管道服务端、配置、日志 | Monitor、Core | 是（`PublishAot`） |
| `DiskThinner.App`（已有） | `AgentClient`（管道客户端）、`AgentInstaller`（安装、更新、卸载）、`agent` 子命令、设置和状态的界面、两个新对话框、`WriteIndex` 的合并 | Monitor | 否 |
| `tests/DiskThinner.Agent.Tests`（新） | 协议、权限过滤、轮换、配置、安装步骤的回滚（用假的 SCM 和文件系统）；提权测试（`DT_AGENT_TESTS=1`） | Agent、Monitor | — |
| `spikes/AotProbe`（2b.0 已加） | 可行性验证，不进入产品 | Monitor | 是 |

管道协议的消息类型（请求、回应、数据行）放在 Monitor 里（`Agent/Protocol.cs`），服务和界面共用一份；序列化用 System.Text.Json 源生成。

### 4.2 数据流

```
Kernel-File / Kernel-Process ──ETW 实时会话 "DiskThinner-Agent"──▶ NativeSession（服务进程，泵线程）
        ▶ PayloadParser → ReorderBuffer → MonitorAggregator（2a 原样）
        ▶ 每 5 分钟和整点：WriteTreeFolder → MonitorStore（ProgramData\DiskThinner\Agent\monitor.db）
界面 ──\\.\pipe\DiskThinner.Agent.v1──▶ 服务：hello / status / query(from, to) / control(…)（管理员）
        ◀── 状态，或按（目录, 进程链）汇总后的行，已经按调用者过滤
界面：服务的 WriteIndex + 自己 monitor.db 的 WriteIndex → WriteIndex.Merge → 增长分析 / 概览 / 空间浏览
```

## 5. 方案

### 5.1 直接调用 ETW 的会话（`NativeSession`，Monitor 项目）

- **开会话**：
  - `EVENT_TRACE_PROPERTIES` 后面跟会话名的缓冲区，`LogFileMode = EVENT_TRACE_REAL_TIME_MODE`，`ClientContext = 2`（系统时间）；
  - 缓冲区大小和个数和 2a 一样：精确 32 MB、轻量 8 MB；
  - `StartTraceW` 返回 `ERROR_ALREADY_EXISTS` 时，按调用方的规则处理：诊断会话换名字；服务的会话名固定，先停掉旧的再开（是自己的残留）。
  - 两个提供程序用 `EnableTraceEx2(EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE, 关键字)` 启用，关键字和 2a 的 `Providers` 一样。
- **消费**：
  - `OpenTraceW`，`EVENT_TRACE_LOGFILEW.LoggerName` 设为会话名，`ProcessTraceMode = REAL_TIME | EVENT_RECORD`，`EventRecordCallback` 用 `delegate* unmanaged<EVENT_RECORD*, void>`；
  - 回调里构造 2a 的 `RawEvent`：`EventHeader.ProviderId`、`EventDescriptor.Id / Version`、`ProcessId`、`ThreadId`、`TimeStamp`、`UserData / UserDataLength`，指针大小按 header 标志，不复制载荷；
  - 回调拿到的是一个静态的 `GCHandle` 指向的消费者对象，用 `UserContext` 传入。
  - `ProcessTrace` 在专用的泵线程上阻塞，`CloseTrace` 或会话停止后返回。
- **统计**：每秒 `ControlTraceW(QUERY)`，读 `EventsLost`、`RealTimeBuffersLost`。会话已经停了（2a 遇到的 0x80071069）时返回最后的值，不抛异常。
- **停止**：`ControlTraceW(STOP)` → 等泵线程退出（不在泵线程上等）→ `CloseTrace`。停止流程只执行一次，和 2a 的 `DiagnosticSession.Stop` 一样，2a 的 `DiagnosticSession` 改成用它。
- **核对**：`TdhVerifier` 用 `TdhGetPropertySize` / `TdhGetProperty` 按名字取 2a 核对的那些字段，代替 TraceEvent 的 `PayloadByName`，比较规则同 2a（整数按位，字符串按序数）。只在 `--verify-parser` 时启用。
- **安全**：`EVENT_TRACE_PROPERTIES` 的缓冲区大小、名字偏移都按结构计算，单元测试检查布局（字段偏移和 Windows SDK 头文件里的一致）。

### 5.2 服务（`DiskThinner.Agent`）

- **入口**：
  - `main` 判断是不是由 SCM 启动（`StartServiceCtrlDispatcherW` 返回 `ERROR_FAILED_SERVICE_CONTROLLER_CONNECT` 时不是）。
  - 由 SCM 启动时，进入服务主函数：`RegisterServiceCtrlHandlerExW`，接受 STOP、PRESHUTDOWN、POWEREVENT（只记日志）。
  - 不是由 SCM 启动时只支持 `--console --seconds N`，前台跑一会儿，给开发和测试用。
- **服务名**：`DiskThinnerAgent`，显示名"DiskThinner 写入监控"，描述"记录本机磁盘上哪个程序写了哪些目录，给 DiskThinner 的增长分析用。只在本机，不联网，不删除文件。"
- **启动顺序**：
  1. 报 START_PENDING；
  2. 读配置；
  3. 打开库（第 5.4 节）；
  4. 把上次没有结束的会话行标成"程序意外退出"；
  5. 停掉残留的同名 ETW 会话；
  6. 开会话、列举进程；
  7. 开管道；
  8. 报 RUNNING。
  任何一步失败：记日志、报 STOPPED 并带服务专用的错误码，让失败动作生效。
- **运行**：`AgentRecorder` 包着 2a 的汇总和落盘逻辑。2a 的 `MonitorRunner` 拆成两部分：
  - 会话无关的 `Recorder`（汇总、定时落盘、降级）；
  - 时长相关的部分，只给诊断用（`SessionDeadline`、兜底任务）。
  服务的会话没有截止时间。
  - 会话行按天轮换：本地时间每天 0 点后的第一个整点落盘时，结束当前会话行（结束原因"轮换"），用同一个 ETW 会话开一条新行。汇总器的文件状态不清空，跨天的估算增长不断。
  - 被外部停止：泵线程返回、不是自己停的时，会话行标"被外部停止"。1 分钟后重开；连续 3 次都在 10 分钟内又被停掉的话，就不再重开，状态里写明，等用户在界面里启动。
- **停止**：
  - STOP、PRESHUTDOWN：报 STOP_PENDING（等待提示 15 秒）→ 停会话 → 最后一次落盘（最多 10 秒）→ 关管道 → 报 STOPPED；
  - 关机时用 PRESHUTDOWN，比 SHUTDOWN 给的时间多。
- **资源**：
  - 进程设为 `BELOW_NORMAL_PRIORITY_CLASS`；
  - 2a 的内存上限原样；
  - 工作集超过 128 MB 时记一次警告并降到轻量（D9 的同一套降级）。
- **日志**：`ProgramData\DiskThinner\Agent\logs\agent.log`，512 KB 时只保留后一半，不记文件名和路径，和 2a 一样。
- **配置**：`ProgramData\DiskThinner\Agent\agent.json`，字段 `mode`、`keepDays`、`maxBytes`、`version`。只有服务写；界面通过管道的 `configure` 命令改。读不了时用默认值，坏文件改名留着。
- **失败动作**：1 分钟后重启、5 分钟后重启，之后不再重启；24 小时后计数清零；非崩溃的错误退出也算失败（W6）。

### 5.3 安装、更新、卸载（`AgentInstaller`，App 项目）

只在管理员下执行；每一步记日志，失败时按相反的顺序回滚已经做了的步骤。

- **安装**：
  1. 检查发布目录里的 `Agent\DiskThinner.Agent.exe` 和 `e_sqlite3.dll` 存在，算 SHA-256；
  2. 建 `Program Files\DiskThinner\Agent\<版本>\`，复制，再算一次哈希核对；
  3. 准备数据目录（第 5.9 节）；
  4. 服务配置写进 `agent.json`。这是服务启动之前唯一一次由界面写这个文件，之后只由服务写；
  5. `CreateServiceW`（LocalSystem、`SERVICE_AUTO_START`、`SERVICE_WIN32_OWN_PROCESS`，二进制路径加引号）→ `ChangeServiceConfig2W` 设描述、延迟自动启动、失败动作、`SERVICE_SID_TYPE_UNRESTRICTED`；
  6. `StartServiceW`，最多等 30 秒到 RUNNING，再等管道应答 `hello`。
- **更新**：
  1. 复制新版本到 `Agent\<新版本>\` 并核对哈希；
  2. 停服务（最多 30 秒）；
  3. `ChangeServiceConfigW` 改二进制路径，指向新版本；
  4. 启动并等应答。
  失败时把路径改回旧版本再启动，报"更新失败，已恢复旧版本"。成功后删除旧版本目录，只保留当前一个。
- **卸载**：
  1. 停服务（最多 30 秒，超时报错，不强行结束）；
  2. `DeleteService`；
  3. 停掉残留的 `DiskThinner-Agent` 会话；
  4. 删除 `Program Files\DiskThinner\Agent\`；
  5. 勾选了"删除数据"时，再删 `ProgramData\DiskThinner\Agent\`。删除前核对路径就是这两个固定位置，不跟随链接。
- **子命令**：`DiskThinner.exe agent --install [--mode light|precise] [--keep-days N] | --uninstall [--delete-data] | --update | --status`，结果写 `--report` 和 `logs\agent-install.log`。
- **测试**：安装器的每一步都在接口后面（SCM、文件系统、哈希），单元测试用假的实现，覆盖每一步失败时的回滚；真实安装只在提权验收里做。

### 5.4 存储、保留、轮换

- 表结构就是 2a 的 monitor.db，不改。会话名 `DiskThinner-Agent-<开始时间>`，结束原因加"rotate"（轮换）。
- 保留：每次轮换后执行一次，按天数和 256 MB 上限（E9）删最早的会话。2a 的 `Prune` 原样用，因为会话按天轮换，删除的粒度就是一天。
- 2a 的 `SessionLiveness` 和 `MonitorCleanup` 只处理 `DiskThinner-Diag-` 前缀，不会碰服务的会话和服务的库（服务的库在另一个目录，界面普通权限下也打不开）。

### 5.5 管道协议和访问权限

- **名字**：`\\.\pipe\DiskThinner.Agent.v1`（协议版本在名字里）。服务端 `NamedPipeServerStreamAcl.Create`：
  - `PipeOptions.Asynchronous | CurrentUserOnly` 不能用，因为服务是 SYSTEM，调用者是用户；
  - 最多 4 个实例；拒绝远程客户端（`PIPE_REJECT_REMOTE_CLIENTS`）。
- **访问权限**：SYSTEM 和 Administrators 完全控制；`INTERACTIVE` 读写（能发请求、读回应）；其他身份没有权限。
- **消息**：一行一个 UTF-8 JSON（源生成），请求不超过 64 KB，回应分块（每行一个数据块，最后一行是结束标记），单次回应不超过 32 MB。
  - `hello{protocol, appVersion}` → `{protocol, agentVersion, startedUtc}`
  - `status` → `{state, mode, sessionStartedUtc, recordingSinceUtc, received, lost, sampleFactor, cpu, workingSet, dbBytes, lastError, externalStops, restarts}`
  - `query{fromUtc, toUtc}` → 会话列表（按天轮换的会话照原样给出，界面合并显示）、按小时的覆盖、按（目录, 进程链）汇总这段时间的行（`Sampled` 取或），每次最多 50,000 行，超出时把权重最小的行并到上级目录，并标出。这样 30 天的查询也只有几 MB。
  - `control{action: stop|start|configure, mode?, keepDays?}`：只接受已提权的管理员，其他人返回 `denied`。
- **调用者身份**（W5）：服务端拿调用者的令牌，判断 SID 和是否已提权的管理员。非管理员调用者的查询结果里：
  - 目录在其他用户个人目录下的行（路径前缀是 `ProfileList` 里不是调用者的 `ProfileImagePath`）删掉；
  - 写入进程的映像在其他用户个人目录下的行，把映像路径换成"其他用户的程序"；
  - 删掉的写入合成一行"其他用户的目录"，放在排行里，只有计数，没有路径（Q7）。
- **超时**：每个连接 10 秒没有请求就断开；查询在后台线程，最多 30 秒；同时最多 4 个连接，多的排队。

### 5.6 界面

- **`AgentClient`**：连不上（服务没装、没运行）时返回状态，不抛异常；`hello` 的协议版本不同时，只用状态，不查询，提示更新。连接 2 秒超时。
- **服务状态**：先查 SCM（`OpenSCManager` + `QueryServiceStatusEx`，普通权限也能查），再查管道。`MonitorHost` 加一个状态源：
  - 服务在运行时，`MonitorUiState.Agent`，对应 Q4 的两种文字；
  - 状态每 10 秒刷新一次；服务的 `lastFlush` 变化时发 `DataChanged`。
- **数据合并**：
  - `WriteIndex.Merge(params WriteIndex[])`：服务数据的会话号取负数，避免和本机诊断库的号冲突；
  - `SessionRecord` 加 `Source`（诊断 / 常驻），界面据此写"诊断"或"常驻"，并把相邻的服务会话合成一行显示（Q5）；
  - `MonitorText.CoveredHours` 原样可用（重叠只算一次）。
- **设置页**："常驻服务"一组（Q1）、安装确认框（Q2）、卸载确认框（Q3）；按钮都在后台执行，期间显示进度和结果。
- **增长分析和概览**：覆盖卡片、时间轴、会话表按 Q5。"开启诊断"在服务运行时隐藏，换成说明。
- **空间浏览**：ETW 证据的文字按数据来源分两种（Q6）。
- **归属**（E11）：`OwnerMap.Build` 加一个可选的 `ObservedOwners` 输入，由 App 从近 30 天的服务数据算出：每个"规则和推断都不认识"的热点目录，主要写入者的估算增长占比 ≥ 80%，服务覆盖 ≥ 7 天。`Owner.Source` 加 `Observed`：显示名是"实测：写入进程（顶层应用）"，标签是"实测"，证据列出覆盖天数和占比。不改变类别、风险和清理计划，"按归属汇总"里作为一个归属出现。
- **命令行**：主程序加 `--agent-pipe <名字>`（测试用另一个管道名）。

### 5.7 2a 的诊断改用新的消费者（E6 选 A 时）

- `DiagnosticSession` 内部从 TraceEvent 换成 `NativeSession`，对外的接口和行为不变（`EtwSessionTests` 原样跑）；
- `--verify-parser` 改用 `TdhVerifier`；
- `Monitor.Etw` 项目、`Directory.Build.targets` 里的两个目标、`Directory.Packages.props` 里的 TraceEvent 都删掉。`spikes/EtwBench` 还用 TraceEvent，留着（不进产品）。
- 先换实现、跑提权测试和冒烟测试，通过后再删 TraceEvent。删之前用一个提权测试同时开两个会话（TraceEvent 和 Native），比对收到的事件（W1）。

### 5.8 打包和 CI

- **打包**：`build-release.ps1` 先 `dotnet publish src/DiskThinner.Agent -c Release -r win-x64 -p:PublishAot=true`，再发布主程序，把 `DiskThinner.Agent.exe` 和 `e_sqlite3.dll` 复制到两个包的 `Agent\` 下。
  - 文件清单照写；
  - 检查依赖运行库版解压后 ≤ 10 MB，超出时打包失败。
- **CI**：`ci.yml` 的构建步骤加一次 Agent 的 AOT 发布（检查 AOT 警告为 0）。
- **冒烟测试**：`smoke.ps1` 每种包加一项 `agent-<包>`（管理员）：
  1. `agent --install --mode precise`；
  2. 跑一个小负载；
  3. 等一次落盘（服务有测试用的 `--flush-seconds`，由安装参数传入配置，验收专用）；
  4. `agent --status` 和一次查询，确认有数据；
  5. `agent --uninstall --delete-data`；
  6. 确认没有服务、没有会话、没有程序和数据目录。
- **兼容**：
  - 两个包各装一次。为了不互相影响，测试第二个包之前先卸载；
  - CI 上装好再卸载，不影响 runner（runner 用完就丢）。

### 5.9 安全

- **程序**：
  - 只从 `Program Files\DiskThinner\Agent\<版本>\` 运行，这个目录继承 Program Files 的权限（普通用户只读）；
  - 安装和更新时复制后核对 SHA-256，和发布目录里的一致才注册；
  - 服务的二进制路径加引号。
- **数据目录**：`ProgramData\DiskThinner\Agent\`。安装时：
  - 目录不存在：建好后设成不继承的权限，SYSTEM 和 Administrators 完全控制，所有者是 Administrators。
  - 目录已经存在：检查所有者和权限。不是上面的样子时，收回所有权、重设权限（含子项），并在安装日志和界面结果里写明"数据目录的权限已重设"。里面有不认识的文件（不是 `monitor.db*`、`agent.json*`、`logs\`）时，不删除，只记日志。
  - 服务每次启动也检查一次，权限不对时记警告、拒绝启动，状态报"数据目录的权限不对，请在 DiskThinner 里重新安装"。
- **管道**：见第 5.5 节。控制命令只接受已提权的管理员；查询结果按调用者过滤。请求解析失败、超长、超时都直接断开，不回显内容。
- **服务本身**：
  - 不打开被写入的文件；
  - 只读取进程映像的版本信息（和 2a 一样，用于产品名和公司名）；
  - 不加载用户目录里的任何 DLL：AOT 程序只依赖系统 DLL 和同目录的 e_sqlite3.dll，安装目录普通用户写不了；
  - `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)`。
- **评审检查**：Agent 项目里没有网络相关的引用；所有 `File.*` 调用只在数据目录和日志目录下。

## 6. 错误处理（要点）

| 操作 | 失败 | 结果 |
|---|---|---|
| 安装：复制、核对哈希 | 磁盘满、被占用、哈希不一致 | 回滚已复制的文件，界面报原因 |
| 安装：注册服务 | 已经存在同名服务（不是我们装的，或者是旧版本） | 是我们的（二进制路径在我们的目录下）：按更新处理；不是：报错，不碰 |
| 启动服务 | 30 秒没到 RUNNING，或者没有应答 | 停止并回滚安装（安装时）/ 恢复旧版本（更新时），界面给出 agent.log 的最后几行（不含路径） |
| 服务：开 ETW 会话 | 会话数已满、权限 | 报 STOPPED，带错误码，失败动作重试；状态里写原因 |
| 服务：写库 | 连续 3 次失败 | 和 2a 一样停会话；服务不退出，状态报"写库失败"，每 10 分钟重试一次 |
| 界面：连管道 | 没有服务、超时、协议版本不同 | 只显示状态，不查询，不弹框 |
| 卸载：停服务超时 | 服务卡住 | 报错，不删除，提示重启后再卸载 |

## 7. 输入校验和不变量

- 管道请求：JSON 解析失败、未知命令、字段超出范围（时间范围超过 400 天、`keepDays` 不在 30 / 90 / 180、`mode` 不认识）都返回 `error` 并断开。
- 服务只处理名字等于 `DiskThinner-Agent` 的 ETW 会话；2a 的清理只处理 `DiskThinner-Diag-` 前缀。两边互不碰。
- 安装器只写这几个地方：`Program Files\DiskThinner\Agent\`、`ProgramData\DiskThinner\Agent\`、SCM 里 `DiskThinnerAgent` 这一个服务；删除时核对路径就是这两个固定位置。

## 8. 测试和验证

### 8.1 单元测试（CI 上跑）

- `NativeSessionLayoutTests`：`EVENT_TRACE_PROPERTIES`、`EVENT_TRACE_LOGFILEW`、`EVENT_RECORD` 的大小和字段偏移，和 SDK 头文件一致（数值写在测试里，注明出处）。
- `ProtocolTests`：每种请求和回应的序列化往返；超长、坏 JSON、未知字段；分块。
- `CallerFilterTests`：管理员和普通用户的过滤；其他用户的个人目录、映像路径；"其他用户的目录"一行的计数。
- `RecorderRotationTests`：用可注入的时钟跨过本地 0 点，会话行轮换，估算增长不断；外部停止后重开和"连续 3 次就不再重开"。
- `InstallerTests`：每一步失败时的回滚（假的 SCM 和文件系统）；已有同名服务时的判断；数据目录权限的检查（假的 ACL 读写）。
- `WriteIndexMergeTests`：合并、会话号不冲突、覆盖小时不重复计算。
- `ObservedOwnerTests`：80% 和 7 天两个门槛的边界；不覆盖规则和推断。
- 界面：设置页服务一组的各种状态、两个确认框、胶囊的两种新状态（`MonitorUiTests` 扩展）；`ContrastTests` 和 `UiAutomationTests`。

### 8.2 提权测试和验收脚本

- **`AgentFact`**：不是管理员或没设 `DT_AGENT_TESTS=1` 时跳过。`AgentTests` 用测试专用的服务名 `DiskThinnerAgentTest`、管道名 `DiskThinner.AgentTest.<guid>`、数据目录 `%TEMP%\DiskThinner-2b-<guid>\`，覆盖：
  - 安装、启动、查询、停止、卸载；
  - `taskkill /F` 后失败重启；
  - 外部停止后重开；
  - 普通用户调用控制命令被拒绝：用一个受限令牌的子进程，`CreateRestrictedToken` 去掉 Administrators；
  - W1 的双会话比对。
- **`tools/verify/phase2b-elevated.ps1 -Part accept|perf|ui`**：真实服务名的安装、负载核对（第 1.3 节第 2 条）、更新、卸载、安全检查（第 1.3 节第 9 条）、开销（第 1.3 节第 14 条，空闲 30 分钟），截图。会弹 UAC，执行前告诉用户。
- **负载约束**：和 2a 一样，低于正常优先级、一次一个负载、不做人为加压。

### 8.3 手工检查（报告里写明）

- 另一个普通用户账户登录后查询，看不到第一个用户个人目录下的行。开发机只有一个账户，需要临时建一个本地账户；建账户需要你同意，否则只靠单元测试和受限令牌的测试。
- 关机和重启：服务在关机时落盘，开机后自动开始；实机做一次，报告记录日志里的时间。

## 9. 子步骤

| 子步骤 | 内容 | 验证 | 提交 |
|---|---|---|---|
| 2b.0 设计（本文档） | 设计、决策点；确认后按第 3.2 节改原型并截图 | 原型截图 | "阶段 2b.0：常驻监控服务的设计和原型" |
| 2b.1 直接调用 ETW（2 人日） | `NativeSession`、`TdhVerifier`、布局测试；2a 的 `DiagnosticSession` 改用它；双会话比对（W1–W3、W7、W8） | 提权测试、冒烟测试、2a 的提权验收 `-Part accept,etw` | "阶段 2b.1：直接调用 ETW API" |
| 2b.2 去掉 TraceEvent（0.5 人日，E6 选 A） | 删 Monitor.Etw 和 TraceEvent 依赖；搬代码 | 全量测试、冒烟测试、发布包清单 | "阶段 2b.2：去掉 TraceEvent" |
| 2b.3 服务（3 人日） | `DiskThinner.Agent`：SCM、`AgentRecorder`（拆出 `Recorder`、轮换、外部停止）、管道和过滤、配置、日志；AOT 发布（W4–W6） | 单元测试、`AgentTests`（测试服务名） | "阶段 2b.3：常驻监控服务" |
| 2b.4 安装和界面（3 人日） | `AgentInstaller`、`agent` 子命令、`AgentClient`、`MonitorHost` 的服务状态、设置页、两个对话框、增长分析和空间浏览的文字、`WriteIndex.Merge`、ObservedOwners（E11） | App 测试、提权验收 `-Part accept,ui` 的截图 | "阶段 2b.4：常驻服务的安装和界面" |
| 2b.5 打包和 CI（1 人日） | `build-release.ps1`、CI 的 AOT 检查、冒烟测试的 agent 项 | 本机打包和冒烟测试、CI 全绿 | "阶段 2b.5：打包和冒烟测试" |
| 2b.6 收尾（0.5 人日） | `docs/phase2b-report.md`（开销、安全检查、没覆盖的项）、dev-plan、known-issues、README | CI 全绿 | "文档：阶段 2b 报告" |

合计约 10 人日，在方案的 8 到 12 人日之内（不做 USN 模式省下约 2 人日，用在了去掉 TraceEvent 和调用者过滤上）。

## 10. 待确认的决策点

| # | 问题 | 选项 | 推荐 |
|---|---|---|---|
| E1 | 服务怎么托管 | A 直接调用 SCM API（`StartServiceCtrlDispatcherW`），不加依赖；B 用 Microsoft.Extensions.Hosting.WindowsServices（多几个包，AOT 下要再验证） | A |
| E2 | 服务程序放哪 | A 复制到 `Program Files\DiskThinner\Agent\<版本>\` 再注册（核对哈希）；B 直接注册解压目录里的程序 | A（B 是用户能改的目录，相当于让 SYSTEM 运行用户能替换的程序） |
| E3 | 界面怎么拿数据 | A 服务的库不让普通用户读，界面通过管道查询（服务端汇总、过滤）；B 库设成所有用户可读，界面直接用 SQLite 读 | A（B 没法按调用者过滤，30 天的明细也太大） |
| E4 | 谁能查询、能看到什么 | A 本机登录的用户都能查，非管理员看不到其他用户个人目录下的记录；B 都能查、看到全部；C 只有管理员能查 | A |
| E5 | USN 轻量模式 | A 不做（ETW 轻量模式实测 0.06% CPU；USN 看不到写入进程）；B 按研发方案做（约 2 人日） | A |
| E6 | 2a 的诊断是否也改用新的 ETW 消费者 | A 改，然后去掉 TraceEvent（只有一条 ETW 代码；发布包少约 3.7 MB，加上服务仍在 10 MB 以内）；B 2a 保留 TraceEvent | A |
| E7 | 服务程序怎么随包发布 | A 两个 zip 里都放一个 `Agent\` 目录（自包含版从 3 个文件变成 5 个）；B 作为资源嵌进主程序，安装时释放（自包含版仍是一个 exe，主程序大约 5.5 MB） | A（简单、透明，也方便核对哈希） |
| E8 | 服务默认的记录模式 | A 精确（2a.1 按 D3 判定通过）；B 轻量 | A |
| E9 | 服务数据的上限 | A 保留天数用设置里的（30 / 90 / 180，默认 90）+ 256 MB 上限；B 和诊断一样 64 MB | A（全天记录，按 2a.3 最坏情况每天 2.6 MB，64 MB 不到一个月） |
| E10 | 服务的会话怎么切 | A 每天一条会话行（同一个 ETW 会话），保留按天删除；B 每次服务启动一条（可能几个月一条，保留删不动） | A |
| E11 | ETW 是否作为归属证据（2a 的 D15） | A 只对规则和推断都不认识的位置：近 30 天常驻监控覆盖 ≥ 7 天、主要写入者占估算增长 ≥ 80% 时，归属显示为"实测：进程（顶层应用）"，不改类别、风险和清理计划；B 继续只作为证据 | A |
| E12 | 服务和诊断同时存在时 | A 服务在记录时隐藏"开启诊断"，换成"常驻服务正在记录"；服务没运行时诊断照常可用；B 都可以开 | A |
| E13 | 服务更新 | A 界面发现版本不同时提示，由用户点"更新服务"（需要管理员）；B 界面以管理员运行时自动更新 | A |
| E14 | 卸载时的数据 | A 确认框里"同时删除监控数据"默认不勾选；B 默认勾选 | A（删除数据不可恢复，按你的清理规则默认不删） |
| E15 | 服务的失败动作 | A 1 分钟、5 分钟后重启，之后当天不再重启；B 一直重启 | A |
| E16 | 第 8.3 节另一个用户的手工检查 | A 临时建一个本地标准用户做一次，做完删除账户；B 不建，只靠单元测试和受限令牌的测试 | 采用 B（建账户是对系统的改动，用户没有明确同意） |

## 11. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| 自己写的 ETW 消费者有布局错误 | 收不到事件或解析错 | 布局测试 + W1 的双会话逐个比对；不通过就不进行 2b.2，2a 保留 TraceEvent |
| AOT 下某个 API 不能用（服务、令牌、ACL） | 服务做不成 AOT | 2b.0 已验证 SQLite 和管道；W4、W5 在 2b.1 开头先验证；实在不行改成依赖运行库的服务（运行库是全机的，SYSTEM 也能用），报告写明 |
| 杀毒软件对"安装 SYSTEM 服务 + ETW"敏感 | 误报、拦截 | 只在用户确认后安装；服务名、描述、路径都写清楚；报告记录 Defender 的表现 |
| 服务长期运行的内存增长 | 超过 96 MB | 2a 的所有上限都在；轮换不清空的只有文件状态（有 300,000 项的上限）；验收里跑 30 分钟 + 一次跨天的单元测试；报告记录 |
| 关机时最后一次落盘没完成 | 丢最后一小时 | PRESHUTDOWN 加 15 秒；每 5 分钟本来就落盘，最多丢 5 分钟 |
| 多用户隐私 | 普通用户看到别人的目录 | E4 的过滤；单元测试；第 8.3 节的手工检查 |
| 用户删了解压目录 | 服务仍在运行，找不到界面来卸载（服务不在"设置 → 应用"列表里） | 服务从 Program Files 运行，不受影响；任何版本的 DiskThinner 都能卸载它；README 写明手动卸载的命令（`sc stop DiskThinnerAgent`、`sc delete DiskThinnerAgent`）和要删的两个目录 |
