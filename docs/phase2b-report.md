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
