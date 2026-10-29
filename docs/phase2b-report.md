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
