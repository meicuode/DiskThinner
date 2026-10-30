# 阶段 2b 报告：常驻监控服务

日期：2026-10-29 起。设计见 `docs/phase2b-design.md`，决策点 E1–E15 按推荐采用，E16 选 B（不建测试用的用户账户）。
本报告按子阶段逐节补充。

## 2b.1 + 2b.2：直接调用 ETW API，去掉 TraceEvent（2026-10-29）

### 改动

- `src/DiskThinner.Monitor/Etw/` 下新增三个文件：
  - `NativeEtw.cs`：结构体和 P/Invoke（`StartTraceW`、`ControlTraceW`、`EnableTraceEx2`、`OpenTraceW`、`ProcessTrace`、`CloseTrace`、`QueryAllTracesW`、`TdhGetPropertySize`、`TdhGetProperty`）。
  - `NativeSession.cs`：实时会话。回调是 `[UnmanagedCallersOnly]` 函数指针，泵线程专用；停止只执行一次，泵线程没按时结束时不释放回调用到的内存。
  - `TdhVerifier.cs`：`--verify-parser` 的差分核对，用 TDH 按名字取字段，代替 TraceEvent 的 `PayloadByName`。
- `DiagnosticSession` 改用 `NativeSession`。`DiagnosticSession`、`MonitorRunner`、`MonitorCleanup` 搬进 Monitor 项目，命名空间 `DiskThinner.Monitor.Etw` 不变。
- 删掉 `DiskThinner.Monitor.Etw` 项目和 `Directory.Build.targets`（原来用来去掉 TraceEvent 带的原生 DLL）。App 只引用 Monitor。TraceEvent 只留在 `spikes/EtwBench`，不进产品。
- `monitor` 子命令的报告加 `parse_verified`（核对上的事件数）。冒烟测试检查它大于 0，并检查包里没有 TraceEvent。`build-release.ps1` 的禁止文件清单加上 TraceEvent 和 FastSerialization。
- 新增 `NativeEtwLayoutTests`：结构体大小和字段偏移和 SDK 头文件一致。这些值用一个编译的 C 程序从 SDK 取得：`EVENT_TRACE_PROPERTIES` 120、`EVENT_TRACE_LOGFILEW` 448、`EVENT_RECORD` 112。
- 新增 `EtwBench compare`：同时开 TraceEvent 会话和 `NativeSession`，比对两边收到的事件。`AotProbe --etw`：在 Native AOT 下开 `NativeSession`。两者都由 `phase2a-elevated.ps1 -Part compare` 运行。

和设计不同的两处：

- 2b.1 和 2b.2 合成一步提交。设计要求删 TraceEvent 之前先做双会话比对，比对放在保留 TraceEvent 的 `EtwBench` 里，所以不影响删除产品里的依赖。
- 时间戳：设计写的是 `ClientContext = 2`（系统时间）。实现用 `ClientContext = 1`（QPC，精度更高）。没有设 `PROCESS_TRACE_MODE_RAW_TIMESTAMP` 时，`ProcessTrace` 交出的就是 FILETIME。和 TraceEvent 比，时间戳差的中位数是 0（见下表）。

### W 清单（设计 2.2 节）

环境同 2a：Windows 11 25H2（10.0.26200），22 个逻辑核。比对负载是 WriteLoad 的默认参数，精确模式的关键字，缓冲区 32 MB。

| 项 | 结果 | 结论 |
|---|---|---|
| W1 自己写的消费者 | cmd.exe 和 WriteLoad 的事件（含目标进程是它们的进程事件）两边各 2,916 个，逐个一致：提供程序、事件号、版本、进程号、线程号、载荷字节。时间戳差：中位数 0 µs，99% 在 10.8 µs 以内，最大 3.8 ms。指针大小按事件头标志取 | 通过 |
| W1（AOT） | `AotProbe --etw`（Native AOT，3.6 MB）：收到 500 个事件，自己写的 20 个文件都有写事件，TDH 核对 371 个，不一致 0，丢失 0；停止后会话不残留 | 通过 |
| W2 丢失数 | 两边的 `EventsLost` 都是 0；`ControlTraceW(QUERY)` 直接查到的 `EventsLost`、`RealTimeBuffersLost` 也都是 0。`BuffersWritten` 没有比：它取决于每个会话各自的刷新时机，两个会话本来就不同，产品也不用它 | 通过 |
| W3 TDH 取字段 | 精确模式核对 1,401 个事件、轻量模式 590 个，不一致 0。第一次 AOT 运行查出 1 处不一致：Kernel-Process 进程结束事件的 `ImageName` 在清单里是 `win:AnsiString`，`TdhVerifier` 原来一律按 UTF-16 解码。改成按 ASCII 解码后为 0。手写解析器本来就按 ANSI 解析，没有问题 | 通过 |
| W7 两个会话同时开着 | 负载期间（去掉头尾各 0.5 秒）全机的事件两边各 4,931 个，一边有的另一边都有；两边丢失 0 | 通过 |
| W8 冷启动 | 见下表：没有可测出的变化 | 见下 |

W8：启动到主窗口出现的时间（`phase2a-perf.ps1`），0.2.0-dev.5（带 TraceEvent，内容同 0.2.0）和 0.2.0-dev.6（去掉后）交替各启动 7 次：

| | 0.2.0-dev.5 中位数 | 0.2.0-dev.6 中位数 | 各次范围 |
|---|---|---|---|
| 普通权限 | 1.88 秒 | 1.66 秒 | 1.57–3.11 秒 |
| 管理员 | 2.61 秒 | 2.49 秒 | 1.73–3.51 秒 |

单次之间差到 ±40%，中位数之间差 5%–12%，在误差内。2a 时 TraceEvent 本来就没有在窗口显示前加载（`traceevent_loaded_at_shown=False`），去掉它不影响冷启动。≤ 1 秒的目标仍然没达到，原因不在 TraceEvent，留到以后单独查。另外，紧接着冒烟测试跑的那一次管理员测量，0.2.0-dev.6 的中位数是 3.50 秒；单独再测时是 2.49 秒，那次是冒烟测试刚跑完 MFT 扫描、系统还忙造成的。

### 验证

- 全量测试通过：App 150、Cleanup 35、Monitor 121（另有 8 个提权测试跳过）、Rules 105、Scanner 35（另有 8 个跳过）、Storage 22。i18n 检查 817 条，一致。
- 提权验收（`phase2a-elevated.ps1 -Part compare,accept -SkipWatchdog`），`accept_failures=0`：
  - 到时停止；
  - 强制结束后 `monitor --cleanup` 停掉残留会话、标记"程序意外退出"、删掉兜底任务；
  - 负载下的精确和轻量模式，`parse_mismatch=0`，丢失 0；
  - `EtwSessionTests` 8/8，含默认负载下的逐项比对和 `ParseVerified > 0`。
  - 兜底任务这一步（a4，要等 7 分钟）跳过：它用 `logman` 停会话，和 TraceEvent 无关，代码只是搬了位置，没有改。
- 发布包 0.2.0-dev.6：
  - 依赖运行库的版本 3.4 MB、18 个文件（zip 1.7 MB），去掉前是 7.3 MB、24 个文件（zip 2.8 MB）；
  - 自包含的版本 zip 44.7 MB，去掉前是 45.8 MB。
- 本机管理员的冒烟测试 0 项失败。两种包的 monitor 项丢失都是 0，核对数分别是 491 和 369。
- "监控中（精确）+ MFT 扫描整个系统盘"的峰值内存 300 MB（2a 是 345 MB）。

## 2b.3：常驻监控服务（2026-10-29）

### 改动

- Monitor 项目：
  - `Etw/Recorder.cs`：从 `MonitorRunner` 拆出和会话无关的部分（解析 → 重排 → 汇总 → 定时落盘、降级、统计），诊断和服务共用。`MonitorRunner` 只剩诊断会话的开始、截止时间和停止，对外接口不变。
  - 按天轮换（只有服务打开）：本地日期变了之后，第一次定时落盘（整点后至少 60 秒）时在 UTC 整点结束当前行，结束原因 `rotate`；整点之后的数据都记到新行。汇总器接着用，跨天的估算增长不断。
  - `Agent/Protocol.cs`：管道协议，一行一个 JSON（源生成），带请求校验。`Agent/AgentQuery.cs`：调用者过滤（E4）、查询的合并和超限折叠。`Agent/DataDirectoryAcl.cs`：数据目录权限的检查和设置。
  - `MonitorStore.LoadMerged`：在 SQL 里按（目录, 进程链）合计，不把逐小时的行读进内存。另加 `FirstSessionStartUtc`、`MonitorStats.Minus`、`MonitorAggregator.ClearFlags`。
- 新项目 `src/DiskThinner.Agent`（Native AOT）：
  - `ServiceControl`：直接调用 SCM 的 API（E1）；
  - `AgentHost`：启动顺序、请求处理；
  - `AgentRecorder`：ETW 会话，被外部停止后重开，写库失败时重试；
  - `PipeServer`：管道服务端；
  - `AgentConfig`、`AgentLog`、`AgentSettings`：配置、日志、命令行参数。
- 测试：
  - Monitor.Tests 加 `RecorderTests`（跨 0 点轮换、换汇总器时计数不丢）和 `AgentProtocolTests`（协议、调用者过滤、查询合并、`LoadMerged`），共 26 个；
  - 新的 Agent.Tests：配置、日志、参数的单元测试 13 个，外加提权的 `AgentServiceTests`（`DT_AGENT_TESTS=1`）；
  - `tools/verify/phase2b-elevated.ps1 -Part agent` 运行提权测试。

和设计不同的几处：

- **查询的行**：按（目录, 进程链）跨会话合计。每行的会话号取最近的会话，小时取最早的小时，界面按会话找"最近一次写入"的功能照样能用。
- **管道权限**：INTERACTIVE 只给读写数据的权限。`PipeAccessRights.Write` 里含"新建管道实例"，给了它，普通用户就能抢先建同名实例冒充服务。第一个实例带 `FILE_FLAG_FIRST_PIPE_INSTANCE`，名字已经被占时服务以退出码 13 退出。因此客户端要用只请求 ReadData | WriteData 的构造函数连接，2b.4 的 `AgentClient` 照这个写，并核对服务端进程号。
- **控制命令**：`control stop/start` 只停止或开始记录，服务进程留着；停止服务本身由界面通过 SCM 做（2b.4）。`configure` 改了模式时，结束当前行（结束原因 `config`），用新模式重开会话。
- **轮换的统计**：旧行的会话统计（收到数等）算到轮换的那一刻，也就是整点后 1 分多钟。按小时的覆盖和写入行按整点分开。
- **汇总器重建（设计里没有）**：轮换时如果目录表超过 100,000 项、或进程链超过 5,000 条，就换一个新的汇总器，免得长期运行的服务把表涨到上限。整点之后还没落盘的计数，按（目录路径, 进程链）合并进下一次落盘。
- **体积**：AOT 发布用 `OptimizationPreference=Size`、`StackTraceSupport=false`、`UseSystemResourceKeys=true`，exe 从 5.18 MB 降到 4.04 MB。依赖运行库版的包加上 `Agent\`（exe 和 e_sqlite3.dll）约 9.6 MB，在 10 MB 以内。
  - `InvariantGlobalization` 不能用：Core 的 `L` 要建 zh-Hans 区域，在固定区域性下会抛 `TypeInitializationException`。异常出在泵线程上，会话随后以 error 结束、1 分钟后重开。第一次提权测试就是这样查出来的。

### W 清单（续）

| 项 | 结果 | 结论 |
|---|---|---|
| W4 服务 | AOT 下 `StartServiceCtrlDispatcherW` + `RegisterServiceCtrlHandlerExW` 能跑。`sc start` 到 RUNNING 0.78–0.87 秒，`sc stop` 到 STOPPED 0.31–0.33 秒（含最后一次落盘）。PRESHUTDOWN 的 15 秒要在安装时用 `ChangeServiceConfig2W` 设（2b.4）。关机时落盘的实机检查放到 2b.4 装上真实服务之后 | 通过（关机部分待做） |
| W5 调用者身份 | 选 `ImpersonateNamedPipeClient`（.NET 的 `RunAsClient`）取线程令牌，客户端用 Identification 级别连接。结果：<br>· 去掉 Administrators 的受限令牌发控制命令，返回 `denied`；<br>· 匿名级别连接发控制命令，返回 `denied`；<br>· 受限令牌查询，看得到调用者自己个人目录下的行 | 通过 |
| W6 失败重启 | 测试服务设 `restart/2000/restart/5000`、`failureflag 1`：<br>· `taskkill /F` 后 2 秒重启。新进程先把被结束的那一行标成 `crashed`（结束时间是最后一次落盘），停掉残留会话，再开新会话；<br>· 占住管道名让服务以 13 退出，5 秒后重启成功，说明非崩溃的错误退出也算失败；<br>· 数据目录的权限不对时服务以 10 退出，不写日志 | 通过 |

`AgentServiceTests` 的其他结果：

- 外部 `logman stop` 后按测试设置的 5 秒重开，那一行标 `external`；
- 测试过程中留下的会话行依次是 external、crashed、service-stop、error、service-stop，都有结束原因；
- 服务刚启动时工作集 19–21 MB；
- 日志里没有负载目录的路径；
- 测试结束后没有留下测试服务和会话。

### 验证

- 全量测试通过：Agent 13（另有 1 个提权测试跳过）、App 150、Cleanup 35、Monitor 147（另有 8 个跳过）、Rules 105、Scanner 35（另有 8 个跳过）、Storage 22。i18n 818 条，一致。
- Agent 的 AOT 发布没有警告。
- 提权：2a 的 `EtwSessionTests` 8/8 通过，这是 `Recorder` 拆分后的回归；`AgentServiceTests` 1/1 通过，用的是 AOT 发布版。
- 还没做的验证：空闲 30 分钟的开销和长时间运行的内存（2b.4 装上真实服务后做）；真实安装路径下的权限设置（2b.4）。

## 2b.4：常驻服务的安装和界面（2026-10-30）

### 改动

- **安装器** `App/Agent/AgentInstaller.cs`（第 5.3 节）：
  - 安装：复制并核对 SHA-256 → 准备数据目录（`DataDirectoryAcl.Apply`）→ 写 `agent.json` → `CreateServiceW` 和 `ChangeServiceConfig2W`（描述、延迟自动启动、失败动作、非崩溃的失败也算、服务 SID、PRESHUTDOWN 15 秒）→ 启动并等管道应答。任何一步失败都按相反的顺序撤销。
  - 更新：新版本复制到自己的目录 → 停服务 → 改程序路径 → 启动并等应答；起不来时改回旧路径再启动。成功后删掉旧版本的目录。
  - 卸载：停服务（30 秒，超时报错，不强行结束）→ 删服务 → 停残留会话 → 删程序目录；勾选时再删数据目录。删除前核对路径的最后两级是 `DiskThinner\Agent`，遇到链接只删链接本身。
  - 已经有同名服务、但程序不在我们的目录下时，报错，不碰它。
- **`agent` 子命令**（FR-9）：`--install | --update | --uninstall [--delete-data] | --status [--dir 路径]`，结果写 `--report`。`--status --dir` 给出这个目录下的新建个数、估算增长和会话行的结束原因，冒烟测试和验收用它。
- **Monitor/Agent**：
  - `ServiceManager`（SCM 的查询和操作，普通权限能查）；
  - `AgentClient`：管道客户端。只请求读写数据的权限，用 Identification 级别连接；连上后用 `GetNamedPipeServerProcessId` 核对服务端就是 SCM 里这个服务的进程；
  - `WriteIndex.Merge` 合并两份数据，服务的会话号取负数；`SessionRecord.Source` 区分诊断和常驻；`WriteIndex.HiddenOtherUsers` 是普通用户看不到的计数。
- **界面**：
  - `MonitorHost` 每 10 秒查一次服务，加了两种状态 `Agent`、`AgentDown`。`Load` 把诊断库和服务的数据合在一起。
  - 工具栏胶囊（Q4）：
    - "写入监控：常驻（精确）· 已记录 N 天"；
    - "写入监控：常驻服务没有运行 · 启动"或"写入监控：常驻服务需要更新 · 更新"。
  - 设置页"常驻服务"一组（Q1）：
    - 没有安装时只有"安装…"，普通权限下是"以管理员身份重启并安装"；
    - 装了以后显示状态和"停止服务 / 启动服务""更新服务""卸载…"，普通权限下只有"以管理员身份重启"；
    - 选了常驻服务、服务在记录时，记录模式和保留显示服务的值，修改经管道发给服务；普通权限下这两项不能改；
    - 保留一行显示"诊断 X · 常驻服务 Y"。
  - 安装对话框 `AgentInstallDialog`（Q2）；卸载确认（Q3）用 TaskDialog 的勾选框"同时删除监控数据"，默认不勾选。
  - 增长分析（Q5）：
    - 首尾相接的常驻会话行合成一段，写"常驻 · 精确模式"；
    - 服务在记录时，"开启诊断"按钮隐藏，说明前面加"常驻服务正在记录"；
    - 覆盖卡片的脚注是"常驻服务记录中 · 丢失 0.0%"；
    - 写入进程排行里，普通用户看到合成的一行"其他用户的目录（需要管理员）"（Q7）。
  - 空间浏览的 ETW 证据（Q6）：有常驻数据时写"近 30 天常驻监控（共 N 天）中，……"；不到一天时写"共 N 小时"。
- **实测归属**（E11）`App/Monitor/ObservedOwners.cs`：
  - 条件：规则和推断都给不出归属，近 30 天常驻监控覆盖 ≥ 7 天，估算增长 ≥ 64 MB，主要写入进程占 ≥ 80%。归属写"实测：uv.exe（Code.exe）"，来源标签"实测"，证据列出覆盖天数、占比和进程链。
  - `OwnerMap.SetObserved` 只在原本没有归属的位置生效，不改变类别、风险和清理计划。每次扫描后分析时算一次。

和设计不同的几处：

- `AgentClient` 和 `ServiceManager` 放在 Monitor 项目，不在 App。这两个类服务端测试也用；Monitor 可以 AOT，不依赖 WinForms。
- 版本目录名是 `<版本>-<exe 哈希的前 8 位>`：开发版本的版本号相同、程序不同时也能区分，更新时也能认出"已经是这个版本"。
- **实测归属往上合并只走到分叉之前**（设计没写到）：一个目录下有两个以上有增长的子目录，就不再往上。否则 `AppData\Local` 这种放着很多程序的目录，会因为近 30 天的增长正好大多来自一个程序，被算成那个程序的。
- **关闭时删除的文件（V12）的计数时机**：测试服务的负载比对里，WriteLoad 的 50 个关闭时删除的文件，过了 20 多秒还没有收到 NameDelete，期间显示为增长 3.2 MB。2a 的测试是停止时由 `Finish` 按删除算，所以没有暴露出来。改成：会话行结束时（停止、外部停止、按天轮换）把还没等到 NameDelete 的这种文件按删除算，估算增长不跨过会话行。外部停止之后再查，删除 50/50，估算增长 0。代价是一天之内，这种临时文件的增长会先显示出来。
- 胶囊在服务需要启动或更新时，管理员点了直接启动或更新；普通权限下提示以管理员身份重启。

### 验收（`phase2b-elevated.ps1 -Part agent,accept`，真实服务名 DiskThinnerAgent，2026-10-30）

| 步骤 | 结果 |
|---|---|
| 没有安装时的设置页和安装对话框 | "没有安装"，对话框截图 |
| 安装（预先建好 `ProgramData\DiskThinner\Agent`，权限继承自 ProgramData，里面放了一个不认识的文件） | 1.1 秒装好并应答；报告"数据目录的权限已重设"；不认识的文件留着（1.3 第 9 条） |
| 服务配置（注册表） | 自动（延迟）、LocalSystem、服务 SID 不受限、非崩溃的失败也算、PRESHUTDOWN 15000 ms、失败动作 60 秒 / 300 秒后重启、之后不再重启、24 小时清零、程序路径在 `Program Files\DiskThinner\Agent\` 下；只有一个 `DiskThinner-Agent` 会话（1.3 第 1 条） |
| 权限 | 程序目录普通用户、Everyone、Authenticated Users、INTERACTIVE 都不能写；数据目录不继承，只有 SYSTEM 和 Administrators（1.3 第 9 条） |
| 负载 | 30 个 64 KB 文件加一次 WriteLoad，11 秒内落盘、经管道查到；丢失 0 |
| 普通权限（`runas /trustlevel:0x20000`，去掉 Administrators） | 子命令和界面都能经管道查到数据；读不了数据目录的权限（`no-access`），这符合预期（FR-3、1.3 第 3 条） |
| 界面截图（管理员和普通权限） | 增长分析有"常驻 · 精确模式"一行和实测的写入进程；设置页显示"运行中 · 精确 · 版本 0.2.0 · 从 10-30 开始记录 · 数据 56.0 KB"；普通权限下只有"以管理员身份重启"；空间浏览的证据是常驻监控的写法 |
| 外部 `logman stop` | 62 秒后重开，会话行标 external（1.3 第 4 条） |
| `taskkill /F` | 62–64 秒后 SCM 重启服务；被结束的那一行标 crashed，会话只有一个（1.3 第 5 条） |
| 停止、启动 | 停止后会话消失，设置页"已停止"，胶囊黄色；再启动后继续记录（1.3 第 6 条） |
| 更新 | 同一个版本："已经是最新版本"。程序末尾加 16 字节（哈希不同）：换到新目录，旧目录删掉。坏的程序（不是可执行文件）：启动失败，改回原来的路径并重新启动，坏的目录删掉（1.3 第 7 条） |
| 卸载（保留数据）→ 重新安装（轻量、30 天）→ 卸载并删除数据 | 保留数据时服务、程序目录、会话都没有了，`monitor.db` 还在；重新安装后接着用（7 个会话行，结束原因都对），模式和保留天数是新选的；删除数据后数据目录和空的 `Program Files\DiskThinner` 都没有了（1.3 第 8 条） |

`AgentServiceTests`（测试服务）：

- 新加的 WriteLoad 默认参数逐项比对：经管道查询，small、big、append、prealloc、短路径、move 各项都对上（1.3 第 2 条）；temp 的删除和增长按上面说的在外部停止之后核对。
- 其余同 2b.3：受限令牌和匿名连接的控制命令被拒绝、外部停止、强制结束、错误退出后重启、数据目录权限不对时拒绝启动。

还没做的：

- 关机时的最后一次落盘（W4）要实机重启一次，留到 2b.6 收尾时做。
- 空闲 30 分钟的开销（1.3 第 14 条）由 `-Part perf` 测，结果写在下一节。
- 另一个用户账户的手工检查不做（E16 选 B），靠 `CallerFilterTests` 和受限令牌的测试。

### 验证

- 全量测试通过，App 测试 176 个，其中新加 `AgentInstallerTests` 16 个、`AgentUiTests` 10 个；Monitor 测试 148 个，其中新加轮换时结算关闭时删除的文件 1 个。i18n 一致。
- 提权：2a 的 `EtwSessionTests` 8/8；`AgentServiceTests` 1/1；真实服务的验收 `accept_failures=0`。

## 2b.5：打包和冒烟测试（2026-10-30）

### 改动

- `tools/release/build-release.ps1`：
  - 先 Native AOT 发布服务，有任何 IL2xxx / IL3xxx 警告就不打包；
  - 把 `DiskThinner.Agent.exe` 和 `e_sqlite3.dll` 复制到两个包的 `Agent\` 下；
  - 检查依赖运行库版解压后不超过 10 MB。
- `ci.yml`：构建之后加一步服务的 AOT 发布，AOT 警告写成注解并让 CI 失败。
- `tools/verify/smoke.ps1`：每个包加一项 `<包>-agent`（管理员）：
  1. 用包里的 `Agent\` 安装（5 秒落盘）；
  2. 确认有 `DiskThinner-Agent` 会话；
  3. 写 20 个文件，经管道查到；
  4. 卸载并删除数据；
  5. 确认不留服务、会话、程序目录和数据目录。
  已经装了常驻服务或者有它的数据目录时跳过。`package.yml` 把这一项的报告也存成构建产物。

会话列表改用 `agent --status` 的 `sessions_active`，原因是：第一次本机冒烟测试时，自包含包装好服务后，`logman query -ets` 的输出里没有 `DiskThinner-Agent`，同一轮依赖运行库的包却有；2b.3 的测试服务也遇到过一次。`sessions_active` 由 `QueryAllTracesW` 取得。

### 结果（0.2.0-dev.7）

- 依赖运行库版：解压后 9.4 MB、20 个文件（2b.1 是 3.4 MB、18 个文件，加了服务的 4.05 MB 和 e_sqlite3.dll 的 1.98 MB），zip 4.8 MB，在 10 MB 以内（1.2 节、1.3 第 11 条）。
- 自包含版：zip 47.8 MB，`Agent\` 一样。
- 本机冒烟测试（管理员）0 项失败。两个包的 agent 项都在 1.0–1.1 秒装好，20 个文件经管道查到，丢失 0，服务的工作集 24–25 MB，卸载后什么都不留。

### 开销（1.3 第 14 条：服务空闲 30 分钟，`phase2b-elevated.ps1 -Part perf`）

用依赖运行库版包里的服务，精确模式，开发机正常使用（编辑器、浏览器开着），每分钟记一次：

| 指标 | 结果 | 目标 |
|---|---|---|
| CPU（服务自己报的，从会话开始的整机平均） | 第 30 分钟 0.087%，最高 0.165%（第 7 分钟） | ≤ 0.5% |
| 工作集 | 开始 34 MB，峰值 56 MB（第 15 分钟），之后稳定在 52–53 MB | ≤ 96 MB |
| 私有字节 | 23 MB → 40 MB | — |
| 收到的事件 / 丢失 | 548,156 / 0 | 丢失 ≤ 0.1% |

工作集在前 15 分钟上涨，主要来自 2a 的几张表：文件名、目录、进程。之后稳定，没有一直涨。长期运行的上限靠 2a 的那些上限，加上轮换时的汇总器重建（2b.3）。
