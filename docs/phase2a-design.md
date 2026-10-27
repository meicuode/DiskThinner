# 阶段 2a 需求与技术设计：写入监控的诊断模式（ETW）

日期：2026-10-27　状态：草案，已按第一、二轮设计评审修订（处理记录见第 12 节），待评审复核和用户确认（第 10 节的决策点）
依据：`docs/dev-plan-v0.2.md`（1.3、2.2、2.3、3.3、3.5、3.7 节，第 5 节"阶段 2"，第 8 节）、`prototype/index.html`、现有代码（main 分支 `cd9ba3a`）、第一轮评审 `artifacts/workflow/b-design-review.md`、第二轮评审 `artifacts/workflow/b2-design-review.md`。

## 0. 摘要

阶段 1 只能回答"哪里变大了"。2a 在界面以管理员身份运行时开一个专用的 ETW 实时会话（1 到 24 小时），记录哪个进程在哪个目录新建、删除、重命名文件（精确模式再加写入字节），按"目录 × 进程链（写入进程、顶层应用）× 小时"汇总，存进本机的 `monitor.db`，再接到增长分析页（覆盖时间轴、写入进程列、写入进程排行）、概览和空间浏览的证据里。

做法上有三条主线：

1. 先实测：2a.1 测两种模式在空闲、小规模合成写入、编译三种负载下的原始开销（每秒事件数、监控进程的 CPU 和内存、内核里会话缓冲区的占用、丢失率），核对只能实测确认的事实（第 2.2 节），并把原始事件存到本机文件；2a.2、2a.3 的汇总逻辑写好后离线回放这些事件（不需要管理员），得出处理速率和逐项比对，再按 D3 定 2b 的默认模式，写进报告（第 5.2 节）。实测不做人为加压，测试进程低于正常优先级、一次只跑一个（第 8.4 节）。
2. 纯逻辑和事件源分开：新项目 `DiskThinner.Monitor`（AOT 兼容，不引用 TraceEvent）放载荷解析、事件重排、进程归属、估算增长、汇总合并、抽样、互斥量和会话状态判定、存储和查询，用合成事件和 JSON fixtures 在 CI 上测；`DiskThinner.Monitor.Etw` 只负责用 TraceEvent 开关会话、把原始事件交给前者。2b 的 AOT 服务直接复用前者，换掉后者。
3. 会话一定会停：唯一命名；全局互斥量由专用线程获取和释放（停止可以发生在任何线程上）；停止流程只执行一次，重复调用拿到同一个任务，定时落盘和最后一次落盘串行；按墙钟和经过时间双重检查截止时间（睡眠唤醒后也成立）；窗口关闭停止；被外部停止时如实标出；崩溃时尽力停止；每次启动判定残留会话和没有结束的会话记录；一次性的兜底计划任务（决策点 D7）。

## 1. 需求

### 1.1 功能需求

- FR-1 开启写入监控对话框：时长 1 / 4 / 8 / 24 小时，模式轻量 / 精确。管理员下直接开始；普通权限下按钮是"以管理员身份重启并开始"，用现有的 `ShellActions.RestartElevated` 重启，新实例带参数自动开始。新实例发现用的是另一个管理员账户（`AccountInfo.Mismatch`）时不自动开始，先显示对话框和账户提示，用户确认后再开始。
- FR-2 诊断会话：一次只有一个；到时停止（按墙钟算，睡眠和休眠的时间也算在内）；窗口关闭、以管理员身份重启、注销关机时停止；被外部停止（例如有人运行 `logman stop`）时如实标出；未处理异常时尽力停止；启动时清理上次崩溃留下的会话，并把没有正常结束的会话记录标出来；最长 24 小时。程序被强制结束时由兜底任务在计划结束 1 分钟后停止，这种情况最长 24 小时 1 分钟；机器那时在睡眠的话醒来后补跑（D7）。
- FR-3 事件：Microsoft-Windows-Kernel-File（轻量：新建文件 / 目录、删除、重命名、建硬链接；精确：再加写入和文件名事件）、Microsoft-Windows-Kernel-Process（进程启动、退出）。
- FR-4 进程归属：开始时列出现有进程补齐父进程链；svchost 等服务宿主按进程号对应到服务名；确定"写入进程"和"顶层应用"；记下程序的产品名和公司名；界面显示时再用已装软件匹配应用名。
- FR-5 汇总：每小时一桶，"目录 × 进程链"一行，计数（新建文件、新建目录、删除、重命名）和字节（精确模式：写入量、估算增长）。目录向上合并到快照记录过的节点，每小时最多 1,000 行（第 5.7 节，决策点 D6）。对不上目录的写入（监控开始前已打开的文件）按进程链单独记，进写入进程排行，界面不把它说成"没有写入"（第 5.6.1、5.9.4 节）。
- FR-6 抽样和丢失：写入事件过多时自动抽样（按倍数放大并标"估算"），统计 ETW 丢失率、抽样比例、无法定位到目录的写入；丢失持续过高时自动降到轻量模式（决策点 D9）。
- FR-7 存储：`%LOCALAPPDATA%\DiskThinner\monitor.db`（决策点 D4），每 5 分钟和每个整点落盘，按天数和总大小清理（决策点 D5）。
- FR-8 界面：增长分析的"写入监控覆盖"卡片、覆盖时段卡片（时间轴、会话列表、开启 / 停止）、变化最大的目录的"写入进程"列、"写入进程排行"；概览的提示和"增长最快"的写入者；空间浏览详情的 ETW 证据；设置页写入监控一组；工具栏和状态栏的监控状态。
- FR-9 命令行：`DiskThinner.exe monitor` 子命令（验证和冒烟测试用，第 5.9.8 节）；主程序的自动化参数（`--monitor-db`、`--start-monitor`、`--monitor-mode`、`--monitor-seconds`、`--show-monitor-dialog`）。
- FR-10 附带修复（和监控无关，单独提交）：方块图的读屏帮助文字有时被鼠标停留时的路径提示顶替（`docs/known-issues.md` 已记录，原因没有确认），先复现确认再修（第 5.12 节）。

### 1.2 非功能需求

- 开销：2a 诊断在空闲时监控进程 CPU ≤ 1%（整机）、进程内存增量 ≤ 64 MB。内存里有上限的结构都算在这 64 MB 里：进程表、`DirectoryTable`、`fileKeyToName`、`fileState`、按小时的计数器、事件重排窗口（上限见第 1.3 节第 17 条）。会话缓冲区在内核的非分页池里，不算进进程内存，单独统计，不超过 D3 的上限（初定轻量 16 MB、精确 32 MB）。能不能作为 2b 默认看第 5.2 节的规则（决策点 D3）。
- 研发方案 1.3 的性能目标不因 2a 变差：冷启动到窗口显示 ≤ 1 秒（启动时不加载 TraceEvent，第 5.9.1 节）；依赖运行库版发布包 ≤ 10 MB；扫描后内存 ≤ 300 MB，"监控中 + MFT 扫描"时不超过 300 + 64 MB。报告里记录实测值（第 5.11 节）。
- 数据量：单次 24 小时会话的 `monitor.db` 增量：每小时都用满预算（1,000 行）的合成最坏情况 ≤ 4 MB，每小时都到硬上限（2,000 行）时 ≤ 8 MB（第 5.8 节）。monitor.db 和快照库是两个文件，单次快照 ≤ 1 MB 的目标不受影响。
- 隐私和安全：只在本机，不联网；不修改、不删除任何用户文件，也不打开被写入的文件（只读取正在运行的程序映像的版本信息，以及含 8.3 短名的目录的长名）；只存目录路径、进程映像路径、服务名、产品名和公司名，不存文件名、文件内容和命令行；日志里不记事件中的文件名和路径（第 5.10 节）。
- 工程约定：`DiskThinner.Monitor` 按 AOT 要求写（不用反射，SQLite 手写读取，P/Invoke 用 `LibraryImport`）；界面文字 `L.T` / `L.F`；新颜色进 `ContrastTests`；新控件有 AccessibleName。
- 发布：依赖运行库版和单文件自包含版都要能跑 ETW 会话，包里不带用不到的架构的原生文件（第 5.11 节）。
- 验证负载（用户要求）：不设计人为加压的测试；测试进程以低于正常的优先级运行，同一时间只跑一个测试进程（被观测的负载程序除外）；每个子步骤的"修改 → 验证"最多 3 轮（第 8.4 节）。

### 1.3 验收标准

1. 普通权限下打开对话框，主按钮是"以管理员身份重启并开始"；UAC 点"否"时不启动、状态栏提示已取消；点"是"后新窗口自动开始监控，时长和模式是对话框里选的。用另一个管理员账户提权时，新窗口不自动开始，而是打开对话框（时长和模式已预填）并显示黄色的账户提示，点"开始监控"后才开始。
2. 管理员下开始监控后，`logman query -ets` 里有且只有一个 `DiskThinner-Diag-<14 位时间>` 会话；工具栏和状态栏显示"诊断中"、模式和剩余时间。
3. 到时自动停止（用 `--monitor-seconds` 和 `monitor --seconds` 验证）：会话从 `logman query -ets` 消失，会话记录的结束原因是"到时"；停止发生在计时器线程上，程序不崩溃、没有新的崩溃报告，同一个进程里能再次开始监控（`EtwSessionTests`）。计时器线程正在停止时另一个线程再调用停止，拿到同一个任务，结束原因仍是"到时"；定时落盘和最后一次落盘不交错，库里最后留下的是最新的计数（`MonitorRunnerTests`）。截止时间按墙钟和经过时间双重检查，睡眠唤醒和改系统时间用可注入时钟的单元测试覆盖（实机睡眠不在自动验收里，报告里写明）。
4. 关闭窗口（含 `--exit-after-scan`）后会话消失，结束原因是"程序退出"。会话被外部停止（测试里用 `logman stop <名字> -ets`）时，结束原因是"被外部停止"，状态栏显示"写入监控被外部停止"，不会一直显示"诊断中"。
5. 强制结束进程后会话残留；以管理员身份再打开 DiskThinner 或运行 `monitor --cleanup` 后消失，会话记录标为"程序意外退出"；采用 D7 时，不打开程序也会在计划结束时间 1 分钟后被兜底任务停止。"用电池时也运行"只由 `WatchdogTaskTests` 断言 XML 里的电池设置覆盖，实机用电池不在自动验收里，报告里写明。普通权限下：有另一个窗口正在监控时显示"另一个 DiskThinner 窗口正在监控"，只有残留会话时显示"上次没有正常结束"。任何权限启动时，没有实例在监控的未结束会话记录都标为"程序意外退出"，结束时间取最后一次落盘时间。名字不符合 `^DiskThinner-Diag-\d{14}$` 的会话一律不碰。
6. 超过 24 小时的时长在 API 层被拒绝（`ArgumentOutOfRangeException`），主程序命令行里超出范围的值被忽略并记日志，`monitor` 子命令返回退出码 4。
7. 负载核对（精确模式）：测试负载 `DiskThinner.WriteLoad.exe`（第 8.2 节）在 `%TEMP%\DiskThinner-2a-<guid>\` 下各子目录的结果和负载报告逐项比对（丢失为 0 时计数完全相等，否则误差不超过丢失的事件数）：
   - `small\`：新建、重命名、删除数一致；估算增长和 `final_bytes` 误差 ≤ 1%；写入量在 `bytes_written` 的 100% 到 105% 之间；
   - `big\`：估算增长误差 ≤ 1%，写入量在 100% 到 105% 之间；
   - `append\`：三种追加方式（只有 `FILE_APPEND_DATA` 访问权限的句柄 `a{i}.bin`、打开后移到末尾写 `o{i}.bin`、`cmd.exe` 的 `>>` 写 `c{i}.bin`）的估算增长都等于各自的 `final_bytes`（误差 ≤ 1%），没有重复计算。报告写明哪种方式出现了 V16 的哨兵偏移；`a{i}.bin` 出现哨兵偏移时，这一项才算核对过第 5.6.2 节第 1 条；
   - `move\`：`dst\`（从 `tmp\` 跨目录移进来的文件，以及改名覆盖已有文件的 `x{i}.bin`）和 `done\`（由 `stage\` 整体改名而来）的估算增长等于各自的 `final_bytes`（误差 ≤ 1%），`tmp\`、`stage\` 在整个范围里合计为 0；重命名数一致。V4 的结果是"FilePath 为旧名字"而且改名后 2 秒内没有同一 FileKey 的 NameCreate 时，这一项只比对计数，会话带 flags 64，报告写明；
   - `temp\`（`DeleteOnClose`）：新建数和删除数都一致（V12 不成立时按第 5.6 节的规则算删除），估算增长约为 0；
   - `prealloc\`（只 `SetLength`、不写）：新建数一致，估算增长为 0（和快照的差别写进说明）；
   - 通过 8.3 短路径写入的目录：行的目录是长路径（卷上没有短名时这一项记"跳过"）；
   - 写入进程是 `DiskThinner.WriteLoad.exe`、进程链里有 `cmd.exe`；`append\c{i}.bin` 的写入进程是 `cmd.exe ← DiskThinner.WriteLoad.exe ← cmd.exe`，估算增长仍记在新建它的 WriteLoad 的进程链上。
   轻量模式下计数一致、字节为空。脚本结束后只删除这个 guid 目录；默认位置的 monitor.db 没有被改动。
8. 开销实测写进 `docs/phase2a-report.md`：两种模式 × 三种负载的每秒事件数（按事件类型）、监控进程 CPU 和内存峰值、会话缓冲区大小和非分页池的变化、丢失率、负载耗时变化（2a.1 实测）；单线程离线回放的可持续处理速率和 W2 的逐项比对（2a.3b，第 5.2 节）；按 D3 的规则写出 2b 默认模式和 2a 对话框默认模式；2a.4 的实机验收数字和 D3 的推算对照，差得多时写明并回头修订 D3 的结论。
9. 增长分析页：比较范围里有监控数据时，"写入监控覆盖"显示"N / M 小时"和丢失率；时间轴和会话列表与 `monitor.db` 一致；"写入进程"列有数据时在同一格里显示进程链和"实测"标签（读屏读出"进程链 实测"），范围内完全没有监控时显示"未监控"，有监控但没有能对上这里的记录时按第 5.9.4 节的三种情况显示（只有轻量模式、有大量对不上目录的写入、真的没有写入），不会在只有轻量模式或写入对不上目录时说"没有写入"；"写入进程排行"精确模式按写入量、轻量模式按新建文件数排序，对不上目录的写入也算进排行（主要目录列写"对不上目录（监控前已打开的文件）"），双击跳到主要目录。
10. 空间浏览详情：选中监控期间被写入过的目录时，证据里有一条"ETW"（只有这一条证据时也有"证据"标题）；监控过但没有能对上这里的记录时按第 5.9.4 节的三种情况显示；从没监控过时不显示这一条。按归属 / 类别汇总的视图里，只有选中组（`ShowGroup`）时不显示 ETW，选中组里的具体节点时（`ShowNode`）照常显示。
11. 设置页："临时诊断"可选，"常驻服务"仍是后续版本；记录模式、默认时长、保留天数写进 settings.json，下次打开对话框时生效；显示 monitor.db 的占用。
12. 概览：近 7 天没有监控覆盖、设置不是"关闭"时显示"开启 4 小时诊断"提示；"增长最快"的第三列"写入者"按第 5.9.5 节的三种情况显示，有监控写入时文字是"写入进程 ← 顶层应用"（例如"uv.exe ← Code.exe"）加"实测"标签，完整进程链在工具提示里。
13. 纯逻辑的单元测试（解析和事件模板核对、事件重排、进程归属、互斥量和会话状态判定、截止时间、兜底任务 XML、估算增长（含改名、跨目录移动、目录改名）、汇总合并、抽样、丢失率、存储、查询、停止和落盘的串行化、ItemList 的标签列）在 CI 上全部通过。CI 上没有设置 `DT_ETW_TESTS`，需要管理员的测试跳过；本机没设这个环境变量时也跳过。
14. 全量测试、Release 构建、`tools/i18n check`、`dtrule validate` 通过；新颜色搭配在 `ContrastTests` 里；`UiAutomationTests` 通过（新控件都有名字）。
15. 两种发布包在开发机和 CI 的 windows-2022 / 2025 冒烟测试上（package.yml 的注释说明 runner 是管理员）跑 `monitor --seconds 15 --verify-parser`：退出码为 0、收到事件、`parse_mismatch=0`、不留会话；包里没有 `x86\` 和 `arm64\` 目录，`amd64\` 下只有 V7 证明需要的文件（V7 证明都不需要时没有 `amd64\`），`Dia2Lib.dll`、`TraceReloggerLib.dll` 同样按 V7 处理；不带 RID 的构建输出（测试项目的输出目录）里也没有 `x86\`、`arm64\`（`PackagingTests`，这一条才证明 `Directory.Build.targets` 生效，第 5.11 节）；两个包的文件清单（相对路径和大小）和加 TraceEvent 之前的对比写进报告。
16. 截图（`artifacts\phase2a\`）：增长分析（有数据）、设置、空间浏览详情（有 ETW 证据）、开启监控对话框（管理员和普通权限两种），100% 缩放；有 150% 缩放的显示器时用 `--secondary-screen` 再截一组。
17. 24 小时最坏情况合成测试：每小时 1,000 行时 monitor.db ≤ 4 MB，每小时 2,000 行（硬上限）时 ≤ 8 MB；保留策略按天数和大小上限（按库里实际占用的页算）删除最早的会话，超出大小上限一点时只删最早的一个会话。内存上限（进程表 100,000、`DirectoryTable` 200,000 个节点、`fileKeyToName` 400,000 项及其目录字符串池、`fileState` 300,000 项、计数器 200,000 项、重排窗口 100,000 个事件）按 1/10 规模实测后外推，合计 ≤ 64 MB。
18. 文档：`docs/phase2a-report.md`、`docs/dev-plan-v0.2.md` 阶段 2 下的进度说明、`docs/known-issues.md`、README（monitor 子命令、验收脚本）。
19. 方块图：显示路径提示之后，读屏读到的帮助文字仍然是"方向键……"的操作说明（用发送鼠标消息的方式复现，不移动真实鼠标）；`docs/known-issues.md` 里这一条改成已修复（复现不了时改成"未复现"并写明复现方法）。
20. 研发方案 1.3 的目标：报告里记录冷启动到窗口显示的时间（加 TraceEvent 前后各测一次，≤ 1 秒）、依赖运行库版发布包的大小（≤ 10 MB）、"监控中（精确）+ MFT 扫描"时的进程内存峰值（≤ 364 MB），和目标对照；不达标时写明原因，不悄悄放宽。

### 1.4 不做的事

- 2b 的全部内容：AOT 常驻服务、直接调用 ETW API、USN 轻量模式、命名管道、服务安装卸载。设置里的"常驻服务"仍显示为后续版本。
- ETW 结果不改变归属引擎的结论：3.3 节说"归属以实测证据优先"，但 2a 只把 ETW 作为证据显示，不改 `OwnerMap`、"按归属汇总"和清理计划。理由：监控覆盖通常只有几个小时，用它覆盖规则和推断的结论容易以偏概全。留到 2b 有持续数据时再定。
- 读事件、网络盘和没有盘符的卷、调用栈、单个文件的写入历史、导出监控数据。
- 2a 只在 DiskThinner 窗口打开期间监控，不做托盘常驻。

### 1.5 假设

- 只支持 x64（发布包只有 win-x64）。TraceEvent 4.0.0 要求 Windows 10 以上，和本项目的支持范围一致。
- 监控数据和快照一样跟着当前用户的 `%LOCALAPPDATA%`。用另一个管理员账户提权时数据存在那个账户下，界面要说明（第 5.1.5 节）。
- 实测和验收在开发机（Windows 11 25H2）上做；Server 2022 / 2025 只通过 CI 冒烟测试覆盖。
- 实时会话的事件不保证按时间顺序到达（ETW 按处理器分缓冲区，实时消费者按缓冲区交付；Microsoft Learn "Logging Mode Constants" 的 `EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING` 就是为这种乱序准备的，这一点凭文档记忆，没有联网重查）。设计按"可能乱序"处理（第 5.4.1 节），乱序的程度由 V22 实测。

## 2. 查证过的事实和待实测的问题

### 2.1 已查证

| 项目 | 结果 | 来源 |
|---|---|---|
| Kernel-File 提供程序 | GUID `EDD08927-9CC4-4E65-B970-C2560FB5C289`；关键字 `FILENAME 0x10`、`FILEIO 0x20`、`OP_END 0x40`、`CREATE 0x80`、`READ 0x100`、`WRITE 0x200`、`DELETE_PATH 0x400`、`RENAME_SETLINK_PATH 0x800`、`CREATE_NEW_FILE 0x1000`；只有 Informational 级别 | 本机 `logman query providers Microsoft-Windows-Kernel-File`（Windows 11 25H2，build 26200.9711） |
| Kernel-File 事件和字段 | 10 NameCreate / 11 NameDelete：`FileKey, FileName`。16 Write：v0 `ByteOffset, Irp, ThreadId(指针), FileObject, FileKey, IOSize(UInt32), IOFlags`；v1 `ByteOffset, Irp, FileObject, FileKey, IssuingThreadId(UInt32), IOSize, IOFlags, ExtraFlags`。26 DeletePath、27 RenamePath、28 SetLinkPath：v0 `Irp, ThreadId, FileObject, FileKey, ExtraInformation, InfoClass, FilePath`，v1 把 ThreadId 换成 `IssuingThreadId(UInt32)` 放在 ExtraInformation 之后。30 CreateNewFile：v0 `Irp, ThreadId, FileObject, CreateOptions, CreateAttributes, ShareAccess, FileName`，v1 同样把线程号换成 UInt32。写事件有每次 I/O 的字节数（IOSize）和偏移，没有文件名，要靠 FileKey 对到 NameCreate | 本机 `Get-WinEvent -ListProvider Microsoft-Windows-Kernel-File` 的事件模板 |
| 写事件的关键字 | Write 的关键字是 `WRITE | FILEIO`（事件 16 的 keywords `0x8000000000000220`）。按清单和"关键字任一匹配"的规则，只开 `WRITE` 就应该能收到，不需要开 `FILEIO`（`FILEIO` 会带上 Create、Close、Cleanup、Read 等所有文件操作）；运行时是否如此由 V14 确认 | 同上 |
| Kernel-Process 提供程序 | GUID `22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716`；`WINEVENT_KEYWORD_PROCESS 0x10`。字段按顺序：<br>1 ProcessStart v0 `ProcessID(UInt32), CreateTime(FILETIME), ParentProcessID(UInt32), SessionID(UInt32), ImageName(UnicodeString)`；v1 在 ImageName 前加 `Flags(UInt32)`；v2 在 v1 的 ImageName 后加 `ImageChecksum(UInt32), TimeDateStamp(UInt32), PackageFullName(UnicodeString), PackageRelativeAppId(UnicodeString)`；v3 `ProcessID, ProcessSequenceNumber(UInt64), CreateTime, ParentProcessID, ParentProcessSequenceNumber(UInt64), SessionID, Flags, ProcessTokenElevationType(UInt32), ProcessTokenIsElevated(UInt32), MandatoryLabel(SID), ImageName, ImageChecksum, TimeDateStamp, PackageFullName, PackageRelativeAppId`；v4 在 v3 末尾加 `SecurityMitigations(UInt32)`。<br>2 ProcessStop v0 `ProcessID, CreateTime, ExitTime(FILETIME), ExitCode(UInt32), TokenElevationType(UInt32), HandleCount(UInt32), CommitCharge(UInt64), CommitPeak(UInt64), ImageName(AnsiString)`；v1 在 CommitPeak 后加 `CPUCycleCount(UInt64), ReadOperationCount, WriteOperationCount, ReadTransferKiloBytes, WriteTransferKiloBytes, HardFaultCount(UInt32)`；v2 在 ProcessID 后插入 `ProcessSequenceNumber(UInt64)`，其余同 v1。<br>15 ProcessRundown：v0 的字段同 ProcessStart v2，v1 同 v3，v2 同 v4 | 本机 `Get-WinEvent -ListProvider Microsoft-Windows-Kernel-Process`（本次修订时重新导出） |
| CreateOptions 的标志、SID 布局 | `FILE_DIRECTORY_FILE = 0x00000001`、`FILE_DELETE_ON_CLOSE = 0x00001000`（`um\winternl.h`）；`_SID` = Revision(1 字节) + SubAuthorityCount(1) + IdentifierAuthority(6) + SubAuthority[n](4×n)（`um\winnt.h`） | Windows SDK 10.0.26100.0 |
| TraceEvent 最新稳定版 | 4.0.0，2026-10-15 发布（前一个是 3.2.8，2026-09-29）；MIT；只有 `lib/netstandard2.0`；依赖 Microsoft.Win32.Registry 5.0.0、System.Collections.Immutable 10.0.7、System.Reflection.Metadata 9.0.8、System.Reflection.TypeExtensions 4.7.0、System.Runtime.CompilerServices.Unsafe 6.1.2、Microsoft.Diagnostics.NETCore.Client 0.2.661903、System.Text.Json 10.0.3 | `https://api.nuget.org/v3-flatcontainer/microsoft.diagnostics.tracing.traceevent/index.json`、registration 页、4.0.0 的 nuspec（经代理 127.0.0.1:10808） |
| 4.0.0 的变化 | "PerfView and TraceEvent 4.0.0 require Windows 10+" | GitHub release `microsoft/perfview` v4.0.0 |
| 包里的原生文件 | `build/native/{amd64,arm64,x86}/KernelTraceControl.dll`（amd64 266,696 字节）和 `msdia140.dll`（amd64 2,255,920 字节）；`lib` 下有 TraceEvent.dll 3,327,800 字节、FastSerialization.dll 77 KB、Dia2Lib.dll、TraceReloggerLib.dll。props 把原生文件作为 `None` 项复制到输出目录的 `amd64\` 等子目录：架构为 msil 时复制全部三种架构，amd64 且 RID 为 `win-*` 时只复制 amd64；所以 `-r win-x64` 的发布本来就只带 `amd64\`，`x86\`、`arm64\` 只出现在不带 RID 的构建（开发和测试输出）里（按 props 的条件推断，2a.4 以实际发布核对）。`Dia2Lib.dll`、`TraceReloggerLib.dll` 除了是 lib 下的程序集，props 里也作为 `None` 项复制到输出根目录（Link 就是文件名）。`build/` 和 `buildTransitive/` 下有同一份 props（都是 4,467 字节），所以通过项目引用间接拿到这个包的项目（App、测试项目）也会导入这些 `None` 项 | 4.0.0 的 nupkg 内容（两轮评审都核对过） |
| TraceEvent 的会话 API | `TraceEventSession(name)` 默认选项 `Create`：同名会话已存在时先停掉再重建；`StopOnDispose` 默认 true（Dispose 或终结时停止）；`EventsLost` 对实时会话是实时值（`ETWTraceEventSource.EventsLost` 不是）；`GetActiveSessionNames()`；`BufferSizeMB` 只能在启用提供程序之前设；`TraceEventProviderOptions.EventIDsToEnable` 是"在关键字之外额外启用"，不是过滤；有 `Attach` 选项和 `CaptureState` | 4.0.0 包里的 `Microsoft.Diagnostics.Tracing.TraceEvent.xml` |
| 原始载荷访问 | `TraceEvent.DataStart`（IntPtr）、`EventDataLength`、`PointerSize`、`ID`、`Version`、`ProviderGuid`、`ProcessID`、`ThreadID`、`TimeStamp`、`TraceEventDispatcher.AllEvents` 都是公开的 | 同上 |
| 会话缓冲区的内存 | 有内核模式提供程序（Kernel-File 就是）写入的会话不能用 `EVENT_TRACE_USE_PAGED_MEMORY`，缓冲区来自非分页池，不算进消费进程的工作集和私有字节 | Microsoft Learn "Logging Mode Constants"（评审时核对） |
| 普通权限能否列出会话 | 普通权限下 `logman query -ets` 能列出所有会话（停止会话仍需要管理员） | 本机实测（评审时又以普通权限跑过一次） |
| 计划任务的默认设置 | `New-ScheduledTaskSettingsSet` 的默认值：`DisallowStartIfOnBatteries=True`、`StopIfGoingOnBatteries=True`、`StartWhenAvailable=False`、`ExecutionTimeLimit=PT72H`、`MultipleInstances=IgnoreNew`。兜底任务的 XML 必须显式写出这些设置（第 5.1.4 节） | 本机（本次修订时重新运行） |
| 现有界面和脚本 | `ItemList` 一个单元格只在文字为空时画标签，读屏文字也只在这时换成标签；`ListItem.Note` 只进工具提示，行是单行绘制。`DetailPanel` 只在有归属证据或规则证据时建"证据"标题；`ExplorerPage` 第 431 行调用 `ShowNode`。`GrowthHotspot` 有 `IsDirectory`；规则里已有 `vm-disk` 类别（`gen.vm-disk`、`dev.wsl.distro`、`dev.wsl.distro-store`）。`build-release.ps1` 统计文件数时只数顶层文件（不递归）。方块图用 `ToolTip.SetToolTip(this, 路径)` 显示提示，读屏说明在 `TreemapAccessible.Description` | 代码（main `cd9ba3a`） |
| WinForms 的 HelpText | `System.Windows.Forms` 10.0.12 里 `AccessibleObject` 的 UIA 属性 `UIA_HelpTextPropertyId` 取 `Help ?? ""`；`ControlAccessibleObject.Help` 只在有 `QueryAccessibilityHelp` 事件时有值。方块图的 `Help` 为空、测试平时却能读到 `Description` 里的"方向键"，说明这里的 HelpText 走的是系统的 MSAA 代理，具体映射要靠复现确认（第 5.12 节） | ILSpy（`ilspycmd`）反编译本机的 System.Windows.Forms.dll |

### 2.2 只能实测确认的问题（2a.1 逐条核对，结果写进报告）

- V1 Kernel-File 事件头里的进程号：新建、删除、重命名和非分页的写入是不是发起操作的进程。
- V2 缓存写入的回写：延迟写和映射文件的回写是不是以 System（4）或其他进程的身份再出现一次写事件，`IOFlags` 里的分页 I/O 标志（WDK `wdm.h` 的 `IRP_PAGING_IO` 等，本机没有 WDK，取值要实测核对）能不能区分；分页写入的 IOSize 是不是按页取整、会不会超过文件的实际末尾。设计按"写入量只统计非分页写入""有非分页写入的文件不用分页写入算估算增长"做（第 5.6 节），这一条决定它成不成立。
- V3 NameCreate 的覆盖：监控开始前就已打开的文件，它的写入能不能通过 FileKey 对上文件名；对不上的写入字节占多少。
- V4 路径格式：FileName / FilePath 和 ProcessStart 的 ImageName 是不是 `\Device\HarddiskVolumeN\…` 形式；RenamePath 的 FilePath 是新名字还是旧名字。
- V5 各系统上的事件版本（本机、Server 2022、Server 2025；后两个从冒烟测试的报告里看）。
- V6 消费跟不上时 `TraceEventSession.EventsLost` 是否按预期增长（用 1 MB 的小缓冲区加回调里睡眠制造丢失，不增加负载）。
- V7 只用实时会话和这两个清单式提供程序时，是否需要 `KernelTraceControl.dll`、`msdia140.dll`、`Dia2Lib.dll`、`TraceReloggerLib.dll`（删掉后能否正常开关会话和收事件）。
- V8 单文件自包含版里 TraceEvent 能否工作（它是否依赖 `Assembly.Location` 找原生 DLL）。
- V9 消费进程被强制结束后会话是否残留，`logman stop <名字> -ets` 能否清掉。
- V10 CreateNewFile 对"覆盖已有文件"（CREATE_ALWAYS）是否触发；对目录是否带 `FILE_DIRECTORY_FILE`。
- V11 `TreatWarningsAsErrors` 下引入 TraceEvent 的传递依赖会不会触发 NuGet 安全审计警告（NU190x）。
- V12 关闭时删除：用 `FILE_DELETE_ON_CLOSE` 创建的文件（CreateNewFile 的 CreateOptions 带 `0x00001000`）、`NtDeleteFile`、POSIX 语义的删除会不会产生 DeletePath。不会时启用第 5.6 节的"关闭时删除"规则。
- V13 事件路径里会不会出现 8.3 短名（例如 `RUNNER~1` 这种 TEMP 路径）。会的话按第 5.6 节换成长路径。
- V14 只开 `WRITE`（0x200，不开 `FILEIO`）时，运行时是否真的收到 Write 事件。
- V15 NameDelete 的语义：只在删除时触发，还是文件关闭、名字缓存拆除时也触发；同一个会话里 FileKey 会不会被复用到别的文件。
- V16 追加写（`FILE_APPEND_DATA`、`>>`）的 ByteOffset 是真实偏移还是哨兵值（`0xFFFFFFFFFFFFFFFF` = 写到末尾、`0xFFFFFFFFFFFFFFFE` = 用文件指针）；只用 `SetEndOfFile` / 预分配加长、没有写入的文件有没有写事件。
- V17 对正在运行的会话再次调用 `EnableProvider` 换关键字是否立即生效（D9 的降级依赖这一条）。
- V18 TraceEvent 的 `BufferSizeMB` 指总大小还是单个缓冲区的大小；实际的缓冲区大小和个数（用 `ControlTraceW(EVENT_TRACE_CONTROL_QUERY)` 查）。
- V19 可选：`TraceEventSession.CaptureState` 对 Kernel-Process 是否发出 ProcessRundown（含受保护进程的完整映像路径、父进程号和序号），对 Kernel-File 是否补发已打开文件的 NameCreate（改善 V3）。
- V20 兜底任务的 XML（UTF-16、SYSTEM、带 `EndBoundary` 和 `DeleteExpiredTaskAfter`）用 `schtasks /Create /XML` 能否注册，过期后是否自动删除（2a.4 用 2 分钟的过期时间单独测一次）。
- V21 普通权限下 `TraceEventSession.GetActiveSessionNames()` 能否列出会话（`logman query -ets` 能）。

## 3. 原型核对

### 3.1 原型里已有的写入监控设计

| 位置 | 内容 | 现在的实现 |
|---|---|---|
| 工具栏（第 331 行） | 胶囊"写入监控：关闭 · 开启诊断"，点开对话框 | 没有 |
| 左侧导航"原型：对话框"组 | "开启写入监控"（只是原型导航） | 不需要 |
| 概览"建议先处理"卡片底部（第 442 行） | 提示"写入监控未开启，增长只能按快照对比，看不到是哪个程序写的。开启 4 小时诊断" | 没有 |
| 概览"增长最快"（第 468 行） | 第三列"写入者"：有监控时是进程（实测），没有时是规则类别加"未监控时段" | 第三列是"归属"，标签"未识别 / 已不存在" |
| 空间浏览详情的证据（第 601 行） | "ETW　最近一次诊断中，uv.exe 在这里新建了 8,921 个文件；上级进程：Code.exe → pwsh.exe" | 没有 ETW 这一条 |
| 增长分析"写入监控覆盖"卡片（第 649 行） | "14 / 168 小时"，脚注"开启常驻监控可覆盖全天" | "未开启"和"后续版本"的说明 |
| 增长分析"写入监控覆盖时段"卡片（第 671 行） | 时间轴、日期刻度、三行会话（时段、"诊断 · 轻量模式"、时长）、说明、"开启诊断"按钮 | 说明文字和置灰的"开启诊断（后续版本）" |
| 增长分析"变化最大的目录"（第 691 行） | 列：目录、变化、当前、写入进程、归属判断；写入进程的例子有"未监控时段""vmmem（WSL2 虚拟机）""svchost.exe（服务 wuauserv）""uv.exe ← pwsh.exe ← Code.exe""多个进程" | 列：目录、变化、当前、归属、写入进程；写入进程为空，标签"未监控" |
| 增长分析"写入进程排行"（第 707 行） | 列：进程、产品 · 签名、上级应用、新建文件、写入量、主要目录；标题注"仅统计有监控的 14 小时" | 没有 |
| 设置"写入监控（ETW）"（第 951 行） | 三档：关闭、临时诊断（推荐）、常驻服务；记录模式轻量 / 精确和说明 | 三项都置灰，固定"关闭" |
| 对话框"开启写入监控（临时诊断）"（第 1010 行） | 说明、时长 1 / 4 / 8 / 24 小时、模式轻量（推荐）/ 精确和说明、三条注意事项、取消 / 以管理员身份开始 | 没有 |
| 状态栏（第 1112 行） | "写入监控：关闭" | 没有 |

### 3.2 原型里缺的

- 监控进行中的样子：工具栏、状态栏、覆盖卡片里的"进行中"和"停止监控"。
- 普通权限和管理员两种对话框按钮；"监控期间要保持窗口打开"；另一个窗口正在监控；用另一个管理员账户提权；兜底计划任务的说明（D7）。
- 丢失率、抽样（"≈"）、轻量模式没有字节数时的显示；"监控过但没有写入"；上次会话没有正常结束。
- 设置里的默认时长、保留天数和 monitor.db 占用。
- 进程链方向不统一：增长分析用"uv.exe ← pwsh.exe ← Code.exe"（写入进程在左），详情面板用"Code.exe → pwsh.exe"。
- "产品 · 签名"：签名要读整个文件，排行里逐个检查太慢；版本信息里的公司名可以直接拿到。
- 系统写入（进程号 0 和 4）怎么显示。

### 3.3 原型需要改的内容（下一步按这里改）

- P1 工具栏胶囊：保留"写入监控：关闭 · 开启诊断"；在"原型说明：概览"里加四种状态的文字："写入监控：关闭 · 开启诊断"；"写入监控：诊断中（精确）· 剩余 3 小时 12 分 · 停止"（圆点绿色）；"写入监控：另一个窗口正在监控"（普通权限下检测到有实例在监控时）；"写入监控：上次没有正常结束 · 清理"（只有残留会话时）。
- P2 状态栏：保留"写入监控：关闭"；原型说明里加"诊断中（精确）· 剩余 3:12 · 丢失 0.0%"。
- P3 概览：把"写入监控未开启……开启 4 小时诊断"提示从"建议先处理"卡片移到"增长最快"卡片底部（它讲的是增长）；"增长最快"的"写入者"列保留，例子改成三种情况：有监控写入的行"uv.exe ← Code.exe"加"实测"标签；没有监控、归属已知的行"崩溃转储"加"未监控"标签；归属也不知道的行只有"未识别"标签。
- P4 空间浏览详情：ETW 证据的文字改成"最近一次诊断（10-19 08:00–10:00，精确）中，uv.exe 在这里新建了 8,921 个文件，估算增长 1.20 GB；进程链：uv.exe ← pwsh.exe ← Code.exe"；原型说明里加另一种："近 30 天 3 次诊断（共 14 小时）里没有程序在这里写入"。全原型统一用"写入进程 ← 上级 ← 顶层"的方向。
- P5 增长分析：
  - "写入监控覆盖"卡片脚注"开启常驻监控可覆盖全天"改成"丢失 0.2% · 开启诊断"（常驻在 2b）。
  - 覆盖时段卡片的会话表加第四列"丢失率"（"丢失 0.1%"）；加一行进行中的例子"10-19 08:00 – 进行中 · 诊断 · 精确 · 剩余 2 小时 · 丢失 0.0%"，这时按钮是"停止监控"。
  - 变化最大的目录：列顺序改成和实现一致：目录、变化、当前、归属、写入进程；写入进程列是"文字 + 标签"同一格（"uv.exe ← pwsh.exe ← Code.exe 实测"），没有监控的是"未监控"标签；"多个进程"改成"node.exe 等 3 个进程"；加一行"监控的 14 小时里没有写入"；加一个抽样的例子"≈ uv.exe ← pwsh.exe ← Code.exe"；docker_data.vhdx 这一行的写入进程改成"vmmem（虚拟磁盘，看不到内部是谁写的）实测"，归属列只写"Docker Desktop"（实现里一格只画一个标签，原来归属列的"虚拟磁盘内部不可见"标签改成写入进程列里的文字；规则里已有 `vm-disk` 类别，实现做得到）。
  - 写入进程排行："产品 · 签名"改成"产品 · 公司"；"上级应用"按 D1 的规则写顶层应用（uv 行"Code.exe（Visual Studio Code）"，node 行"claude.exe（Claude Code）"，codex 行和 msedge 行"—"表示自己就是顶层）；写入量列加一个轻量模式的例子"—"；加一行"系统（缓存回写或内核）"（产品和上级应用为"—"，主要目录"多个目录"）；标题注改成"仅统计有监控的 14 小时 · 轻量模式的 12 小时没有写入量"。
  - 原型说明加三条：丢失率和"≈"的含义；轻量和精确的区别；"写入量"和"估算增长"的区别（第 5.6 节）。
- P6 设置"写入监控（ETW）"：
  - 关闭："不提示开启诊断。看得到哪里变了，看不到是谁写的。"
  - 临时诊断（推荐）："需要时手动开启 1 到 24 小时，到时自动停止。需要管理员权限，不安装后台服务。"
  - 常驻服务："（后续版本）"，置灰。
  - 记录模式的说明改成："轻量：只记录新建、删除、重命名，看不到写了多少。精确：额外记录每次写入，能估算每个程序让目录涨了多少；编译、npm install 时开销更大。"
  - 新增两行："默认时长"（1 / 4 / 8 / 24 小时）；"保留"（30 / 90 / 180 天）加"当前占用 1.2 MB"。
- P7 开启写入监控对话框：
  - 说明改成："监控期间会记录哪个程序在本机磁盘上新建、删除、重命名了文件（精确模式还记录写了多少），结束后在"增长分析"和"空间浏览"里查看。"
  - 注意事项改成四条："需要管理员权限。现在是普通权限：点下面的按钮后 DiskThinner 会以管理员身份重新打开、重新扫描，然后开始监控。""监控期间请保持 DiskThinner 打开（可以最小化）；到时间或关闭窗口都会停止。""数据只保存在本机，按目录和程序汇总，不记录文件名和文件内容，不联网，不修改任何文件。""为了在程序被强制结束时也能停下来，会临时添加一个计划任务，监控结束后删除。"（最后一条取决于 D7）
  - 按钮：普通权限"以管理员身份重启并开始"，管理员"开始监控"。
  - 原型说明里列出另外四种状态：另一个窗口正在监控（"另一个 DiskThinner 窗口正在监控，结束后再开启"，开始按钮置灰）；用另一个管理员账户提权（黄色提示"你用 X 账户提权，监控数据会保存在 X 的数据目录下，用自己的账户打开 DiskThinner 时看不到"）；普通权限下点了"以管理员身份重启并开始"、新窗口发现账户不一致时（对话框重新打开，时长和模式保留，显示同样的黄色提示，按钮"开始监控"）；设置为"关闭"时不出现提示入口。
- P8 停止监控的确认（原型说明里写一句即可，实现用 MessageBox）："停止写入监控？已经记录的数据会保留。"按钮"停止""继续监控"。

## 4. 总体设计

### 4.1 工程结构和技术栈（设计批准后锁定）

| 项目 | 内容 | 依赖 | AOT |
|---|---|---|---|
| `src/DiskThinner.Monitor`（新） | 事件模型、原始载荷解析、设备路径换算和短名换长名、进程表和归属、`DirectoryTable`、`FileGrowthTracker`（估算增长）、按小时汇总、`WriteTreeFolder`（合并）、抽样和丢失统计、`MonitorLock`（专用线程持有的全局互斥量）、`SessionLiveness`、`SessionDeadline`、`WatchdogTask`（兜底任务的 XML 和 schtasks 调用）、`KeyValueReport`、`MonitorStore`（monitor.db）、查询用的 `WriteIndex`；Windows API（Toolhelp32、QueryFullProcessImageName、GetProcessTimes、EnumServicesStatusEx、QueryDosDevice、GetLongPathNameW）都在接口后面 | Core、Inventory（`ExeIdentityReader` 取产品名）、Microsoft.Data.Sqlite | `IsAotCompatible=true` |
| `src/DiskThinner.Monitor.Etw`（新） | `DiagnosticSession`：会话命名、开关会话、事件泵线程、每秒的统计和截止检查、TraceEvent 事件转成 `RawEvent`、差分核对；`MonitorRunner`：会话 + 汇总 + 定时落盘 + 状态快照，界面和 `monitor` 子命令共用 | Monitor、Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0 | 否（项目文件里写明原因） |
| `src/DiskThinner.App` | `Monitor/MonitorHost.cs`（主窗口持有，管界面状态和刷新）、`Dialogs/MonitorDialog.cs`、`Commands/MonitorCommand.cs`、各页面接入；`ItemList` 加标签列 | 上面两个 | 否（不变） |
| `spikes/EtwBench`（新，进 `DiskThinner.slnx` 的 `/spikes/`，和现有 spike 一样由 CI 构建，保证能编译） | 2a.1 的实测程序：只开会话、数事件、测 CPU、内存和会话缓冲区、回放，核对 V1–V19、V21 | TraceEvent、Monitor 的解析部分（2a.2 之前先放在 spike 里，2a.2 移进 Monitor） | 否 |
| `tests/DiskThinner.Monitor.Tests`（新） | 纯逻辑测试 + `EtwFact` 提权测试 | Monitor、Monitor.Etw | |
| `tests/DiskThinner.WriteLoad`（新，进解决方案的 `/tests/`，和 `DiskThinner.LockHolder` 一样） | 测试写入负载（第 8.2 节） | 无 | |
| 仓库根目录 `Directory.Build.targets`（新） | 去掉 TraceEvent 带进来的不需要的原生文件（第 5.11 节） | | |
| `tools/verify/export-etw-templates.ps1`、`tools/verify/phase2a-elevated.ps1`（新） | 导出事件模板 fixture（普通权限）；提权的实测和验收 | | |

拆成两个项目是对 2.2 节结构的补充（2.2 只列了 `DiskThinner.Monitor`），见决策点 D11。

TraceEvent 加进 `Directory.Packages.props`：`<PackageVersion Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="4.0.0" />`，注释写明"只在 Monitor.Etw 和 spikes/EtwBench 里使用，2b 改为直接调用 ETW API"。如果 V11 出现传递依赖的审计警告，在 Directory.Packages.props 里给那个包加固定版本的 `PackageVersion` 并开启 `CentralPackageTransitivePinningEnabled`，不用 `NoWarn` 压掉。

### 4.2 数据流

```
Kernel-File / Kernel-Process ──ETW 实时会话──▶ Monitor.Etw 事件泵线程（DiagnosticSession）
   RawEvent（提供程序、事件号、版本、进程号、时间、载荷）
        ▼  PayloadParser（已知版本按字节解析；未知版本退回 PayloadByName；--verify-parser 时抽样差分核对）
   MonitorEvent ──▶ 过滤（自己的进程、非本地卷）──▶ 路径换算（设备前缀 → 盘符，8.3 短名 → 长名）──▶ DirectoryTable（DirId）
        ▼
   ProcessTable（进程链、服务名）──▶ ChainId（写入进程、上级、顶层应用）
        ▼
   WriteSampler（只对写事件）──▶ FileGrowthTracker（fileKeyToName、fileState：估算增长）
        ▼
   HourAggregator（每小时：(DirId, ChainId) → 计数器）
        ▼ 每 5 分钟、整点、停止时（MonitorRunner）
   WriteTreeFolder.Fold（在副本上三段合并，只输出行）──▶ MonitorStore（monitor.db）
                                                                  ▼
   界面：WriteIndex.Load(时间范围) ──▶ 增长分析 / 概览 / 空间浏览详情
```

## 5. 方案

### 5.1 会话的生命周期

#### 5.1.1 命名和互斥

- 会话名 `DiskThinner-Diag-<开始时间 UTC，yyyyMMddHHmmss>`，判断"是不是我们的"只用正则 `^DiskThinner-Diag-\d{14}$`。每次用新名字，兜底任务（D7）只会停它自己那一个会话，不会误停后来新开的。开始前如果活动会话里已经有同名的（同一秒内连续开始），等到下一秒再取名，不让 `TraceEventSessionOptions.Create` 把它停掉重建。2b 的服务用另一个前缀。
- 全局互斥量 `Global\DiskThinner.Monitor`，封装成 `MonitorLock`（Monitor 项目）。`Mutex` 有线程归属，只有获取它的线程能 `ReleaseMutex`，而停止可能发生在计时器线程、泵线程、界面线程或 `monitor` 子命令的主线程上。所以互斥量只在 `MonitorLock` 自己的一个专用后台线程上获取和释放，其他线程只发信号：

```csharp
public sealed class MonitorLock : IDisposable
{
    public const string DefaultName = @"Global\DiskThinner.Monitor";
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _owner;
    private int _disposed;

    // 拿不到（另一个实例在监控）返回 null。名字可以注入，单元测试用 Local\DiskThinner.Test.<guid>。
    public static MonitorLock? TryAcquire(string name = DefaultName)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var l = new MonitorLock(name, acquired);
        if (acquired.Task.Wait(TimeSpan.FromSeconds(5)) && acquired.Task.Result)
        {
            return l;
        }

        l.Dispose(); // 超时或拿不到：发释放信号，专用线程万一稍后拿到也会立即释放
        return null;
    }

    private MonitorLock(string name, TaskCompletionSource<bool> acquired)
    {
        _owner = new Thread(() =>
        {
            try
            {
                using var m = new Mutex(false, name);
                bool owned;
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; } // 上一个持有者没释放就退出了，也算拿到
                acquired.TrySetResult(owned);
                if (!owned) return;
                _release.Wait();
                m.ReleaseMutex(); // 同一个线程释放
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                acquired.TrySetResult(false); // 对象存在但打不开（普通权限遇到管理员建的），当作拿不到
            }
        }) { IsBackground = true, Name = "DiskThinner.Monitor.Lock" };
        _owner.Start();
    }

    /// <summary>任何线程都可以调用，只生效一次。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _owner.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>别的进程是否开着这个互斥量（第 5.1.2 节）。</summary>
    public static bool ExistsElsewhere(string name = DefaultName); // TryOpenExisting 为 true（句柄立即关闭）或抛 UnauthorizedAccessException
}
```

- 进程退出时专用线程（后台线程）随之结束，系统释放互斥量，下一个获取者得到 `AbandonedMutexException`，按"拿到"处理。
- `DiagnosticSession` 在开始时持有一个 `MonitorLock`。`Stop(reason)` 用 `Interlocked.Exchange` 保证只执行一次，顺序是：停 ETW 会话 → 等泵线程结束（当前线程就是泵线程时不等）→ 最后一次落盘（`MonitorRunner`，最多 3 秒）→ 删兜底任务 → `MonitorLock.Dispose()`。每一步的异常（`COMException`、`UnauthorizedAccessException`、`InvalidOperationException`、`SqliteException`、`IOException`）单独捕获并记日志，后面的步骤照常执行；锁在 `finally` 里释放。
- TraceEventSession 用 `TraceEventSessionOptions.Create`（默认），`StopOnDispose = true`（默认）。

#### 5.1.2 残留会话和未结束的会话记录

纯函数 `SessionLiveness.Classify(selfMonitoring, lockExistsElsewhere, activeNames, openRows)`（Monitor，单元测试覆盖各种组合）。输入：

- `lockExistsElsewhere`：`MonitorLock.ExistsElsewhere()`。`Mutex.TryOpenExisting` 返回 true，或者抛 `UnauthorizedAccessException`（对象存在，只是普通权限打不开管理员建的对象），都算存在。
- `activeNames`：`TraceEventSession.GetActiveSessionNames()` 里符合正则的名字；列不出来时为 null（V21）。
- `openRows`：monitor.db 里 `ended_at IS NULL` 的会话行。

输出：

- `Live`：自己在监控，或者 `lockExistsElsewhere`。
- `Residual`：不是 Live 时，`activeNames` 里的全部会话（要停掉）；是 Live 时为空（那个会话属于正在监控的实例）。
- `Orphans`（要标"程序意外退出"的行）：不是 Live 时是全部 `openRows`（没有实例在监控，就不会再有人写这些行，包括残留会话的那一行）；是 Live 时只是名字不在 `activeNames` 里的行；Live 且 `activeNames` 为 null 时为空（判断不了就不动）。自己正在监控的那一行永远不在里面。

时机和动作：

1. 每次启动（任何权限，`MainForm.OnShown` 之后在后台线程）、`monitor --cleanup`、开始监控前，各算一次。
2. Orphans：`UPDATE session SET ended_at = last_flush, end_reason = 'crashed' WHERE id = ? AND ended_at IS NULL`。只改 monitor.db，任何权限都做。
3. Residual（只在管理员下）：在一个同步方法里 `MonitorLock.TryAcquire()`，拿到才逐个 `new TraceEventSession(name, TraceEventSessionOptions.Attach).Stop()`、记日志、删残留的兜底任务，然后释放锁；拿不到（刚好有别的实例开始了监控）就什么都不做。
4. 普通权限下的显示：Live 时工具栏显示"写入监控：另一个窗口正在监控"（P1），对话框里开始按钮置灰；不是 Live 而 Residual 不为空时状态栏显示"写入监控：上次没有正常结束，以管理员身份打开后会自动清理"，不弹框；`activeNames` 为 null 时不显示残留提示。

#### 5.1.3 一定停止

| 情况 | 怎么停 |
|---|---|
| 到时 | `DiagnosticSession` 的每秒统计计时器（`System.Threading.Timer`，1 秒，本来就要每秒读 `EventsLost`）同时调用 `SessionDeadline.ShouldStop()`：墙钟（UTC）到了计划结束时间，或者 `Stopwatch` 经过的时间到了时长，任一满足就 `Stop("timeout")`。睡眠和休眠期间计时器不走，唤醒后第一次触发（1 秒内）按墙钟发现已经过期，立即停止；用户把系统时间往回调时由经过时间兜底 |
| 用户点"停止监控" | 确认后 `Stop("user")` |
| 关闭窗口、注销、关机、以管理员身份重启 | `MainForm.OnFormClosing` 调 `MonitorHost.Stop("app-exit")`：停止会话、最后一次落盘，最多等 3 秒，超时就不等落盘（会话一定先停） |
| 未处理异常 | `Program.cs` 的 `AppDomain.UnhandledException` 和新加的 `AppDomain.ProcessExit` 里调 `DiagnosticSession.EmergencyStop()`：只按名字停会话，不落盘、不加锁、吞掉异常；会话已经停了时什么都不做 |
| 事件泵线程出错 | 泵线程顶层捕获后 `Stop("error")`（Stop 发现当前线程是泵线程时不等它结束），写崩溃报告（`AppLog.Crash`），状态栏显示"写入监控因错误停止" |
| 进程被强制结束、断电 | 断电和重启后会话自然消失，会话行在下次启动时按第 5.1.2 节标出；强制结束时会话残留，由第 5.1.2 节在下次管理员启动时清理，D7 的兜底任务在计划结束 5 分钟后停掉它（机器那时在睡眠的话醒来后补跑） |

- 计时器回调整体用 try/catch 包住，不把异常抛到线程池（线程池上的未处理异常会让进程退出）；连续 3 次出错时 `Stop("error")`。
- `DiagnosticSession` 在构造时就把时长限制在 10 秒到 24 小时（超出抛 `ArgumentOutOfRangeException`），界面和命令行都绕不过去。
- `SessionDeadline` 是纯类，墙钟（`Func<DateTime>`）和经过时间（`Func<TimeSpan>`）可以注入。`SessionDeadlineTests` 用假时钟验证"墙钟跳过 30 小时后停止""墙钟往回调 2 小时仍按经过时间停止""都没到时不停"。
- 因为截止检查每秒都做，`MonitorHost` 不需要订阅 `SystemEvents.PowerModeChanged`。

#### 5.1.4 兜底计划任务（D7，推荐采用）

开始监控时注册一次性任务 `\DiskThinner\StopDiagnostic`（一次只有一个会话，所以只用一个任务名，`/F` 覆盖上一次的），以 SYSTEM 运行，在计划结束时间加 5 分钟执行 `logman.exe stop <这次的会话名> -ets`。XML 由 `WatchdogTask.BuildXml(sessionName, triggerAt, expireAfter, logmanPath)`（Monitor，纯函数）用 `XmlWriter` 生成；会话名先过正则，不符合抛 `ArgumentException`：

```xml
<?xml version="1.0" encoding="utf-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>（L.T 的说明文字：程序被强制结束时停止写入监控会话）</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>2026-10-26T18:05:00+08:00</StartBoundary>
      <EndBoundary>2026-10-27T18:05:00+08:00</EndBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>C:\Windows\System32\logman.exe</Command><Arguments>stop DiskThinner-Diag-20261026100000 -ets</Arguments></Exec>
  </Actions>
</Task>
```

- 时间用 `DateTimeOffset` 的本地时间加偏移（`yyyy-MM-ddTHH:mm:sszzz`），不受区域日期格式影响。`EndBoundary` = 触发时间 + `expireAfter`（正式用 1 天），配合 `DeleteExpiredTaskAfter` 让任务过期后自己删除，DiskThinner 一直不再打开时也不留任务。V20 核对注册和自动删除；带这两项注册失败时去掉它们再注册一次。
- `StartWhenAvailable=true`：触发时机器在睡眠或关机，醒来后补跑。电池两项为 false：笔记本用电池时也运行。`ExecutionTimeLimit` 5 分钟。
- `logman.exe` 用 `Environment.SystemDirectory` 拼出的完整路径，转义交给 `XmlWriter`。
- 文件编码：UTF-16 LE 带 BOM（`new UnicodeEncoding(bigEndian: false, byteOrderMark: true)`），和 XML 声明一致，`schtasks /Query /XML` 导出的也是这种格式。写在 `%TEMP%\DiskThinner-task-<guid>.xml`，注册完立即删除。
- 注册：`schtasks /Create /TN \DiskThinner\StopDiagnostic /XML <文件> /F`；正常停止时 `schtasks /Delete /TN … /F`；残留清理时也删。沿用 `ScheduledSnapshot` 的做法：System32 下 schtasks 的完整路径，`ArgumentList` 逐个传参，不经过命令行解释器，15 秒超时。
- 注册失败不阻止监控，只记警告，会话标"兜底任务注册失败"，状态里说明"程序被强制结束时会话可能残留到下次启动"。
- `WatchdogTaskTests` 断言上面每个设置元素都在、取值正确，时间带偏移，文件头是 UTF-16 LE 的 BOM，会话名不合法时拒绝。

#### 5.1.5 非管理员和账户不一致

- 普通权限下所有"开启诊断"入口都打开同一个对话框。主按钮"以管理员身份重启并开始"调用 `ShellActions.RestartElevated(drive, owner, extraArgs)`（给它加一个参数数组），追加 `--start-monitor <小时> --monitor-mode light|precise`。UAC 取消时返回 false，状态栏显示"已取消以管理员身份重启"，和现在空间浏览页的做法一致。
- 新实例在 `OnShown` 里先做第 5.1.2 节的判定和清理，然后：
  - `AccountInfo.Mismatch` 为 false：直接开始监控（用户已经确认过），然后照常扫描；
  - 为 true（UAC 里用了另一个管理员账户）：不自动开始，打开 `MonitorDialog`，时长和模式按参数预填，显示黄色账户提示（P7），用户点"开始监控"才开始；点"取消"不开始，状态栏显示"没有开始写入监控"。
- 管理员下直接打开对话框时，`Mismatch` 为 true 同样显示黄色提示，允许继续。

### 5.2 两种模式和开销实测

| 模式 | Kernel-File 关键字 | 能回答 |
|---|---|---|
| 轻量 | `CREATE_NEW_FILE | DELETE_PATH | RENAME_SETLINK_PATH` = `0x1C00` | 谁在哪个目录新建、删除、重命名了多少文件 |
| 精确 | 轻量 + `WRITE | FILENAME` = `0x1E10` | 再加写了多少字节、新文件的估算增长 |

两种模式都开 Kernel-Process 的 `0x10`。缓冲区初值：轻量 8 MB、精确 32 MB（按总大小设 `BufferSizeMB`；它到底指总大小还是单个缓冲区由 V18 确认）。2a.1 按丢失情况在 D3 的上限（轻量 16 MB、精确 32 MB）内调整后写死。

实测方法（2a.1，`tools/verify/phase2a-elevated.ps1 -Part bench`，一次 UAC）：

- 程序：`spikes/EtwBench`，三种配置（不开会话、轻量、精确）× 三种负载，每组跑 1 次；某一组的结果离 D3 的阈值不到 20% 时，对那一组再跑最多 2 次取中位数（总共不超过 3 轮）。
- 优先级和并发（第 8.4 节）：脚本开头把自己设成低于正常（`BelowNormal`），启动的 EtwBench、负载和编译都继承这个优先级；同一时间只有一个 EtwBench 和一个负载在跑。监控进程在低优先级下更容易丢事件，所以测出来的丢失率偏保守，报告里写明。
- 负载（都是正常使用会遇到的规模，不做加压）：
  - W1 空闲 10 分钟（正常桌面）；
  - W2 `cmd.exe /c DiskThinner.WriteLoad.exe` 默认参数（第 8.2 节：500 个 64 KiB 文件、一个 128 MiB 文件、重命名 50、删除 100、追加 50、重开追加 50、关闭时删除 50、预分配 20、短路径 20），共写约 170 MB，几秒钟；
  - W3 本仓库 `dotnet build -c Release --no-incremental -m:1 --disable-build-servers`（先 `dotnet restore`，不计入）：单个 MSBuild 节点，结束后不留编译服务进程。
- 记录：
  - 每种事件每秒的平均和峰值；
  - 监控进程的 CPU（`Process.TotalProcessorTime` 的差 ÷ 墙钟时间 ÷ 逻辑核数，同时记单核峰值）、工作集和私有字节的峰值（W1、W2、W3 都记）；
  - 会话缓冲区：`ControlTraceW(EVENT_TRACE_CONTROL_QUERY)` 读出的 BufferSize × NumberOfBuffers（V18），以及负载前后 `Win32_PerfFormattedData_PerfOS_Memory.PoolNonpagedBytes` 的变化（用 CIM 读，不受系统语言影响）；
  - `TraceEventSession.EventsLost` 和丢失率；负载耗时相对"不开会话"的变化；
  - W2 的逐项比对（同第 1.3 节第 7 条）、对不上文件名的写入占比（V3）；
  - 可持续处理速率：EtwBench 在 W3 期间把收到的原始事件（最多 200,000 个）复制到内存，负载结束后单线程回放 5 遍，经过 PayloadParser、ProcessTable、FileGrowthTracker、HourAggregator，记每秒处理的事件数（回放在 10 秒内结束）；
  - 可选：负载前后 `FSCTL_QUERY_USN_JOURNAL` 的 NextUsn 差（只读，估计 USN 记录量，给 2b 的 USN 轻量模式参考）。
- 2b 默认模式的判定规则（D3）：精确模式同时满足下面四条就推荐精确，否则推荐轻量。
  1. W1：监控进程 CPU 平均 ≤ 0.5%（整机），工作集增量 ≤ 64 MB；
  2. W2：丢失率 ≤ 0.1%，逐项比对通过（W2 只有几秒，耗时变化只记录，不参与判定）；
  3. W3：丢失率 ≤ 1%，编译耗时增加 ≤ 10%，监控进程峰值 ≤ 单核 50%；
  4. 会话缓冲区不超过上限（精确 32 MB）。
- 2a 自身的底线：任何模式在 W1 下 CPU ≤ 1%、进程内存增量 ≤ 64 MB、缓冲区不超过上限。达不到的模式在对话框里标"开销较大"并在报告里说明。
- 对话框的默认模式（D2）：原型是"轻量（推荐）"。推荐规则：实测满足上面四条时 2a 的对话框默认改成精确（诊断是临时的，用户要的就是"谁写了多少"），否则保持轻量。

### 5.3 自动抽样、降级和丢失率

- 只抽样写事件。新建、删除、重命名、文件名、进程事件永远全收（量小，而且计数和文件对应要准）。
- 每秒统计收到的写事件数 r。r > R（初值 20,000/秒，2a.1 改成实测可持续处理速率的一半）时倍数 k 翻倍（1、2、4……最大 64）；连续 10 秒 r < R/4 时减半。按到达顺序每 k 个处理一个。
- 抽样的口径（和第 5.6 节一致）：被跳过的写入完全不处理（不查文件名、不更新最大写入位置）；处理的那个写入，`write_bytes` 和 `write_ops` 乘以 k；估算增长不乘 k（它按最大写入位置算，乘了会放大）；有抽样的小时和行标"估算"。
- 降级（D9，推荐）：精确模式下 k 已经是 64、最近 60 秒丢失率仍 > 5% 时，重新调用 `EnableProvider` 只开轻量的关键字（V17 确认立即生效），会话记录写下降级时间，界面会话行显示"中途改为轻量（事件太多）"。
- 统计（每小时一份，会话一份）：
  - received：送到回调的事件数；
  - lost：`TraceEventSession.EventsLost` 每秒读一次的差值（内核侧丢的，分不出事件类型）；
  - sampled_out：抽样跳过的写事件数；
  - parse_errors、filtered（自己的进程、非本地卷、不认识的事件号）；
  - 在处理了的事件里再细分：unresolved_writes（对不上文件名的写入次数和字节）、bad_offsets（偏移不可用的写入）、short_names_unresolved（短名换不成长名的路径）；
  - parse_mismatch：只在 `--verify-parser` 时有（第 5.4 节）；
  - 丢失率 = lost ÷ (received + lost)。抽样和对不上文件名单独显示，不算丢失。
- 显示：覆盖卡片脚注"丢失 0.2%"（> 1% 时用 WarnText 颜色并加"部分数据不全"）；会话列表每行"丢失 0.1%"，有抽样时加"写入量为抽样估算"；写入进程列和排行里来自抽样或丢失率 > 1% 小时的数字前加"≈"；详情面板的 ETW 证据同样。对不上文件名的写入在会话的提示里写"有 3% 的写入量对不上目录（文件在监控开始前已经打开）"。

### 5.4 事件解析

- `RawEvent` 是 `ref struct`：提供程序 GUID、事件号、版本、进程号、线程号、UTC 时间、指针大小、载荷 `ReadOnlySpan<byte>`。Monitor.Etw 从 `TraceEvent.DataStart` / `EventDataLength` 直接构造，不复制。
- `PayloadParser.TryParse(in RawEvent, out MonitorEvent)` 按每个（事件号, 版本）手写的模板顺序读字段，不做对齐（是否如此由下面的模板 fixture 和差分核对确认）：指针按 `PointerSize`（本机 8），UnicodeString 读到 NUL（最长 32,767 个字符，超出或没有 NUL 算解析失败），AnsiString 同理，FILETIME 8 字节，SID 按 `winnt.h` 的 `_SID` 布局跳过（第 2 字节是子授权数 n，长度 8 + 4×n，n > 15 算解析失败）。ProcessStart v3 起、ProcessStop v2 起同时取进程序号，ProcessStart v3 起取父进程序号。覆盖：Kernel-File 10、11、16、26、27、28、30 的 v0 和 v1；Kernel-Process 1 的 v0–v4、2 的 v0–v2、15 的 v0–v2（V19 用）。其他事件号直接丢弃（计入 filtered）。
- 模板 fixture：`tools/verify/export-etw-templates.ps1`（普通权限，只读，调用 `Get-WinEvent -ListProvider`）把上面用到的事件的各版本字段名和类型导出到 `tests/fixtures/monitor/templates.txt`，文件头记系统版本，不含本机路径。每行一个事件版本，例如 `Kernel-File 16 1 ByteOffset:UInt64 Irp:Pointer FileObject:Pointer FileKey:Pointer IssuingThreadId:UInt32 IOSize:UInt32 IOFlags:UInt32 ExtraFlags:UInt32`。`TemplateConformanceTests` 读这个文件，按字段类型生成字节（每个字段填可以辨认的值，字符串、SID 取几种长度），交给 PayloadParser，检查用到的字段都取对了；解析器的手写模板和导出的不一致时测试失败。系统更新出现新版本时重新导出，新版本号在测试里应该走"未知版本"的路径。格式写进 `tests/fixtures/README.md` 的"monitor"一节。
- 未知版本（以后的 Windows 加了字段）：Monitor.Etw 通过 `RegisteredTraceEventParser`（按系统里的清单用 TDH 解码）订阅这两个提供程序的事件（它的额外开销在 2a.1 一并测），未知版本时用 `PayloadByName("FileName")` 等按名字取字段，再构造同样的 `MonitorEvent`；仍然失败计入 parse_errors。每个（事件号, 版本）前 5 次记警告日志，停止时记汇总；日志只记提供程序、事件号、版本和载荷长度，不记载荷内容（里面有文件路径）。
- 差分核对（`--verify-parser`，EtwBench 和 `monitor` 子命令支持，界面不用）：每个（事件号, 版本）的前 200 个事件同时用 TraceEvent 的 `PayloadByName` 解码，逐个比对用到的字段（ProcessID、ProcessSequenceNumber、CreateTime、ParentProcessID、ParentProcessSequenceNumber、ImageName、FileKey、FileName / FilePath、ByteOffset、IOSize、IOFlags、CreateOptions）。不一致计入 `parse_mismatch`，前 3 次在日志里记事件号、版本和字段名（不记值）。冒烟测试要求 `parse_mismatch=0`，这样 Server 2022 / 2025 上的事件版本（V5）才算核对过。
- 理由：精确模式每秒可能几万个事件，`PayloadByName` 每次装箱，GC 压力大；按字节解析快，而且 2b 直接调 ETW API 时原样复用。解析器是纯函数，模板 fixture 防止"测试和实现按同一份写错的模板通过"。

### 5.5 进程归属

#### 5.5.1 进程表

- 进程实例用（进程号, 创建时间）标识，ProcessStart 和 ProcessStop 都带 CreateTime，进程号复用不会混；事件带序号（ProcessStart v3+、ProcessStop v2+）时同时记下序号。
- 开始监控时先启动会话，再用 `CreateToolhelp32Snapshot` 列出现有进程（进程号、父进程号、exe 名），`QueryFullProcessImageName` 取完整路径，`GetProcessTimes` 取创建时间；打不开的进程（受保护进程）只有 exe 名。会话启动后到列举之间新开的进程两边都会看到，以事件为准。V19 如果确认 `CaptureState` 的 ProcessRundown 在 2 秒内给出全部进程（含受保护进程的完整路径、父进程号和序号），改用 rundown，Toolhelp32 只作为 rundown 失败时的后备；结论写进报告，并回来修订本节。
- 父进程：子进程的事件带 ParentProcessSequenceNumber、表里的父进程也有序号时，按序号精确匹配；否则取进程号等于 ParentProcessID、创建时间早于子进程、在子进程创建时还没退出的那个实例；找不到（父进程已退出、进程号已被复用）就当作没有父进程，进程链到此为止。
- 退出的进程保留到会话结束，最多 100,000 个，超出时先删退出最早的。来自没见过的进程号的事件归到"未知进程（PID n）"。
- 进程号 0 和 4 归到"系统"，界面显示为"系统（缓存回写或内核）"。

#### 5.5.2 服务宿主

`EnumServicesStatusEx(SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE)` 得到"进程号 → 服务名、显示名"（显示名由系统按界面语言给出，例如"Windows 更新"）。开始时查一次；之后父进程是 services.exe 的进程启动 2 秒后再查一次（服务要在进程启动后才注册上来），每次整点落盘前对还没有服务名的服务宿主再查一次。一个进程有多个服务时全部记下，显示"svchost.exe（服务 wuauserv）"或"svchost.exe（服务 A、B 等 4 个）"。

#### 5.5.3 写入进程和顶层应用

- 写入进程：事件头里的进程（服务宿主带上服务名）。
- 顶层应用（D1，推荐"最外层"）：从写入进程沿父进程链往上走，碰到"系统根"就停，取路上最外层的一个"不透明"进程；路上全是透明进程时取写入进程自己。相邻的同名进程（msedge ← msedge）合成一个。服务宿主自己是写入进程时，顶层应用就是它的服务（不再往上走）。
  - 系统根（只作为上级时停下，不计入）：System、smss、csrss、wininit、winlogon、services、lsass、explorer、userinit、sihost、svchost、taskhostw。
  - 透明（壳和运行时）：cmd、powershell、pwsh、conhost、OpenConsole、WindowsTerminal、wt、bash、sh、node、python、pythonw、py、java、javaw、dotnet。
  - 服务宿主拉起的子进程（例如 svchost 启动的 worker）：往上走碰到 svchost 就停，顶层应用取子进程自己（它不透明时）；进程链的最后一环显示这个服务宿主和服务名，例如"TiWorker.exe ← svchost.exe（服务 X）"。系统根本来不显示在进程链里，有服务名的服务宿主例外，因为它说明了是谁拉起的。
  - 例：node ← claude ← pwsh ← WindowsTerminal ← explorer → 顶层 claude.exe；uv ← pwsh ← Code ← explorer → 顶层 Code.exe；codex ← pwsh ← WindowsTerminal → 顶层 codex.exe 自己；wuauserv 的 svchost → 顶层"Windows 更新"。
  - 名单写在 Monitor 里（按 exe 名，不区分大小写），用单元测试固定。
- 进程链：写入进程加最多 3 个最近的非系统根上级（有服务名的服务宿主例外），顶层应用不在其中时用"…"接上。显示统一为"uv.exe ← pwsh.exe ← Code.exe"。
- 程序身份：每个映像路径第一次出现时用 `ExeIdentityReader.Read` 取产品名和公司名（已有缓存），存进 monitor.db（程序以后被卸载也能显示）。界面显示应用名的顺序：服务显示名 → 已装软件里安装位置包含这个 exe 的应用名（App 层用当前扫描的 `InventorySnapshot`）→ 清理过的产品名（`AppNames.CleanDisplayName`）→ exe 名。商店应用（路径在 WindowsApps 下）用 `AppNames.FromPackageFamily`。
- 进程链去重：一个会话里最多 10,000 种进程链，超出的归到"其他进程"（chain_id 0，和第 5.7 节合并用的是同一个），会话标"进程链表已满"。

### 5.6 写入量的口径

Kernel-File 的写事件只有每次 I/O 的 IOSize 和 ByteOffset，没有文件大小，所以分三个口径，界面上分开写：

| 口径 | 模式 | 算法 | 用来 |
|---|---|---|---|
| 新建 / 删除 / 重命名个数 | 两种 | CreateNewFile（带 `FILE_DIRECTORY_FILE` 的算新建目录）、DeletePath、RenamePath 各计一次；SetLinkPath（建硬链接）计入新建文件数（表里没有单独一列；硬链接不占新空间，估算增长不变）；都算在目标路径的所在目录上（RenamePath 用新名字还是旧名字看 V4） | 轻量模式的排序依据；"写入进程"列的依据 |
| 写入量 | 精确 | 非分页写入的 IOSize 之和（分页写入是缓存回写或映射文件的写入，算进去会重复；V2 确认后定标志位），抽样时乘以 k | 排行的"写入量"；含覆盖写，可能比增长大得多 |
| 估算增长 | 精确 | 只看监控期间新建的文件：按文件记最大写入位置，每次变大就把增量记到新建这个文件的进程链和文件所在目录上；文件在监控期间被删除时，把它累计的值从删除时的目录和进程链上减掉（行里的值可以是负数） | 和快照增长对照；"写入进程"列在精确模式下按它排序 |

#### 5.6.1 文件的对应和状态（精确模式，只在内存里）

```
fileKeyToName : FileKey → (DirId, NameHash)          NameCreate 时加，NameDelete 时删
fileState     : (DirId, NameHash) → (MaxPos, CreatorChain, SawNonPaging, DeleteOnClose, LastWrite)
                CreateNewFile 时建；DeletePath 时减回并删除；RenamePath 时移到新键；
                满 300,000 项时淘汰最久没写的（会话标"估算增长不完整"）
```

- DirId 来自会话级的 `DirectoryTable`：目录路径按段存的树，不区分大小写，一个会话内编号不变，只建到 32 层（更深的算在第 32 层，和快照一样），最多 200,000 个节点；满了以后新目录记到已有的最近的上级上，会话标"目录表已满"。按小时的计数器引用 DirId，合并只在计数器上做（第 5.7 节），不改 DirectoryTable，所以 fileState 里的 DirId 一直有效。
- NameHash 是文件名（`ToUpperInvariant` 后的 UTF-16）的 64 位 FNV-1a 哈希；文件名本身不保存。300,000 项时碰撞概率约 2×10⁻⁹，可以忽略。
- fileState 只为 CreateNewFile 建，所以表里都是监控期间新建的文件（V10：覆盖已有文件也会触发的话，旧内容被截断，新内容全部算估算增长，偏大，写进说明）。
- 写事件：FileKey → fileKeyToName 得到 (DirId, NameHash)，找不到计入 unresolved_writes（字节计入 unresolved_bytes，不进任何目录的行）；再找 fileState，找不到（监控开始前就有的文件）只计写入量。
- 关闭后重开：NameDelete 只删 fileKeyToName，fileState 不动（如果 V15 显示文件关闭时也会触发 NameDelete，这样也不会丢状态）；重开时新的 NameCreate 指回同一个 (DirId, NameHash)，MaxPos 接着算，不会从 0 重新累计。FileKey 被复用到别的文件时，NameCreate 会覆盖映射，写入跟着新的名字走。
- 改名：RenamePath 的 FileKey 在 fileKeyToName 里得到旧键、FilePath 得到新键，把 fileState 移到新键，并更新 fileKeyToName；旧键不知道时只计重命名数。
- 删除：DeletePath 的 FilePath 得到键，fileState 里累计的增长从（删除时的目录, CreatorChain）上减掉，然后删除这一项。同名文件删掉后再建，CreateNewFile 会建新的一项，从 0 开始。

#### 5.6.2 估算增长的输入规则

1. 位置：end = ByteOffset + IOSize，用 `ulong` 加 `checked` 计算。ByteOffset 等于 `0xFFFFFFFFFFFFFFFF`（写到文件末尾，追加写可能出现，V16）时 end = MaxPos + IOSize；其他 ≥ 2⁶³ 的值、溢出，或者单次增量 > 1 TiB 时不更新 MaxPos，计入 bad_offsets。
2. 分页和非分页：非分页写入更新 MaxPos 并置 SawNonPaging；分页写入只在 SawNonPaging 为 false 时更新 MaxPos（只有映射写入的文件）。理由：缓存写入已经由非分页写入算过，回写的分页写入按页取整，可能超过文件的实际末尾（V2 核对），全算进去会让小文件偏大。`write_bytes` / `write_ops` 只算非分页写入。
3. 记到谁头上：增量 = max(0, end − MaxPos)，记到 fileState.CreatorChain（CreateNewFile 的进程链）和文件当前所在目录上；写事件的进程（分页写入时是 System）不影响估算增长。
4. 抽样：被跳过的写入不更新 MaxPos；估算增长不乘 k。
5. 关闭时删除：CreateNewFile 的 CreateOptions 带 `FILE_DELETE_ON_CLOSE`（0x1000）时 fileState 标 DeleteOnClose。V12 显示这种文件不产生 DeletePath 时启用这条规则：它的 NameDelete 到达时按删除处理（删除数加 1、减回估算增长、删掉这一项），会话结束时还在表里的 DeleteOnClose 项也按删除处理。V12 显示有 DeletePath 时不启用（避免重复计数）。

#### 5.6.3 路径规范化

事件路径先按 `QueryDosDevice` 的映射把设备前缀换成盘符（第 5.7 节第 5 条）。有含 `~` 的段时（V13），用 `GetLongPathNameW` 换成长路径（`LongPathResolver`，按目录前缀缓存，最多 10,000 项；只读目录元数据，不打开文件）；目录已经不在或调用失败时保留原样，计入 short_names_unresolved。解析器测试用注入的假解析器，不碰真实磁盘。

#### 5.6.4 和快照对照时要说明的差别

快照用分配大小（按簇取整、压缩后更小），估算增长是逻辑大小；监控开始前已有的文件变大不算估算增长（只有写入量）；临时文件建了又删，估算增长自然抵消，和快照一致；只用 `SetLength` 预分配、没有写入的文件，快照里占空间，估算增长为 0（V16）。这些写进增长分析页的原型说明和报告。

### 5.7 目录向上合并

方案原文："目录向上合并到快照里有记录的节点，这样能和快照对比的结果对上。" 只合并到快照节点有一个问题：监控期间新出现、快速变大的目录（例如新装的软件），在开始监控时的快照里还没有，它的写入会被并到上级；等下一个快照记录了它、热点分解报出它时，按路径就查不到写入记录。所以设计成"快照节点是合并的下限，预算内保留更细的目录"（D6，推荐）：

1. 锚点：每个盘最近一次快照里记录的目录（`store.List(volume)` 的最后一个，`Load` 后取 `IsDirectory` 的条目的 `GetPath`），加上盘的根。开始监控时读一次；监控期间保存了新快照时（扫描后自动保存、"创建快照"）重新读。没有快照的盘只有根。`monitor` 子命令的快照库不存在时不创建，只用盘根。
2. 当前小时的计数器：`Dictionary<(DirId, ChainId), Counters>`，事件算在文件所在的目录上。按事件时间戳（UTC）分小时；整点后 60 秒内到达的上一个小时的事件仍记到上一个小时（缓冲区有延迟），之后上一个小时封存。计数器超过 200,000 项时就地压缩一次：只做下面的第 1 段，不碰锚点。
3. 落盘时合并（纯函数 `WriteTreeFolder.Fold(counters, directoryTable, anchors, budget = 1000, hardLimit = 2000)`）。它在计数器的副本上做，只输出行，不改内存里的计数器，所以同一小时的结果和什么时候落盘无关：
   - 每个目录的权重 = |估算增长| + 写入量 + 1 MiB ×（新建 + 删除 + 重命名个数），按子树汇总；行数 = 有计数的"目录 × 进程链"个数；
   - 第 1 段：反复取权重最小的非锚点叶子目录，把它的计数器按进程链加到父目录上，然后删掉它；直到行数 ≤ budget，或者已经没有非锚点的叶子；
   - 第 2 段：还超出时，每个目录只保留权重最大的 N 条进程链，其余并成这个目录的"其他进程"（chain_id 0）；N 依次取 8、4、2、1，直到行数 ≤ budget；
   - 第 3 段：还超出（有写入的锚点本身就比预算多，例如 Windows 更新加编译）：行数 ≤ hardLimit 时照样输出，会话和这个小时标 `budget_exceeded`；> hardLimit 时反复把权重最小的锚点叶子并到上级（根除外），直到 ≤ hardLimit，标 `anchors_folded`。根只有一个目录，所以这一段一定会结束；
   - 结果：每一行的目录是锚点，或者在某个锚点下面；没有标 `anchors_folded` 的小时，锚点以下的写入不会被并到锚点以上。
4. 查询（`WriteIndex.Under(路径, 排除的子路径)`）：热点路径 H 的写入 = 目录等于 H 或在 H 下面的行之和；"H 的其余部分"再减去同一列表里 H 下面的其他热点。H 是监控时的锚点（大多数情况）时结果是准的；H 是新目录时，只要它在预算内被保留下来就准，被并到上级时查不到（这时显示"监控的 N 小时里没有写入"会误导，所以如果 H 的上级行里有写入，显示"写入记录在上级目录 X"）。热点是文件时（`GrowthHotspot.IsDirectory == false`，快照会单独记 ≥ 100 MiB 的文件），用它所在的目录查，只取等于这个目录的行，文字加"（所在目录）"。
5. 边界：只记有盘符的本地固定盘（设备路径按 `QueryDosDevice` 换成盘符，遇到不认识的前缀每分钟最多刷新一次映射，仍不认识的计入 filtered）；网络路径（`\Device\Mup`）和没有盘符的卷不记；自己进程的事件不记（写 monitor.db 会产生事件）。

文件夹扫描（`--scan <文件夹>`）的快照路径也是完整路径，按路径前缀匹配同样成立。

### 5.8 存储

`%LOCALAPPDATA%\DiskThinner\monitor.db`（D4，推荐单独一个库）。理由：快照库的大小在设置和增长分析页上显示，有"单次快照 ≤ 1 MB"的目标，不混进去；保留策略和快照不同；2b 的服务写自己的库，用同一套表结构；要清掉监控数据只需删一个文件。和快照对照按路径字符串匹配（`GrowthHotspot.Path` 本来就是字符串，比较时不区分大小写），不需要共用 `path` 表的编号。

```sql
PRAGMA user_version = 1;  -- 比这个大时拒绝打开（"来自更新的版本"），和快照库一样
PRAGMA auto_vacuum = INCREMENTAL;  -- 建库时设置，清理后 incremental_vacuum
CREATE TABLE session (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, started_at INTEGER NOT NULL, planned_end INTEGER NOT NULL,
  ended_at INTEGER, last_flush INTEGER NOT NULL, mode TEXT NOT NULL, end_reason TEXT, downgraded_at INTEGER,
  received INTEGER NOT NULL, lost INTEGER NOT NULL, sampled_out INTEGER NOT NULL, parse_errors INTEGER NOT NULL, filtered INTEGER NOT NULL,
  unresolved_writes INTEGER NOT NULL, unresolved_bytes INTEGER NOT NULL, bad_offsets INTEGER NOT NULL, peak_events_per_sec INTEGER NOT NULL,
  avg_cpu_permille INTEGER NOT NULL, peak_ws_bytes INTEGER NOT NULL, flags INTEGER NOT NULL);
  -- flags：1 估算增长不完整，2 目录表已满，4 budget_exceeded，8 anchors_folded，16 兜底任务注册失败，32 进程链表已满
CREATE TABLE session_hour (session_id INTEGER NOT NULL, hour INTEGER NOT NULL, mode TEXT NOT NULL, received INTEGER NOT NULL,
  lost INTEGER NOT NULL, sampled_out INTEGER NOT NULL, unresolved_bytes INTEGER NOT NULL, flags INTEGER NOT NULL,
  PRIMARY KEY (session_id, hour)) WITHOUT ROWID;
CREATE TABLE dir (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, UNIQUE (parent_id, name));
  -- 和快照库的 path 表一样按段存（SnapshotStore 的做法）
CREATE TABLE image (id INTEGER PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL, services TEXT NOT NULL,
  product TEXT, company TEXT, UNIQUE (path_key, services));
CREATE TABLE chain (id INTEGER PRIMARY KEY, writer_id INTEGER NOT NULL, top_id INTEGER NOT NULL, links TEXT NOT NULL UNIQUE); -- links: "12<5<3"
CREATE TABLE write_stat (session_id INTEGER NOT NULL, hour INTEGER NOT NULL, dir_id INTEGER NOT NULL, chain_id INTEGER NOT NULL,
  created_files INTEGER NOT NULL, created_dirs INTEGER NOT NULL, deleted INTEGER NOT NULL, renamed INTEGER NOT NULL,
  write_bytes INTEGER NOT NULL, write_ops INTEGER NOT NULL, growth_bytes INTEGER NOT NULL, estimated INTEGER NOT NULL,
  PRIMARY KEY (session_id, hour, dir_id, chain_id)) WITHOUT ROWID;
```

- 不建 `write_stat (hour)` 索引：查询先从 session 表（行很少）挑出和时间范围有交集的会话，再按会话 `WHERE session_id = ? AND hour BETWEEN ? AND ?` 读，走主键前缀。
- `hour` 是 UTC 整点的 Unix 秒数 ÷ 3600，界面按本地时间显示（时区偏移不是整点的地区，本地的"小时"和桶边界差半小时，只影响显示）。
- 会话开始时（会话已建、提供程序已启用之后）立即插入 session 行（`last_flush = started_at`，计数为 0），所以刚开始就被强制结束的会话也能在清理时找到。
- 落盘：每 5 分钟、整点加 60 秒、停止时，在一个事务里删掉（会话, 还没封存的小时）的旧行，再写合并后的行，补上新出现的 dir / image / chain，更新 session 和 session_hour 的统计及 last_flush。崩溃最多丢 5 分钟。
- 保留（D5，推荐）：默认 90 天（设置可选 30 / 90 / 180），另有总大小上限 64 MB；每次会话结束和程序启动时按开始时间删最早的整个会话，直到两条都满足，然后 `PRAGMA incremental_vacuum`。`dir`、`image`、`chain` 里没人引用的行一起删（`dir` 只删没有被引用、也没有子节点的行，反复做到没有为止）。
- 体积估算（SQLite 整数变长编码；数字是估算，以合成测试为准）：

| 表 | 最坏情况（每小时 1,000 行）的行数 | 每行约 | 合计约 |
|---|---|---|---|
| write_stat | 24,000 | 46 字节（12 列，记录约 43 字节，加单元格开销；按主键顺序写，页按 85% 填充算） | 1.3 MB |
| dir（含 UNIQUE 索引） | 26,000（每小时 500 个新目录，每个带 2 个新段，另有约 2,000 个锚点和上级） | 表 23 字节 + 索引 24 字节（索引随机插入，按 70% 填充算） | 1.5 MB |
| chain / image | 500 / 300 | — | 0.15 MB |
| session / session_hour | 1 / 24 | — | 可以忽略 |
| 合计 | | | 约 3.0 MB（目标 ≤ 4 MB） |

  每小时都到硬上限（2,000 行，一半是新目录）时约 6 MB（目标 ≤ 8 MB）。正常使用每小时几十到几百行，24 小时在 0.5 MB 以内。
- 最坏情况的合成 fixture（`MonitorStoreTests`，写到临时目录）：24 小时；每小时 1,000 行，其中 500 行在固定的 500 个锚点上（深度 4），500 行在这个小时新出现的目录上（锚点下再加 2 段，每段 12 个 ASCII 字符）；500 种进程链、300 个映像（路径 80 个字符）；计数取上限附近的值（created_files 10,000、write_bytes 2⁴⁰）。断言 ≤ 4 MB；同样的参数每小时 2,000 行时断言 ≤ 8 MB。实测超出时先查是哪张表，再决定压缩办法或修改目标，不悄悄放宽。
- 落盘和查询都是每次单独开连接（`Pooling=false`、`busy_timeout=5000`），和 `SnapshotStore` 一样。

### 5.9 界面和命令行

#### 5.9.1 `MonitorRunner`（Monitor.Etw）和 `MonitorHost`（App/Monitor/MonitorHost.cs）

- `MonitorRunner`：持有 `DiagnosticSession`、汇总器和 `MonitorStore`；负责开始 / 停止、5 分钟和整点的落盘计时、状态快照 `MonitorStatus`（状态、模式、开始和计划结束时间、收到和丢失的事件、抽样倍数、CPU、内存、最近一次落盘的错误）。界面和 `monitor` 子命令共用它，落盘和停止的逻辑只有一份。事件泵线程和其他线程之间只通过加锁的汇总器和 `MonitorStatus` 快照交互：泵线程每个事件拿一次锁（不竞争时开销很小），落盘线程拿锁后复制出要落盘的小时的计数器，`Fold` 和写 SQLite 都在锁外做。
- `MonitorHost`：主窗口持有一个，包着 `MonitorRunner`。2 秒一次取状态刷新工具栏和状态栏；会话结束或落盘后发 `DataChanged`（界面重新查询，500 毫秒防抖）；给设置页提供 monitor.db 的大小；启动时在后台做第 5.1.2 节的判定。截止时间在 `DiagnosticSession` 里每秒检查（第 5.1.3 节），`MonitorHost` 不订阅 `SystemEvents`。

#### 5.9.2 开启写入监控对话框（Dialogs/MonitorDialog.cs）

模态 Form，`AutoScaleMode.Dpi`，结构照原型和第 3.3 节 P7：时长用 `SegmentedControl`（1 / 4 / 8 / 24 小时，默认取设置），模式用 `SegmentedControl`（默认取设置），四条注意事项。几种状态：管理员（"开始监控"）；普通权限（"以管理员身份重启并开始"）；另一个实例在监控（第 5.1.2 节的 Live，说明文字，开始按钮置灰）；账户不一致（黄色提示，允许继续）；自动开始被账户不一致拦下（第 5.1.5 节，时长和模式预填，黄色提示，按钮"开始监控"）。返回（时长, 模式）。所有控件有 AccessibleName；`Esc` 取消、`Enter` 开始。

#### 5.9.3 工具栏和状态栏

- 工具栏右侧加一个链接样式的按钮（第 3.3 节 P1 的四种文字），AccessibleName 和文字一致。点击：关闭时打开对话框；诊断中时确认后停止；另一个窗口正在监控时打开对话框（显示这个状态）；有残留时提示以管理员身份重启。设置为"关闭"时点击跳到设置页的写入监控一组。
- 状态栏在"管理员模式"前加一格（P2 的文字）。

#### 5.9.4 增长分析页

- 比较范围 [起点快照时间, 终点快照时间或现在]。`SelectionChanged` 里的后台任务同时 `WriteIndex.Load(monitorStore, from, to)`（SQL 里按 dir、chain 汇总这段时间的小时行，再在内存里建目录树），和快照对比一起返回。monitor.db 不存在时返回空索引；打不开时覆盖卡片显示"写入监控数据读不了：{0}"。
- "写入监控覆盖"卡片：值"N / M 小时"（会话时段和范围的交集，向下取整到 0.1 小时；M 是范围的小时数），脚注"丢失 0.2%"或"开启诊断"或"诊断中，剩余 3 小时"。
- "写入监控覆盖时段"卡片：新控件 `CoverageTimeline`（放在 Controls/Charts.cs，自绘：TitleBar 色的底条，Accent 色的时段，进行中的时段同色加右端标记；AccessibleRole 为 Graphic，AccessibleDescription 列出所有时段）；下面最多 4 行会话（时段、"诊断 · 轻量 / 精确"、时长、丢失率、降级说明），多出的写"另有 N 次"；说明文字；按钮"开启诊断"或"停止监控"。
- `ItemList` 的扩展（在 OverviewPage.cs 里，`ItemList` 和 `ListItem` 都在那里）：
  - `ListItem` 加可选参数 `int? TagColumn = null`。设了时，这一列先画文字（右边留出标签宽度加 6 px，超长用省略号），再在文字后面画标签；`SetItems` 写进这个 SubItem 的读屏文字是 `"{文字} {标签}"`。没设时保持现在的规则（第一个空单元格画标签），现有断言 `rows[0].Tag == "未监控"` 不受影响。
  - 构造函数加可选参数 `int[]? rightAligned`（右对齐的列号），给排行用；不给时保持 `numericColumns` 的行为。
  - 不加"第二行"：行的补充说明放在 `Note`（工具提示），行高不变。
- 变化最大的目录的"写入进程"列（第 5 列，下标 4）：
  - 范围内没有监控：空文字，标签"未监控"（现有的 `notMonitored` 样式，不设 TagColumn）；
  - 有监控，`Under` 有行：选主要进程链：Change ≥ 0 时，精确模式按估算增长、其次写入量，轻量模式按新建 + 删除 + 重命名；Change < 0（净减少）时按删除数、其次 |估算增长|。第一名不到合计的 60% 时文字写"node.exe 等 3 个进程"，否则写进程链；有估算时前面加"≈"；`TagColumn = 4`，标签"实测"（`RuleView.ObservedSource`）。写入者是进程号 0 或 4 时文字是"系统（缓存回写或内核）"。热点的规则类别是 `vm-disk` 时文字后面加"（虚拟磁盘，看不到内部是谁写的）"。热点是文件时文字加"（所在目录）"。`Note` 里加"监控 14 / 168 小时内：新建 8,921 个文件，估算增长 1.20 GB"；
  - 有监控但没有行：上级目录有写入时文字"写入记录在上级目录 X"，否则"监控的 N 小时里没有写入"，不加标签。
- "写入进程排行"：新的 `ItemList`，列"进程、产品 · 公司、上级应用、新建文件、写入量、主要目录"，`rightAligned: [3, 4]`；按写入进程（映像 + 服务名）汇总，进程号 0 和 4 汇总成一行"系统（缓存回写或内核）"（产品和上级应用为"—"）；上级应用取这个写入进程写得最多的那条链的顶层应用（顶层就是自己时写"—"）；最多 20 行；精确按写入量排，范围内只有轻量数据时按新建文件排，写入量列为"—"；双击跳到主要目录（当前扫描里找得到时）。
- "增长最多的应用"卡片仍按快照热点的归属算（任何时候都有）；有监控数据时脚注加"监控时段写入最多：Code.exe 1.2 GB"。

#### 5.9.5 概览

- "增长最快"（列：目录、变化、写入者；第三列原来叫"归属"）按三种情况显示第三列（`TagColumn = 2`）：
  1. 近 7 天（`WeekBaseline` 到现在）的监控数据里这个热点有写入：文字是主要进程链（选法同第 5.9.4 节），标签"实测"；
  2. 没有监控写入、但归属已知（`OwnerOf` 或规则类别）：范围内没有监控覆盖时，文字是归属，标签"未监控"（和增长分析页同一个样式）；有监控覆盖、只是这里没写入时，只写归属，不加标签，`Note` 写"监控的 N 小时里没有写入"；
  3. 两者都没有：保持现在的做法（空文字，标签"未识别"或"已不存在"）。
- 近 7 天没有监控覆盖、设置不是"关闭"时，"增长最快"卡片底部显示提示和"开启 4 小时诊断"链接（`ItemList` 加一个可选的提示行，放在"双击或按回车……"上面）。
- 主窗口在扫描完成和 `DataChanged` 之后，在后台为概览加载 [`WeekBaseline` 的时间, 现在] 的 `WriteIndex`，为空间浏览加载近 30 天的。

#### 5.9.6 空间浏览详情

- `DetailPanel.ShowNode(tree, node, stats, classification = null, owners = null, writes = null)` 增加 `WriteIndex? writes` 参数。`ExplorerPage` 加 `SetWriteIndex(WriteIndex?)`：主窗口传入近 30 天的索引，`ExplorerPage` 存下来并重新显示当前节点；第 431 行调用 `ShowNode` 时传入。
- "证据"标题的条件从"有归属证据或规则证据"改成"有归属证据、规则证据，或者有 ETW 这一条"（有写入，或者监控过但没写入）。
- ETW 这一条：标签"ETW"、样式 `RuleView.ObservedSource`：
  - 有写入：最近一次有写入的会话，"最近一次诊断（10-19 08:00–10:00，精确）中，uv.exe 在这里新建了 8,921 个文件，估算增长 1.20 GB；进程链：uv.exe ← pwsh.exe ← Code.exe"；有多个会话时再加一行"近 30 天 3 次诊断共 14 小时，这里的写入主要来自 uv.exe、node.exe"；
  - 监控过但没有写入："近 30 天 3 次诊断（共 14 小时）里没有程序在这里写入"；
  - 从没监控过：不显示。
  - 选中的是文件时用它所在的目录查，文字写"所在目录"。
- `ShowGroup`（按归属汇总、按类别汇总的视图）不显示 ETW。

#### 5.9.7 设置页和设置模型

- `AppSettings` 加 `Monitor` 一组（`MonitorSettings`）：`Enabled`（"off" / "diagnostic"，默认 "diagnostic"）、`Mode`（"light" / "precise"，默认按 D2）、`DefaultHours`（1 / 4 / 8 / 24，默认 4）、`KeepDays`（30 / 90 / 180，默认 90）。字段带默认值，旧的 settings.json 没有这一组也能读，`Version` 不变；`Normalized()` 把不认识的值收回到默认值。
- 设置页的写入监控卡片按第 3.3 节 P6 改：关闭和临时诊断是真的单选按钮，常驻服务保留 `Later(...)`；记录模式 `SegmentedControl`；默认时长、保留两个下拉框；"当前占用 X"；"数据位置"里加 monitor.db 一行。
- 设置改成"关闭"时如果正在监控，先确认再停止。

#### 5.9.8 命令行

- 主程序参数（`AppOptions`）：`--monitor-db <路径>`；`--start-monitor <小时>`（1 到 24 的整数）和 `--monitor-mode light|precise`（以管理员身份重启时带上；新实例只有在管理员下才执行，账户不一致时按第 5.1.5 节先显示对话框）；`--monitor-seconds <秒>`（自动化验证用，10 到 86,400，优先于 `--start-monitor`）；`--show-monitor-dialog`（截图后再截一张对话框，和 `--show-confirm` 一样只显示不执行）。超出范围或不认识的值忽略并记日志。`--report` 增加 `monitor_*` 几行（状态、会话数、行数、库大小、收到 / 丢失 / 抽样、错误）。
- 子命令（D14），`Program.Main` 里和 `snapshot` 一样在界面初始化之前分派（这条路径不经过界面的异常处理，`MonitorCommand.Run` 自己注册 `AppDomain.UnhandledException` 和 `ProcessExit` 的 `EmergencyStop`）：

```
DiskThinner.exe monitor --seconds N [--mode light|precise] [--db 路径] [--snapshot-db 路径] [--report 路径] [--verify-parser]
DiskThinner.exe monitor --cleanup [--db 路径] [--report 路径]
DiskThinner.exe monitor --status [--report 路径]
```

  - `--seconds`：10 到 86,400 的整数，跑会话时必填；不合法时退出码 4。`--mode` 默认 light。
  - `--db` 默认 `%LOCALAPPDATA%\DiskThinner\monitor.db`；验证脚本一律指向输出目录（和 `--snapshot-db` 的约定一样："验证时指向别处，不影响真实数据"）。
  - `--snapshot-db` 默认快照库的默认位置，只读取锚点；文件不存在时不创建，只用盘根。
  - `--report`：key=value 的 UTF-8 文件，格式和主程序的 `--report` 一样（smoke.ps1 的 `Read-Report` 能直接读）。在开始、运行中每 5 秒、结束时各整体重写一次：先写同目录的临时文件，再 `File.Move(…, overwrite: true)`。键：
    - `state=starting|running|stopped`、`started=0|1`（会话已建、提供程序已启用、session 行已插入后为 1）、`session_name`、`mode`；
    - `received`、`lost`、`sampled_out`、`parse_errors`、`parse_mismatch`（只在 `--verify-parser` 时有）、`filtered`、`unresolved_writes`、`bad_offsets`、`events_by_id`（例如 `Kernel-File/16/v1:1234;Kernel-Process/1/v4:20`）；
    - 结束时：`session_stopped=true|false`（`GetActiveSessionNames()` 里已经没有这个名字）、`end_reason`、`exit_code`；
    - `--cleanup`：`residual_found`、`residual_stopped`、`orphans_marked`、`task_deleted`；`--status`：`live=0|1`、`residual`（逗号分隔的会话名）。
  - 主程序是 WinExe，控制台看不到输出，所以 `--status` 的结果只写 `--report` 和日志。
  - 返回值：0 成功，1 出错，2 不是管理员（`--status` 不需要管理员），3 另一个实例正在监控，4 参数不对。日志写到 `logs\monitor.log`（`AppLog.Append`，256 KB 上限，和 snapshot.log 一样）。

### 5.10 隐私和安全

- 不联网：Monitor 和 Monitor.Etw 不引用 HttpClient；不使用 TraceEvent 的符号下载（`SymbolReader`）和 `TraceLog`；如果 V7 证明不需要，`msdia140.dll` 不进发布包。
- 只读：监控本身不打开被写入的文件（文件名来自事件，大小来自事件里的偏移）；只读取正在运行的程序映像的版本信息（`ExeIdentityReader.Read`，`FileVersionInfo` 会打开 exe 读版本资源）和含 8.3 短名的目录的长名（`GetLongPathNameW`，只读目录元数据）。只写自己的 monitor.db、日志和 `--report` 文件；D7 的计划任务（和注册时的临时 XML 文件）是唯一的系统改动，监控结束时删除，过期后也会自己删除。这一条写进 Monitor 的项目注释；评审时检查 Monitor 和 Monitor.Etw 里没有打开别的文件的 `File.Open` / `FileStream` 之类的调用（monitor.db 由 SQLite 打开，报告和临时 XML 集中在 `KeyValueReport` 和 `WatchdogTask` 里）。
- 只存目录、进程映像路径、服务名、产品名和公司名；不存文件名、命令行、文件内容。fileState 里的文件名哈希只在内存里。
- 日志：解析失败只记提供程序、事件号、版本和载荷长度；差分核对不一致只记字段名；不记事件里的文件名和路径。
- 最长 24 小时：第 5.1.3 节，`DiagnosticSession` 负责；只会停名字符合正则的会话。
- 监控数据和快照一样只在本机，界面上的路径用 `PathDisplay.Shorten` 显示。

### 5.11 打包、原生 DLL 和 CI

- 原生文件（D10）：新增仓库根目录的 `Directory.Build.targets`（所有项目都会导入）。TraceEvent 的 props 在 `build/` 和 `buildTransitive/` 下都有，App 和测试项目通过项目引用也会导入它的 `None` 项，而 Monitor.Etw 自己的 `None` 项也会随项目引用复制到 App 的输出目录，所以只在 Monitor.Etw 里处理不够。目标在 `AssignTargetPaths` 之前按 `Link` 前缀去掉不需要的 `None` 项：

```xml
<Project>
  <!-- TraceEvent 4.0.0 的 props（build/ 和 buildTransitive/ 都有）把 x86\、arm64\、amd64\ 下的原生文件作为 None 项复制到输出目录。
       只发 win-x64，去掉其他架构；V7 证明 amd64\ 下的也不需要时一起去掉（docs/phase2a-design.md 第 5.11 节）。 -->
  <Target Name="DropTraceEventNatives" BeforeTargets="AssignTargetPaths">
    <ItemGroup>
      <_DtDropNative Include="@(None)" Condition="$([System.String]::Copy('%(None.Link)').StartsWith('x86\')) Or $([System.String]::Copy('%(None.Link)').StartsWith('arm64\'))" />
      <None Remove="@(_DtDropNative)" />
    </ItemGroup>
  </Target>
</Project>
```

  V7 证明 `msdia140.dll` 不需要时把 `amd64\msdia140.dll` 也加进条件；连 `KernelTraceControl.dll` 都不需要时去掉整个 `amd64\`。`Dia2Lib.dll`、`TraceReloggerLib.dll` 是 lib 下的托管程序集，V7 证明不需要时在同一个文件里加一个 `AfterTargets="ResolvePackageAssets"` 的目标，从 `RuntimeCopyLocalItems` 里按文件名去掉。V7 证明需要 KernelTraceControl 时保留它，并按 V8 的结果处理单文件版（单文件里 `Assembly.Location` 为空，可能找不到它）：设 `ExcludeFromSingleFile=true` 让它放在 exe 旁边。最终以 `dotnet publish` 的实际输出为准。
- 两种发布包：`build-release.ps1` 改两处：统计文件数改成递归（现在只数顶层，子目录里的原生文件看不到）；为每个包写出文件清单 `files-<包名>.txt`（相对路径和大小，按路径排序）。报告里对比加 TraceEvent 前后的清单（依赖运行库版现在约 1.6 MB、17 个文件，以加之前重新打包的清单为准）。
- 冒烟测试（`tools/verify/smoke.ps1`）：每种包解压后先断言没有 `x86\`、`arm64\` 目录。管理员下每种包加一项 `monitor-<包>`：后台跑一个 pwsh 循环写 20 个小文件，同时 `monitor --seconds 15 --mode precise --verify-parser --db <OutDir>\monitor-<包>\mon.db --snapshot-db <OutDir>\monitor-<包>\snap.db --report <OutDir>\monitor-<包>\report.txt`；检查退出码 0、`received` > 0、`parse_mismatch=0`、`session_stopped=true`、`logman query -ets` 里没有 `DiskThinner-Diag-`；把 `events_by_id` 写进 smoke.txt（V5）。不是管理员时 SKIP。`package.yml` 不用改（上传的 `smoke/**/report.txt` 能匹配到）。
- CI 构建和测试（`ci.yml`）不用改：Monitor.Tests、EtwBench、WriteLoad 都在解决方案里，提权测试自动跳过。

### 5.12 附带修复：方块图的帮助文字（FR-10）

- 现象（`docs/known-issues.md`）：`UiAutomationTests.TreemapIsReachableAndDescribed` 偶尔读到的 HelpText 是一个临时目录路径，不是"方向键……"的说明。推测：鼠标停在方块图上时 `TreemapView.OnMouseMove` 调用 `_tip.SetToolTip(this, 路径)`，提示框和这个控件关联后，系统的 UIA 代理把提示文字当成了 HelpText。读屏用户把鼠标停在方块图上时也会遇到。没有确认。
- 复现（先于修改）：`UiAutomationTests` 加用例 `TreemapHelpSurvivesHoverTooltip`：用现有的 `Launch` 启动主程序，找到方块图的窗口句柄，`PostMessage(WM_MOUSEMOVE)` 到某个方块的中心（只发消息，不移动真实鼠标），等 1 秒，再读 HelpText。改代码前先跑这个用例，读到路径就算复现。复现不了时不改代码，把 known-issues 这一条改成"未复现"并写上复现方法。
- 修法（按复现用例的结果选，先 a 后 b）：
  - a. `TreemapAccessible` 覆盖 `Help`，返回和 `Description` 相同的操作说明，再跑复现用例；
  - b. a 不够时，不再用 `SetToolTip` 把提示框绑在方块图上，改为在鼠标移到新方块时 `ToolTip.Show(文字, this, 位置)`、离开时 `Hide(this)`；
  - 两种都不行时停下来和用户商量（例如把悬停的路径和大小改到面包屑旁边显示），不擅自去掉提示框。
- 回归：新用例留在 `UiAutomationTests`；known-issues 里这一条改成已修复，写明原因。

## 6. 错误处理

| 操作 | 失败情况 | 可恢复 | 调用方得到 | 日志 |
|---|---|---|---|---|
| 开始监控 | 不是管理员 | 是 | `StartResult.NotElevated`（走重启流程） | 不记 |
| | 另一个实例在监控（`MonitorLock.TryAcquire` 返回 null，含专用线程 5 秒内没给出结果） | 是 | `StartResult.AlreadyRunning`；超时的那个专用线程收到释放信号后自己退出 | 信息（超时时警告） |
| | monitor.db 打不开（IO、权限、版本更新、损坏） | 是 | `StartResult.Failed(消息)`，不开会话（没处存就不收） | 错误 |
| | 建会话或启用提供程序失败（`UnauthorizedAccessException`、`COMException`、会话数到上限的 Win32 错误） | 是 | `StartResult.Failed(消息)`，已建的会话立即停掉、锁释放；界面"无法开启写入监控：{0}" | 错误 |
| | 插入 session 行失败（`SqliteException`） | 是 | 同上，会话停掉 | 错误 |
| | 兜底任务注册失败 | 是 | 带 EndBoundary 的失败时先去掉 EndBoundary / DeleteExpiredTaskAfter 再试一次；仍失败照常开始，会话标 flags 16，状态里加说明 | 警告 |
| | 兜底任务 XML 生成时会话名不合法 | 否（程序错误） | `ArgumentException`，不注册，照常监控 | 错误 |
| | 列举进程时个别进程打不开 | 是 | 只记 exe 名 | 不记（计数写进会话统计） |
| 处理事件 | 载荷太短、版本未知且按名字也取不到 | 是 | 丢弃，计入 parse_errors | 每种前 5 次警告（只记事件号、版本、长度），停止时汇总 |
| | 偏移是哨兵值以外的 ≥ 2⁶³、溢出、单次增量 > 1 TiB | 是 | 不更新最大写入位置，计入 bad_offsets | 不记（计数） |
| | 短名换不成长名 | 是 | 保留原样，计入 short_names_unresolved | 不记（计数） |
| | 文件状态表、目录表、进程链表满 | 是 | 按第 5.5.3、5.6.1 节降级，会话标 flags | 信息（每种一次） |
| | 其他异常（程序错误） | 否 | 泵线程停止会话，`end_reason=error`，状态栏提示 | 崩溃报告 |
| 每秒的计时器回调 | 任何异常 | 是 | 捕获，不抛到线程池；连续 3 次出错时 `Stop("error")` | 错误 |
| 落盘 | `SqliteException`（锁超时、磁盘满、损坏）、`IOException` | 是 | 数据留在内存，下次再写；连续 3 次失败后停止会话，`end_reason=store-error`，状态栏提示 | 每次错误 |
| 停止 | 会话已经不在 | 是 | 当作已停止 | 信息 |
| | 停会话、落盘、删任务中某一步失败 | 是 | 后面的步骤照常执行，锁在 finally 里释放 | 警告 |
| | 退出时最后一次落盘超过 3 秒 | 是 | 不等，退出 | 警告 |
| 启动时的判定（第 5.1.2 节） | monitor.db 打不开或锁超时 | 是 | 这次不标未结束的行，下次启动再做 | 警告 |
| | 列不出活动会话（V21） | 是 | `activeNames = null`：不显示残留提示，Live 时不标行 | 不记 |
| 清理残留 | 普通权限停不了 | 是 | 状态栏提示 | 不记 |
| | 管理员下停止失败 | 是 | 状态栏"残留的监控会话清理失败：{0}，可以运行 logman stop <名字> -ets" | 警告 |
| 查询（界面） | monitor.db 不存在 | 是 | 空索引 | 不记 |
| | 打不开、版本更新、损坏 | 是 | 索引不可用，覆盖卡片显示原因，其他功能不受影响 | 警告 |
| 以管理员身份重启 | UAC 取消 | 是 | 状态栏"已取消以管理员身份重启" | 不记 |
| `monitor` 子命令 | 参数不合法 | 是 | 退出码 4 | 信息 |
| | 报告文件写不了（目录不存在、权限） | 是 | 监控照常进行，退出码不受影响 | 警告（只记一次） |
| 设置、主程序命令行 | 值不合法 | 是 | 收回默认值 / 忽略 | 信息 |

## 7. 输入校验和不变量

输入校验：

| 输入 | 规则 | 不合格时 |
|---|---|---|
| ETW 载荷 | 长度够模板；字符串在载荷内以 NUL 结束、≤ 32,767 字符；SID 子授权数 ≤ 15；指针大小 4 或 8 | 计入 parse_errors，丢弃 |
| 写入的偏移和大小 | `ulong` 加 `checked`；`0xFFFFFFFFFFFFFFFF` 按"写到末尾"处理；其他 ≥ 2⁶³ 或单次增量 > 1 TiB 不用 | 计入 bad_offsets |
| 事件路径 | 以已知的设备前缀开头（换成盘符后是本地固定盘），换算后 ≤ 32,767 字符；含 `~` 的段换成长路径 | 前缀不认识计入 filtered；换不成长路径保留原样 |
| 进程号 | 任意；0 和 4 归"系统" | — |
| 映像路径 | `QueryFullProcessImageName` 失败时用 Toolhelp 的 exe 名 | — |
| 时长 | API：10 秒到 24 小时；界面：1 / 4 / 8 / 24 小时；`--start-monitor` 1 到 24 的整数；`--monitor-seconds` 和 `monitor --seconds` 10 到 86,400 | API 抛 `ArgumentOutOfRangeException`；主程序命令行忽略并记日志；`monitor` 子命令退出码 4 |
| 模式 | `light` / `precise` | 主程序命令行忽略；子命令退出码 4；设置收回默认 |
| monitor.db | `user_version` ≤ 1 | 拒绝打开，提示升级 |
| 会话名 | `^DiskThinner-Diag-\d{14}$` | 不是我们的，不碰；生成兜底任务 XML 时抛 `ArgumentException` |
| 设置 | `Enabled`、`Mode`、`DefaultHours`、`KeepDays` 只能取界面上的值 | `Normalized()` 收回 |

不变量和负责的层：

- 最长 24 小时、一次只有一个会话、只停自己的会话：Monitor.Etw 的 `DiagnosticSession`，它握着会话句柄和 `MonitorLock`，界面和命令行都通过它，绕不过去。互斥量只由 `MonitorLock` 的专用线程获取和释放，其他线程只发信号（所以停止可以发生在任何线程上）。
- 未结束的会话行只属于正在监控的实例：Monitor 的 `SessionLiveness`（纯函数）加启动时的标记，`MonitorStore` 的测试覆盖。
- 合并：每小时行数 ≤ budget；或者 ≤ hardLimit 并标 `budget_exceeded`；只有标了 `anchors_folded` 的小时才会把锚点以下的写入并到上级锚点。`Fold` 不修改输入，同样的输入得到同样的行。负责：Monitor 的 `WriteTreeFolder`（纯函数，fixtures 覆盖）。
- 估算增长不重复计算：同一个文件累计的增量 = 它最后的 MaxPos（删除时整体减回，行的合计回到 0）。负责：Monitor 的 `FileGrowthTracker`，fixtures 断言。
- received = 处理的 + 抽样跳过的 + 解析失败的 + 过滤掉的：Monitor 的统计类，单元测试断言。
- 不修改用户文件、不联网：Monitor 和 Monitor.Etw 的代码约定加评审；运行时由"只写 monitor.db、日志、报告和临时任务 XML"的设计保证。
- 进程实例不因进程号复用而混淆：Monitor 的 `ProcessTable`（按创建时间配对，有序号时按序号）。

## 8. 测试和验证

### 8.1 单元测试（`tests/DiskThinner.Monitor.Tests`，CI 上跑）

- `TemplateConformanceTests`：读 `tests/fixtures/monitor/templates.txt`，按字段类型拼字节，检查解析器每个（事件号, 版本）用到的字段（第 5.4 节）。
- `PayloadParserTests`：模板之外的边界：32 位指针、截断的载荷、没有 NUL 的字符串、子授权数 0 和 15 的 SID、超限的 SID、未知事件号、未知版本。
- `DevicePathTests`：设备前缀换盘符、不认识的前缀、`\Device\Mup`、大小写；`LongPathResolver` 用假解析器测短名换长名、缓存、失败时保留原样。
- `ProcessTableTests`：开始时的快照和事件合并、进程号复用、父进程已退出、未知进程号、退出进程的淘汰、按序号匹配父进程。
- `AttributionTests`：第 5.5.3 节的四个例子、同名相邻合并、服务宿主、服务宿主的子进程、全是透明进程、链很长时的"…"、进程号 0 和 4。
- `AggregatorFixtureTests`：读 `tests/fixtures/monitor/*.json`（新格式，写进 `tests/fixtures/README.md` 的"monitor：写入事件汇总"一节）：给出锚点、预算、硬上限、进程列表、服务、按时间排列的事件（`create` / `mkdir` / `delete` / `rename` / `link` / `write`（带路径、偏移、大小、是否分页，运行器替它生成 NameCreate 和 FileKey）/ `close`（生成 NameDelete）/ `lost` / `process-start` / `process-stop`）和期望的行（小时、目录、写入进程、顶层应用、各计数）以及统计。用例至少有：跨小时、新文件写了又删（估算增长抵消）、写入 → 关闭 → 重开 → 追加（估算增长等于最终大小）、改名后继续写、同名文件删掉后重建、缓存写入后的分页回写（不重复）、只有分页写入的映射文件、追加写的哨兵偏移、坏偏移、关闭时删除（V12 两种结果各一个）、预算不够时合并（锚点不动）、有写入的锚点数比预算多（第 2、3 段，预算取 4、硬上限取 8，保持 fixture 小）、深于 32 层、自己的进程被过滤、未知 FileKey、抽样放大（最大写入位置不被跳过的写入更新）、丢失率。
- `WriteSamplerTests`：速率翻倍和减半的滞后、按 k 放大、降级条件。
- `WriteTreeFolderTests`：`Fold` 不修改输入（同一份计数器先后落盘两次，结果相同）、三段合并的边界、`budget_exceeded` 和 `anchors_folded` 的标记。
- `MonitorLockTests`（名字用 `Local\DiskThinner.Test.<guid>`）：线程 A 获取、线程池线程释放、再获取成功；持有时另一次获取返回 null；`Dispose` 调两次没事；一个线程拿到原始 `Mutex` 后不释放就结束，`TryAcquire` 按"遗弃"拿到；`ExistsElsewhere` 在持有和释放后的结果。
- `SessionLivenessTests`：自己在监控 / 别人在监控 / 没人监控 × 有无残留会话 × 列不出会话 × 各种未结束的行。
- `SessionDeadlineTests`：第 5.1.3 节的三种情况。
- `WatchdogTaskTests`：第 5.1.4 节。
- `MonitorStoreTests`：建库、当前小时的覆盖写、会话行在开始时插入、会话统计、意外退出的标记、按天数和大小保留、没人引用的 dir / image / chain 清理（dir 按树逐层删）、版本更新拒绝打开、第 5.8 节两个最坏情况的体积。
- `MemoryFootprintTests`：fileState、DirectoryTable、ProcessTable、计数器各按上限的 1/10 建（30,000 项、20,000 个节点、10,000 个进程、20,000 个计数器），用 `GC.GetAllocatedBytesForCurrentThread()` 量出每项的字节数，外推到上限后乘 1.5 的余量，断言合计 ≤ 64 MB；超出时调低上限。不在完整上限下分配（第 8.4 节）。
- `WriteIndexTests`：`Under` 和排除子路径（"其余部分"）、上级目录有写入的提示、文件热点用所在目录、排行的汇总和排序（含"系统"一行）、净减少时主要进程链的选法、覆盖时长的交集计算、轻量和精确混合的范围。
- `MonitorCommandTests`：子命令参数的解析和退出码 4；`KeyValueReport` 先写临时文件再替换。
- App 测试：
  - `GrowthUiTests` 用临时 monitor.db 验证写入进程列（文字和"实测"在同一格，读屏文字是"进程链 实测"）、覆盖卡片、排行；现有断言 `rows[0].Tag == "未监控"` 在没有监控数据时保持不变；
  - `ItemListTests`（新）：`TagColumn` 的读屏文字、没设 `TagColumn` 时行为不变、`rightAligned`；
  - 概览三种情况、`DetailPanel` 只有 ETW 证据时有"证据"标题、`ShowGroup` 不显示 ETW；
  - `SettingsTests` 加监控设置的读写和收回；
  - `MonitorDialogTests` 验证普通权限 / 管理员 / 另一个实例在监控 / 账户不一致 / 自动开始被账户不一致拦下五种状态的文字和按钮，所有控件有 AccessibleName；
  - `ContrastTests` 加"覆盖时间轴 Accent / TitleBar（图形 ≥ 3:1）""监控中圆点 Down / Surface（图形）""工具栏监控链接 Accent / Surface"，以及丢失率提示用的 WarnText / Card。

### 8.2 测试负载、提权测试和验收脚本

`tests/DiskThinner.WriteLoad`（控制台程序，和 `DiskThinner.LockHolder` 一样只给测试用）：

```
DiskThinner.WriteLoad.exe --dir <目录> --report <json>
  [--small 500] [--small-size 65536] [--rename 50] [--delete 100]
  [--big-size 134217728] [--chunk 1048576]
  [--append 50] [--reopen 50] [--delete-on-close 50] [--prealloc 20] [--short-path 20]
```

- `--dir` 必须在 `Path.GetTempPath()` 下、名字以 `DiskThinner-2a-` 开头，否则退出码 2，什么都不写。只在这个目录下写。
- 目录结构固定：
  - `small\`：`f{i}.bin` 共 `--small` 个，每个 `--small-size` 字节；再把前 `--rename` 个改名为 `r{i}.bin`，删除接下来的 `--delete` 个；
  - `big\big.bin`：`--big-size` 字节，每次写 `--chunk`（普通缓存写）；
  - `append\`：`a{i}.bin` 先写 4 KiB、关闭，再用 `FileMode.Append`（`FILE_APPEND_DATA`）追加两次 4 KiB；`o{i}.bin` 先写 4 KiB、关闭，再用 `FileMode.Open` 打开、移到末尾写 4 KiB；
  - `temp\`：`t{i}.tmp` 用 `FileOptions.DeleteOnClose` 创建，写 64 KiB 后关闭；
  - `prealloc\`：`p{i}.bin` 只 `SetLength(1 MiB)`，不写；
  - `LongDirectoryNameForShortPath\`：用 `GetShortPathNameW` 取它的短路径，通过短路径写 `s{i}.bin` 各 4 KiB；没有短名时跳过，报告里 `short_name` 为 null。
- 报告 JSON（System.Text.Json 源生成）：`pid`、`short_name`，以及按子目录的 `created_files`、`created_dirs`、`deleted`、`renamed`、`bytes_written`（Write 调用的字节之和）、`final_bytes`（结束时留下的文件长度之和）。
- 进程优先级继承父进程（脚本是 BelowNormal）。

提权测试：

- `EtwFactAttribute`（照 `TestVolumeFactAttribute`）：不是管理员或没设 `DT_ETW_TESTS=1` 时跳过。`EtwSessionTests`：开关会话；到时停止发生在计时器线程上、进程没有崩溃、之后能再开始一次；残留清理（开会话后不停，另一个 `DiagnosticSession` 的清理能停掉它）；不碰名字不符合的会话（测试自己建一个 `DiskThinnerTest-Other` 会话，结束后自己停掉）；小负载（WriteLoad 用很小的参数）的端到端汇总。

验收脚本 `tools/verify/phase2a-elevated.ps1 -OutDir <目录> [-Part bench|accept|all] [-SkipWatchdog]`，`Start-Process pwsh -Verb RunAs` 启动（会弹 UAC，执行前告诉用户）。运行前先用普通权限 `dotnet build -c Release`。脚本开头把自己设成 BelowNormal。结果写 `<OutDir>\elevated.log` 和各步的报告：

- bench：第 5.2 节的实测。
- accept（各步顺序执行，同一时间只有一个 DiskThinner 进程）：
  1. 记录 `logman query -ets` 里已有的 `DiskThinner-*`（应该没有）；记下默认 monitor.db 的修改时间（不存在就记"不存在"）。
  2. 到时：`monitor --seconds 30 --db <OutDir>\mon-2.db --report <OutDir>\r-2.txt`；退出码 0、`end_reason=timeout`、`session_stopped=true`。
  3. 强制结束：`monitor --seconds 600 --db <OutDir>\mon-3.db --report <OutDir>\r-3.txt`，等报告里出现 `started=1` 后 `Stop-Process -Force`，确认会话还在；`monitor --cleanup --db <OutDir>\mon-3.db --report <OutDir>\r-3c.txt` 后 `residual_stopped=1`、`orphans_marked=1`，会话不在，mon-3.db 里这次会话标"程序意外退出"。
  4. 兜底任务（D7，`-SkipWatchdog` 时跳过，要等约 7 分钟）：`monitor --seconds 60 --db <OutDir>\mon-4.db --report <OutDir>\r-4.txt`，`started=1` 后 10 秒强制结束，等到计划结束 + 6 分钟，确认会话不在；然后 `monitor --cleanup --db <OutDir>\mon-4.db` 删掉任务。V20 的"过期后自动删除"在 2a.4 用 2 分钟的过期时间单独测一次，结论写进报告。
  5. 负载核对：在 `%TEMP%\DiskThinner-2a-<guid>\` 下先用 `snapshot --drive <这个目录> --db <OutDir>\snap.db` 存一个快照；后台 `monitor --seconds 240 --mode precise --verify-parser --db <OutDir>\mon-5p.db --snapshot-db <OutDir>\snap.db --report <OutDir>\r-5p.txt`，等 `started=1`；用 `cmd.exe /c DiskThinner.WriteLoad.exe --dir … --report <OutDir>\load-5p.json` 跑默认参数；再存一个快照；等监控结束；读 mon-5p.db 按第 1.3 节第 7 条逐项比对，并记每秒事件数、CPU、内存、丢失率、负载耗时。轻量模式用新的 guid 目录和 `mon-5l.db` 再跑一次，只比对计数。
  6. 界面：用同一个目录和这几个库 `--scan <目录> --snapshot-db <OutDir>\snap.db --monitor-db <OutDir>\mon-5p.db --settings <OutDir>\settings.json --user-db <OutDir>\user.db --page growth --screenshot …`、`--page settings`、`--page explorer --select <目录>\big`、`--show-monitor-dialog`；再用 `--monitor-seconds 20 --monitor-db <OutDir>\mon-6.db` 开着窗口 40 秒，确认窗口还开着时会话已经停了、没有新的崩溃报告，然后 `--exit-after-scan` 确认关窗口时停止。普通权限下的对话框截图在脚本外用普通权限跑一次。有第二块 150% 的显示器时加 `--secondary-screen` 再截一组。
  7. 每一步之后检查 `logman query -ets` 没有 `DiskThinner-Diag-`；删除 `%TEMP%\DiskThinner-2a-<guid>\`（先确认路径在 `Path.GetTempPath()` 下、名字含这次的 guid），不删除任何别的东西；最后断言默认 monitor.db 的修改时间没变（或者仍然不存在）。
- 截图和报告放在 `artifacts\phase2a\`（不提交）。

### 8.3 每次提交前

`pwsh -File tools\verify\run.ps1 test`（全量测试）、`run.ps1 release`、`dotnet run --project tools/i18n -- check src src/DiskThinner.Core/Strings.resx`、`dtrule validate rules`；最后一步推送后看 CI（含 windows-2022 / 2025 冒烟测试）。

### 8.4 验证的负载约束（用户要求）

- 不设计人为加压的测试：负载都是正常使用的规模（第 5.2 节的 W1–W3、WriteLoad 默认参数）；不做多窗口并发，不做压满 CPU 或磁盘的测试；制造丢失（V6）用小缓冲区加回调里睡眠，不增加负载；最坏情况的体积和内存在单元测试里用合成数据算，内存按 1/10 规模外推。
- 低于正常优先级：`phase2a-elevated.ps1` 和 `run.ps1` 开头把自己的进程设成 `BelowNormal`，启动的 dotnet、testhost、EtwBench、WriteLoad、DiskThinner 都继承（Windows 上父进程是低于正常时，子进程默认继承这个优先级）。
- 一次一个测试进程：`run.ps1 test` 给 `dotnet test` 加 `-m:1`，测试项目逐个跑，同一时间只有一个 testhost；提权脚本的各步顺序执行。被观测的负载（WriteLoad、编译）和监控进程同时运行是测试本身需要的，除此之外不并行。
- 最多 3 轮：每个子步骤的"修改 → 构建测试 → 提权验证"最多 3 轮；第 3 轮仍不通过就停下，把现象、数据和建议报告给用户。实测的重复也不超过 3 轮（第 5.2 节）。
- 需要管理员的脚本会弹 UAC，运行前告诉用户。

## 9. 子步骤

| 步骤 | 内容 | 验证 | 提交点 |
|---|---|---|---|
| 2a.0 | 本设计文档；按第 3.3 节改原型 | 设计评审通过；用户确认第 10 节的决策点和原型 | 提交"阶段 2a：设计文档和写入监控原型" |
| 2a.0a（0.1 人日） | `run.ps1`：BelowNormal、`dotnet test -m:1`（第 8.4 节） | `run.ps1 test` 全部通过 | 单独提交"验证脚本：低于正常优先级运行，测试项目逐个跑" |
| 2a.0b（0.4 人日，和监控无关，可以随时做） | 第 5.12 节：先加复现用例，确认后修 | 复现用例改之前失败、改之后通过；全量测试 | 单独提交"可访问性：方块图的帮助文字不再被路径提示顶替"（复现不了时提交"已知问题：方块图帮助文字的复现方法"） |
| 2a.1 开销实测（2 人日） | TraceEvent 4.0.0 加进 Directory.Packages.props；`spikes/EtwBench`、`tests/DiskThinner.WriteLoad`（都进解决方案）；`tools/verify/export-etw-templates.ps1` 和 `tests/fixtures/monitor/templates.txt`；`phase2a-elevated.ps1 -Part bench`；核对 V1–V19、V21；报告草稿的"开销实测"一节；按 D3 定 2b 默认模式和 2a 对话框默认模式；有需要时回头修订本文档（例如 V2 不成立时改写入量口径，V12 决定"关闭时删除"规则是否启用，V19 决定进程列举方式） | 提权脚本跑完；构建和全量测试通过 | 提交"阶段 2a.1：ETW 开销实测"（实测原始数据不提交，数字写进报告） |
| 2a.2 Monitor 库（2.5 人日） | `DiskThinner.Monitor`：解析和模板核对、设备路径和短名、进程表和归属、DirectoryTable、FileGrowthTracker、汇总和合并、抽样和统计、MonitorLock、SessionLiveness、SessionDeadline、WatchdogTask 的 XML；Monitor.Tests 和 `tests/fixtures/monitor/` | 单元测试和 fixtures 全过；AOT 分析没有警告 | 提交"阶段 2a.2：写入监控的汇总逻辑" |
| 2a.3 存储和查询（1 人日） | `MonitorStore`、保留、`WriteIndex` | 单元测试（含两个最坏情况的体积测试和内存外推） | 可以和 2a.2 合成一次提交 |
| 2a.4 ETW 会话（2 人日） | `DiskThinner.Monitor.Etw`（DiagnosticSession、MonitorRunner）、`monitor` 子命令和报告、崩溃停止、残留清理、兜底任务注册（V20）；`Directory.Build.targets` 和原生 DLL 处理；`build-release.ps1` 的文件清单；`EtwSessionTests`；smoke.ps1 加 monitor 项；`phase2a-elevated.ps1 -Part accept` 的 1–5、7 | 提权脚本通过；本机打两种包、核对文件清单并跑 smoke.ps1 | 提交"阶段 2a.4：ETW 诊断会话" |
| 2a.5 界面（2.5 人日，原型确认后才开始） | MonitorHost、对话框、工具栏和状态栏、设置、增长分析、概览、空间浏览详情、`ItemList` 的标签列、自动化参数；resx；对比度和可访问性 | App 测试、UiAutomationTests、ContrastTests、i18n check；`-Part accept` 的第 6 步截图并逐张检查布局 | 提交"阶段 2a.5：写入监控的界面" |
| 2a.6 收尾（0.5 人日） | `docs/phase2a-report.md`、dev-plan 阶段 2 下的"> 日期 2a 已完成……"、known-issues、README；推送 | CI 全绿（含冒烟测试） | 提交"文档：阶段 2a 报告"，推送 |

合计约 11 人日（含 0.5 人日和监控无关的 2a.0a、2a.0b），比方案的 6 到 8 人日多。多出的是：兜底任务、原始载荷解析和模板核对（2b 直接复用）、按文件的估算增长，以及评审补上的会话状态判定、合并的终止条件和打包核对。

## 10. 待用户确认的决策点

> 2026-10-26 用户确认：全部按推荐（D1 A、D2 B、D3 用第 5.2 节的数、D4 A、D5 A、D6 B、D7 A、D8 A、D9 A、D10 A、D11 A、D12 A、D13 A、D14 A、D15 同意）。原型按第 3.3 节和第 12.2 节第 7、15 条改好（prototype/index.html）。

| # | 问题 | 选项 | 推荐 |
|---|---|---|---|
| D1 | "顶层应用"取哪一层 | A 最外层的非壳程序（uv ← pwsh ← Code 记为 Code.exe）；B 最近的非运行时程序（记为 uv.exe） | A，和方案的"顶层"一致；写入进程和进程链也存下来，界面两样都显示 |
| D2 | 对话框默认模式 | A 一直按原型"轻量（推荐）"；B 按 2a.1 的实测：满足 D3 的四条就默认精确 | B |
| D3 | 开销阈值和判定规则 | 第 5.2 节的四条（空闲 CPU ≤ 0.5% / 进程内存 ≤ 64 MB；合成负载丢失 ≤ 0.1% 且逐项比对通过；编译丢失 ≤ 1% / 变慢 ≤ 10% / 单核 ≤ 50%；会话缓冲区轻量 ≤ 16 MB、精确 ≤ 32 MB）；或者你给出别的数 | 第 5.2 节的数 |
| D4 | 监控数据存哪 | A 单独的 monitor.db；B snapshots.db 里加表 | A |
| D5 | 保留多久 | A 90 天 + 64 MB 上限（设置可选 30 / 90 / 180）；B 跟快照一样 180 天 | A |
| D6 | 目录合并 | A 严格按方案只合并到快照节点；B 快照节点是下限，每小时 1,000 行的预算内保留更细的目录（锚点本身超出时最多 2,000 行，再多才合并锚点并标出） | B（A 对不上监控期间新出现的增长目录） |
| D7 | 强制结束时的兜底 | A 一次性计划任务（SYSTEM，计划结束 + 5 分钟停会话，用电池时也运行、错过了醒来后补跑，正常结束时删除，过期 1 天后自己删除）；B 不加，只靠崩溃处理和下次启动清理，强制结束后会话可能一直残留到重启或下次以管理员打开 | A（"一定停止"只有 A 能做到；代价是监控期间多一个计划任务，对话框里说明） |
| D8 | 精确模式显示哪种字节数 | A 估算增长为主、写入量为辅（第 5.6 节）；B 只显示写入量 | A |
| D9 | 事件太多时自动降到轻量 | A 降级并在会话里标明；B 只抽样，不降级 | A |
| D10 | TraceEvent 的原生文件 | A 只保留实测需要的（目标是一个都不带）；B 照包里的默认全部带上（多约 2.5 MB，单文件版可能有路径问题） | A |
| D11 | 工程拆分 | A Monitor（AOT，纯逻辑和存储）+ Monitor.Etw（TraceEvent）两个项目；B 只一个 Monitor 项目，用命名空间分开 | A（B 让 Monitor 不能 AOT，2b 没法直接引用） |
| D12 | 设置里"关闭"在 2a 的含义 | A 不显示开启诊断的提示，工具栏点击跳到设置；B 和"临时诊断"一样，只是默认不同 | A |
| D13 | 记录哪些盘 | A 所有有盘符的本地固定盘；B 只记系统盘 | A（事件在内核里对所有盘都会产生，只在存储上有差别） |
| D14 | `monitor` 子命令 | A 公开，README 写明是诊断和验证用；B 隐藏，只给脚本用 | A |
| D15 | 1.4 节：ETW 只作为证据，不改归属引擎和"按归属汇总" | 同意 / 2a 就要改 | 同意，留到 2b |

## 11. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| V2 不成立：缓存写入的回写分不出来，或者写事件的进程号不是发起者 | 写入量重复或归到 System | 2a.1 先核对；不成立时只用估算增长（按文件取最大写入位置，不受重复影响），写入量改为"仅供参考"并修订本文档 |
| 分页写入按页取整（V2） | 只有映射写入的新文件估算增长偏大（每个文件最多一页左右） | 有非分页写入的文件不用分页写入（第 5.6.2 节）；报告写明误差 |
| V3：长期打开的文件（数据库、浏览器缓存）对不上路径的比例高 | 精确模式下这些写入只能报"对不上目录" | 显示比例；新建、删除、重命名不受影响；报告里写明；V19 的 CaptureState 可能改善 |
| 监控期间用户关了窗口 | 监控提前结束 | 对话框和状态栏写明"关闭窗口会停止"；会话记录的结束原因 |
| 机器在监控期间睡眠 | 按墙钟超过 24 小时 | 每秒按墙钟检查，唤醒后 1 秒内停止；兜底任务错过时醒来补跑 |
| 精确模式在编译时事件暴增 | CPU 高、丢事件 | 抽样、降级、显示丢失率（第 5.3 节） |
| 实测在低于正常的优先级下进行 | 丢失率比正常使用时偏高 | 报告里写明数据偏保守；按 D3 判定时不额外放宽 |
| TraceEvent 在单文件版里有路径问题（V8） | 自包含版不能监控 | 2a.4 用冒烟测试覆盖；去掉不需要的原生文件；需要时 `ExcludeFromSingleFile` |
| 杀毒软件对"开 ETW 会话 + 注册 SYSTEM 计划任务"敏感 | 误报 | 只在用户点开始时做；任务动作是系统自带的 logman.exe；报告里记录 Defender 的表现 |
| 会话总数有上限（系统级） | 开不了会话 | 失败时如实提示；残留清理避免自己占着不放 |
| 方块图帮助文字的问题复现不了 | FR-10 修不了 | 按第 5.12 节记下复现方法，不盲改 |

## 12. 评审意见处理

### 12.1 第一轮（`artifacts/workflow/b-design-review.md`，CHANGES_REQUESTED：HIGH 1、MEDIUM 14、NIT 9）

| # | 级别 | 意见 | 处理 | 改了哪里 |
|---|---|---|---|---|
| 1 | HIGH | Mutex 在计时器线程和泵线程上释放会抛异常 | 采纳。互斥量封装成 `MonitorLock`，只在专用线程上获取和释放，`Dispose` 可以在任何线程调用，只发信号；`Stop` 用 `Interlocked` 只执行一次，各步骤单独捕获异常，锁在 finally 里释放；计时器回调整体 try/catch；残留清理也用 `MonitorLock`，不存在跨 `await` 换线程的问题。测试：`MonitorLockTests`（A 线程获取、线程池释放、再获取成功等）和 `EtwSessionTests`（到时停止发生在计时器线程上，之后能再开始） | 5.1.1、5.1.2、5.1.3、6、7、8.1、8.2，1.3 第 3 条 |
| 2 | MEDIUM | 普通权限分不清正在监控还是残留；断电后会话行一直是"进行中" | 采纳，规则略有调整：定义纯函数 `SessionLiveness`；不是 Live 时把全部未结束的行标为意外退出（没有实例在监控就不会再有人写这些行，残留会话的那一行在普通权限下也能及时标出），Live 时只标名字不在活动会话里的行，列不出活动会话时 Live 下不动。每次启动（任何权限）都做。普通权限分别显示"另一个窗口正在监控"和"上次没有正常结束" | 5.1.2、5.9.2、5.9.3，3.3 P1、P7，1.3 第 5 条，6、7、8.1 |
| 3 | MEDIUM | 机器睡眠时 24 小时上限没有保证 | 部分采纳。截止检查放进本来就有的每秒统计计时器（比建议的 30 秒更及时），墙钟和 `Stopwatch` 经过时间任一到了就停；唤醒后 1 秒内停止，所以不再订阅 `PowerModeChanged`（多一个 `SystemEvents` 订阅没有额外收益）。时钟可注入，测试"跳过 30 小时"和"往回调时钟"。兜底任务 `StartWhenAvailable=true` 覆盖强制结束后又睡眠的情况 | 5.1.3、5.9.1，1.1 FR-2，1.3 第 3 条，8.1，11 |
| 4 | MEDIUM | 兜底任务的 XML 设置没写，默认值用电池时不运行 | 采纳。XML 模板写进设计（电池两项 false、`StartWhenAvailable`、`ExecutionTimeLimit` 5 分钟、SYSTEM、UTF-16 LE BOM、时间带偏移、logman 完整路径）；另外加了 `EndBoundary` + `DeleteExpiredTaskAfter` 让任务过期后自己删除（V20 核对，注册失败时去掉这两项重试）。默认值本机重新核对过，写进 2.1 | 5.1.4、2.1、2.2 V20、6、8.1、8.2，10 D7 |
| 5 | MEDIUM | `--start-monitor` 自动开始跳过了账户不一致的提示 | 采纳。账户不一致时新实例不自动开始，打开预填的对话框和黄色提示 | 5.1.5、5.9.2、5.9.8，1.1 FR-1，1.3 第 1 条，3.3 P7，8.1 |
| 6 | MEDIUM | 解析器只用自己拼的字节测；Kernel-Process 模板不完整 | 采纳。2.1 按本机重新导出的模板补全（含 ProcessStop v2 的序号字段、ProcessRundown）；新增导出脚本和 `templates.txt` fixture，`TemplateConformanceTests` 按它拼字节；`--verify-parser` 用 TraceEvent 的 TDH 解码做差分核对，冒烟测试要求 `parse_mismatch=0` | 2.1、4.1、5.4、5.9.8、5.11、8.1，1.3 第 15 条，9 |
| 7 | MEDIUM | V 清单缺 V12–V17，WriteLoad 没有覆盖 | 采纳。V12–V17 加进 2.2（另外补了 V18 BufferSizeMB、V19 CaptureState、V20 兜底任务 XML、V21 普通权限列会话）；WriteLoad 加关闭时删除、追加、重开追加、预分配、短路径（规模按负载约束取小）；1.3 第 7 条给出每种负载的期望；处理规则写进 5.6（"关闭时删除"只在 V12 显示没有 DeletePath 时启用，避免重复计数；短名用 `GetLongPathNameW` 换成长路径） | 2.2、5.6.2、5.6.3、8.2，1.3 第 7 条 |
| 8 | MEDIUM | 按 FileKey 存文件状态，关闭重开后重复计数 | 采纳。拆成 `fileKeyToName` 和 `fileState` 两张表；去掉了"是否本次新建"（fileState 只为 CreateNewFile 建）；DirId 来自会话级的 `DirectoryTable`，合并不改它，fileState 的键一直有效。fixtures 加"写、关闭、重开、追加""改名后继续写""同名删除后重建" | 5.6.1、5.7、7、8.1 |
| 9 | MEDIUM | 估算增长的输入规则不完整 | 采纳，调整一处：分页写入只在这个文件没有非分页写入时才更新最大写入位置（缓存回写按页取整，全部算进去会让小文件偏大，V2 加了这一问）。其余照建议：增量记到新建者的进程链；`checked` 计算，`0xFFFFFFFFFFFFFFFF` 按追加处理，其他异常值计 bad_offsets；抽样只乘写入量和次数，跳过的写入不更新最大位置 | 5.3、5.6.2、2.2 V2 和 V16、7、8.1 |
| 10 | MEDIUM | 锚点 × 进程链超预算时合并停不下来 | 采纳。三段合并；第 3 段只在超过硬上限时合并锚点并标 `anchors_folded`，到根为止，所以一定结束；`Fold` 在副本上做、只输出行；内存里的压缩只做第 1 段。不变量改成"≤ 预算，或 ≤ 硬上限并带标记" | 5.7、5.8、7、8.1，10 D6 |
| 11 | MEDIUM | 体积估算漏了 dir / chain / image 和索引，4 MB 达不到 | 采纳。dir 表改成和快照库 path 表一样的树结构；去掉 `write_stat_hour` 索引（查询先按会话筛，再走主键前缀）；按表估算体积；最坏情况 fixture 的参数写死。为了让 4 MB 成立，每小时预算从 2,000 行降到 1,000 行（热点分解一般只有几十个目录，1,000 行足够细），硬上限 2,000 行时目标 8 MB | 5.8、5.7，1.2，1.3 第 17 条，10 D6 |
| 12 | MEDIUM | 开销实测没测内核里的 ETW 缓冲区 | 采纳。bench 用 `ControlTraceW` 查询缓冲区，用 CIM 读非分页池的变化（不受系统语言影响）；1.2 把内存拆成进程增量和会话缓冲区；D3 加缓冲区上限（轻量 16 MB、精确 32 MB，待确认）；缓冲区初值改成 8 / 32 MB | 1.2、2.1、2.2 V18、5.2，10 D3 |
| 13 | MEDIUM | TraceEvent 的 props 也在 buildTransitive 里 | 采纳。改用仓库根目录的 `Directory.Build.targets`；`build-release.ps1` 递归统计并写文件清单；冒烟测试断言包里没有 `x86\`、`arm64\` | 2.1、4.1、5.11，1.3 第 15 条，9 |
| 14 | MEDIUM | monitor 子命令的报告、`--db` / `--snapshot-db`、WriteLoad 接口没定义；验收会写真实的 monitor.db | 采纳。定义子命令、报告的键和写入时机（先写临时文件再替换）、退出码 4；会话行在开始时插入；验收各步都带 `--db`，脚本核对默认 monitor.db 没被改动；`--snapshot-db` 只读、不存在不创建；WriteLoad 的参数、目录结构和报告 | 5.8、5.9.8、8.2，1.3 第 6、7 条 |
| 15 | MEDIUM | ItemList 不能在同一格画文字和标签，Note 只是工具提示 | 采纳。`ListItem` 加 `TagColumn`，`ItemList` 加 `rightAligned`；"第二行"改成工具提示；概览写明三种情况（第 2 种再分"范围内没有监控"和"监控过但这里没写入"，后者不加"未监控"标签，避免误导） | 5.9.4、5.9.5，3.3 P3、P5，1.3 第 9、12 条，8.1 |
| 16 | NIT | CI 是否管理员前后矛盾；FR-9 节号写错 | 采纳 | 1.1 FR-9，1.3 第 13、15 条 |
| 17 | NIT | DetailPanel 的证据标题、ShowGroup、WriteIndex 的传入 | 采纳 | 5.9.6，1.3 第 10 条，8.1 |
| 18 | NIT | 服务宿主的子进程、SetLinkPath 的计数列、净减少热点的进程链 | 采纳 | 5.5.3、5.6、5.9.4 |
| 19 | NIT | "不打开任何用户文件"和 ExeIdentityReader 冲突；日志不记路径 | 采纳 | 1.2、5.4、5.10、6 |
| 20 | NIT | 原型里的系统写入和 vmmem / VHDX 行 | 采纳。进程号 0 和 4 显示为"系统（缓存回写或内核）"；vmmem 行保留：规则里已有 `vm-disk` 类别，实现能给出"虚拟磁盘，看不到内部是谁写的"，原型里把归属列的标签改成写入进程列里的文字（一格只画一个标签） | 3.2、3.3 P5、5.5.1、5.9.4 |
| 21 | NIT | WinExe 下 `--status` 看不到输出；同一秒内重名 | 采纳 | 5.9.8、5.1.1 |
| 22 | NIT | 处理速率的测法、W3 内存峰值、最坏情况内存测试 | 部分采纳。回放测法和 W3 记内存峰值照做；最坏情况内存不在完整上限下分配（用户要求不做人为加压），改成 1/10 规模实测后外推 | 5.2、8.1、8.4，1.3 第 17 条 |
| 23 | NIT | 可选：CaptureState / ProcessRundown、序号匹配、SID 来源 | 部分采纳。CaptureState 列为 V19 在 2a.1 试验，默认实现仍是 Toolhelp32，试验结果好再换并修订文档；序号匹配父进程采纳；SID 布局注明 `winnt.h` | 2.1、2.2 V19、5.4、5.5.1 |
| 24 | NIT | EtwBench 和 WriteLoad 放不放进 slnx | 采纳。EtwBench 进 `/spikes/`（和现有 spike 一样由 CI 构建，保证能编译），WriteLoad 进 `/tests/` | 4.1、5.11 |

不是评审意见、本轮一并改的：

- 按用户对验证负载的要求改了实测和验证（第 1.2、5.2、8.4 节）：W2 缩小到约 170 MB、耗时不参与判定；W3 单节点编译；所有测试进程低于正常优先级、一次一个；"修改 → 验证"最多 3 轮；`run.ps1` 的调整单独作为 2a.0a 提交。
- 附带修复方块图帮助文字（FR-10、第 5.12 节、2a.0b、1.3 第 19 条）：用 ILSpy 看了 WinForms 的 HelpText 取值，确认不能靠推测直接改，先复现。
- 新增 `MonitorRunner`（第 5.9.1 节），界面和 `monitor` 子命令共用会话、落盘和停止的逻辑，避免两份实现。
- 子步骤的工作量改成约 11 人日（第 9 节）。

### 12.2 第二轮（`artifacts/workflow/b2-design-review.md`，CHANGES_REQUESTED：HIGH 0、MEDIUM 7、NIT 14）

本轮意见的处理直接写在这里，作为对正文的修订；和正文冲突时以本节为准，实现时把对应正文一并改掉。

| # | 级别 | 意见 | 处理 |
|---|---|---|---|
| 1 | MEDIUM | 停止流程跨 `DiagnosticSession` 和 `MonitorRunner`，回调、落盘串行化、重复调用没定义 | 采纳评审给出的接口：`DiagnosticSession` 有 `Action<StopReason>? Stopping`（等泵线程结束之后、删兜底任务之前，在调用 Stop 的线程上同步调用）、`Task Stopped`、`Task Stop(StopReason)`（只执行一次，之后的调用返回同一个 Task）。兜底任务的注册和删除放在 `DiagnosticSession`。`MonitorRunner.FinalFlush`：先 `_flushTimer.Dispose(waitHandle)` 等计时器回调退出；所有落盘共用一个 `SemaphoreSlim(1)`；在汇总器的锁里复制计数器后写行和 `ended_at`、`end_reason`；整体放进 `Task.Run`，调用方最多等 3 秒，超时记警告继续。`MonitorHost.Stop` 和 `OnFormClosing` 等 `Stopped` 最多 4 秒。`MonitorHost` 用 `System.Windows.Forms.Timer` 每 2 秒轮询 `MonitorStatus`，`DataChanged` 只在界面线程上发。测试：计时器线程正在停止时界面线程再调 Stop，拿到同一个 Task，`end_reason=timeout`；假会话验证定时落盘和最后一次落盘不交错、最后写入的是最新计数 |
| 2 | MEDIUM | 实时会话的事件不保证按时间顺序到达 | 采纳。Monitor 加纯类 `ReorderBuffer`：事件按时间戳进窗口，早于"已收到的最大时间戳 − W"的按时间顺序放行；W 初值 2 秒，2a.1 按新增的 V22 定；窗口最多 100,000 个事件，满了按到达顺序放行并计 `reorder_overflow`；停止时清空。文件事件匹配"进程号相同、CreateTime ≤ 事件时间、已退出实例的 ExitTime ≥ 事件时间"的进程实例，都没有才归"未知进程（PID n）"。V22：W2、W3 下统计文件事件早于自己进程 ProcessStart 到达的比例、时间戳倒序的最大间隔、写入早于 NameCreate 到达的比例。fixtures 加"文件事件先于 ProcessStart""写入先于 NameCreate"；`ReorderBufferTests` 覆盖窗口边界和溢出 |
| 3 | MEDIUM | 改名规则依赖 V4；没处理跨目录移动、覆盖已有文件、目录改名 | 采纳。V4 两种结果各一条规则：RenamePath 的 FilePath 是新名字时照现有规则；是旧名字时新名字取同一 FileKey 随后 2 秒内的 NameCreate（借重排窗口），取不到时只计重命名数并标 flags 64"改名后的估算增长不完整"。跨目录移动：把这个文件已累计的值从（旧目录, CreatorChain）移到（新目录, CreatorChain），记在当前小时。目标已存在：先把目标那一项按删除处理（减回累计值，不计删除数），再移动。目录改名：`DirectoryTable.Move(dirId, newParentId, newName)` 原地改父节点和名字，DirId 不变；新路径节点已存在时把旧节点并进去，旧 DirId 记为别名；已封存的小时不受影响。判定是目录：旧路径在 DirectoryTable 里是目录节点，或由带 `FILE_DIRECTORY_FILE` 的 CreateNewFile 建立。WriteLoad 加 `move\`（写到 `move\tmp\` 再移到 `move\dst\`；`move\stage\` 整体改名为 `move\done\`），期望 `dst\`、`done\` 的估算增长等于最终大小，`tmp\`、`stage\` 为 0；fixtures 加对应三个用例 |
| 4 | MEDIUM | fileKeyToName 没有上限，内存预算没算它 | 采纳。上限 400,000 项，最近最少使用淘汰，计 `name_map_evicted`（flags 128），被淘汰键的写入算 unresolved；加进 1.3 第 17 条和 `MemoryFootprintTests`（1/10 规模 40,000 项）。2a.1 记录 W1、W3 下 fileKeyToName 和 DirectoryTable 的峰值项数和每小时净增长并外推 24 小时；峰值超过上限一半时改为延迟建目录节点（NameCreate 只存目录部分的驻留字符串和名字哈希，第一次有写入或被新建 / 删除 / 改名用到时才建节点），由 V15 的结果决定 |
| 5 | MEDIUM | WriteLoad 的 `FileMode.Append` 不请求 `FILE_APPEND_DATA`，V16 的哨兵偏移测不到 | 采纳。`a{i}.bin` 用 `[LibraryImport]` 的 `CreateFileW(path, 0x00100004 /* FILE_APPEND_DATA | SYNCHRONIZE */, FILE_SHARE_READ, null, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, 0)` 加 `WriteFile` 写两次 4 KB；另加 `c{i}.bin` 用 `cmd.exe /c "echo … >> c{i}.bin"`。报告 JSON 按追加方式分别记 `bytes_written`；V16 写清哪种方式出现了哨兵值 |
| 6 | MEDIUM | 2a.1 的实测要用 2a.2 才写的组件，D3 在 2a.1 定不下来 | 采纳方案 A。2a.1 只测原始开销（每秒事件数、CPU、内存、会话缓冲区、丢失率、负载耗时）和 V 清单，EtwBench 同时把 W2、W3 的原始事件存到 `artifacts\phase2a\raw-*.bin`（本机，含路径，不提交）。2a.2、2a.3 完成后用 `EtwBench --replay`（普通权限，不再弹 UAC）离线回放，得出处理速率、W2 的逐项比对和抽样阈值 R，再按 D3 定默认模式。第 9 节"按 D3 定默认模式"移到 2a.3 之后、2a.5 之前，写进那一步的提交说明 |
| 7 | MEDIUM | 对不上目录的写入和轻量模式让"没有写入"的说法不成立，排行漏掉写得最多的进程 | 采纳。对不上目录的写入按进程链保留在 `write_stat`，`dir_id = 0` 表示"对不上目录"（dir 表不建这行，合并不参与，`WriteIndex.Under` 永远不返回它）；排行的写入量包含它们，主要目录列写"对不上目录（监控前已打开的文件）"。"没有写入"的文字按三种情况写死，增长分析、概览、空间浏览共用：范围内只有轻量模式的小时→"监控的 N 小时里这里没有新建、删除或重命名（轻量模式不记录写入量）"；有精确模式的小时且 unresolved_bytes ≥ write_bytes 的 1% 或 ≥ 100 MB→"监控的 N 小时里没有记录到能对上这里的写入；另有 X 的写入对不上目录（监控开始前已打开的文件）"；其余→"监控的 N 小时里没有写入"。原型 P5 的 vmmem 行换成排行里一行"对不上目录"的例子。`WriteIndexTests`、`GrowthUiTests` 和 DetailPanel 的测试加这三种情况 |
| 8 | NIT | MonitorLock 专用线程只捕获两种异常 | 采纳：线程体 `catch (Exception)` 记日志并 `TrySetResult(false)`；`Join` 成功后 `_release.Dispose()` |
| 9 | NIT | SessionLiveness 的读取顺序 | 采纳：先读未结束的会话行，再查锁和活动会话；`UPDATE` 加 `AND last_flush = ?` |
| 10 | NIT | 合并第 3 段的不变量和小规则 | 采纳：第 3 段每次合并后对合并后的节点重做 N = 1 的第 2 段；内存压缩压到 100,000 项以下；封存后才到的事件记到当前小时并计 `late_events`；建库时插入 chain id 0 的保留行 |
| 11 | NIT | 打包细节 | 采纳：`DropTraceEventNatives` 同时按 `%(None.Link)` 去掉 `Dia2Lib.dll`、`TraceReloggerLib.dll`；5.11 写明 `-r win-x64` 本来只复制 `amd64\`，发布包上核对的是 `amd64\` 按 V7 处理后的结果；V11 改成"任何 NU* 警告" |
| 12 | NIT | 锚点由谁读、"不存在时不创建" | 采纳：Monitor.Etw 定义 `IAnchorSource { IReadOnlyCollection<string> Load(); }`，App 和 `MonitorCommand` 实现（`File.Exists` 为 false 时只返回盘根，否则用 `SnapshotStore` 读）；主窗口保存快照后调用 `MonitorRunner.ReloadAnchors()` |
| 13 | NIT | 兜底任务余量 | 采纳：余量改成 1 分钟；FR-2 写明"程序被强制结束时最长 24 小时 1 分钟，睡眠中错过的在醒来后补跑"；用电池时的实机行为不在自动验收里 |
| 14 | NIT | 按大小保留看实际占用的页 | 采纳：按 `(page_count − freelist_count) × page_size` 判断；测试断言超出上限时只删最早的一个会话 |
| 15 | NIT | 界面细节 | 采纳：概览统一显示"写入进程 ← 顶层应用"，完整进程链放提示；排行写出 `fixedWidths [150, 130, 70, 80, 160]`；`ItemList.SetFooter(string? text, string? linkText, Action? onLink)`，链接有 AccessibleName；`WriteIndex` 保留 (session_id, dir, chain) 维度，小时桶 [h, h+1) 和 [from, to) 有交集就算；只有组的汇总详情（`ShowGroup`）不显示 ETW，选中具体节点照常显示 |
| 16 | NIT | 自动开始和开始流程的分支 | 采纳：设置为"关闭"时 `--start-monitor` 仍按参数执行；`AlreadyRunning` 时状态栏显示"另一个 DiskThinner 窗口正在监控，没有开始"；开始监控在后台任务里做，工具栏显示"正在开启…"；`monitor --cleanup` 普通权限下先标 Orphans 再返回 2；8.2 第 6 步参数写成 `--monitor-seconds 600 --monitor-db <OutDir>\mon-6b.db --exit-after-scan`，检查 `end_reason=app-exit` |
| 17 | NIT | 会话被外部停止 | 采纳：泵线程返回时不在停止过程中就调用 `Stop(StopReason.External)`，`end_reason` 加 `external`，状态栏显示"写入监控被外部停止" |
| 18 | NIT | 泵线程上不做文件读取 | 采纳：产品名和公司名在落盘线程写 image 行时再读；长路径解析缓存未命中时泵线程先按短名记，交给后台线程解析，落盘前替换 |
| 19 | NIT | 提权测试和真实会话的隔离 | 采纳：会话名前缀和互斥量名都可注入，测试用 `DiskThinnerTest-Diag-` 和 `Local\DiskThinner.Test.<guid>` |
| 20 | NIT | 低于正常优先级的落实 | 采纳：`run.ps1` 的 build 和 test 加 `-nodeReuse:false`，进程设 BelowNormal |
| 21 | NIT | 研发方案 1.3 的性能目标 | 采纳：`MonitorHost` 延迟创建 `MonitorRunner`，第一次需要时才加载 TraceEvent；报告记录冷启动时间、两个包的总大小、"监控中 + MFT 扫描"的峰值内存，和 1.3 的目标对照 |

不是评审意见、本轮一并改的：用户要求后续不再用工作流，本节由主会话直接处理，不再做第三轮独立评审；第 10 节的决策点交用户确认后进入实现。
