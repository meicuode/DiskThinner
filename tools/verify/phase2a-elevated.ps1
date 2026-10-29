# 阶段 2a 需要管理员的实测和验收（docs/phase2a-design.md 5.2、8.2 节；以管理员身份运行，会先弹出 UAC）。
#   -Part bench：ETW 开销实测和 V 清单（2a.1）。三种配置（不开会话 / 轻量 / 精确）× 三种负载
#     W1 空闲、W2 测试写入负载（DiskThinner.WriteLoad）、W3 编译本仓库的主程序；另外测 V6（制造丢失）、V7（不带原生 DLL）、
#     V9（进程被强制结束后会话残留，logman 能否清掉）、V21（普通权限列会话，由普通权限的一侧配合）。
# 负载约束（8.4 节）：本脚本设为低于正常优先级，子进程继承；同一时间只有一个 EtwBench 和一个负载。
# 只删除测试自己在 %TEMP%\DiskThinner-2a-* 下建的目录（EtwBench 自己删）。运行前先用普通权限构建：
#   dotnet build spikes/EtwBench -c Release; dotnet build tests/DiskThinner.WriteLoad -c Release; dotnet restore src/DiskThinner.App
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File','tools\verify\phase2a-elevated.ps1','-OutDir','<目录>'
#   -Part accept：2a.4 的验收（8.2 节 accept 的第 1–5、7 步）：monitor 子命令的到时停止、强制结束后的清理、兜底任务（-SkipWatchdog 跳过）、
#     负载下的精确和轻量模式，再以 DT_ETW_TESTS=1 跑 EtwSessionTests（-Part etw 只跑这些测试）。运行前先用普通权限构建：
#     dotnet build src/DiskThinner.App -c Release; dotnet build tests/DiskThinner.Monitor.Tests -c Release
#   -Part perf：1.3 节第 20 条的性能对照（-Before / -After 两个发布目录）。
#   -Part ui：2a.5 的界面验收（8.2 节 accept 第 6 步）：调用 phase2a-ui.ps1，截有监控数据的各页面，验证到时停止和关窗口停止。
param(
    [Parameter(Mandatory)] [string] $OutDir,
    # 可以用逗号连着写几个部分（例如 etw,ui），只弹一次 UAC
    [ValidatePattern('^(bench|bench2|accept|etw|ui|perf)(,(bench|bench2|accept|etw|ui|perf))*$')] [string] $Part = 'bench',
    [int] $IdleSeconds = 180,
    [switch] $SkipWatchdog,
    # -Part perf 用：加 TraceEvent 之前和现在的发布目录（build-release.ps1 的输出）
    [string] $Before,
    [string] $After
)

$ErrorActionPreference = 'Stop'
$parts = $Part -split ','
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$bench = Join-Path $root 'spikes\EtwBench\bin\Release\net10.0-windows\EtwBench.exe'
$load = Join-Path $root 'tests\DiskThinner.WriteLoad\bin\Release\net10.0-windows\DiskThinner.WriteLoad.exe'
$project = Join-Path $root 'src\DiskThinner.App\DiskThinner.App.csproj'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$env:PATH = (Split-Path $dotnet) + ';' + $env:PATH

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'elevated.log'
Set-Content $log "start $(Get-Date -Format s)"

function Step($name, [scriptblock] $body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        Add-Content $log "$name ok $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
    } catch {
        Add-Content $log "$name failed: $_"
    }
}

function Bench([string] $name, [string[]] $arguments, [string] $exe = $bench) {
    $report = Join-Path $OutDir "$name.txt"
    Remove-Item $report -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList (@('run') + $arguments + @('--out', "`"$report`"")) -NoNewWindow -PassThru -Wait `
        -RedirectStandardError (Join-Path $OutDir "$name.err")
    Add-Content $log "$name exit=$($p.ExitCode)"
    if ($p.ExitCode -ne 0) { throw "EtwBench 退出码 $($p.ExitCode)" }
}

function DiskThinnerSessions { @(logman query -ets | Select-String -Pattern 'DiskThinner' | ForEach-Object { ($_.Line -split '\s+')[0] }) }

if ($parts -contains 'bench') {
    Add-Content $log "sessions_before=$((DiskThinnerSessions) -join ',')"

    # W1 空闲（不开会话时没有要测的，只测两种模式）
    foreach ($mode in 'light', 'precise') {
        Step "w1-$mode" { Bench "w1-$mode" @('--mode', $mode, '--workload', 'idle', '--seconds', $IdleSeconds) }
    }

    # W2 测试写入负载
    foreach ($mode in 'none', 'light', 'precise') {
        $extra = if ($mode -eq 'precise') { @('--raw', "`"$(Join-Path $OutDir 'raw-w2.bin')`"", '--raw-max', '300000') } else { @() }
        Step "w2-$mode" { Bench "w2-$mode" (@('--mode', $mode, '--workload', 'writeload', '--writeload', "`"$load`"") + $extra) }
    }

    # W3 编译（先还原，不计入）
    Step 'w3-restore' { & $dotnet restore $project -nologo -v q | Out-Null }
    foreach ($mode in 'none', 'light', 'precise') {
        $extra = if ($mode -eq 'precise') { @('--raw', "`"$(Join-Path $OutDir 'raw-w3.bin')`"", '--raw-max', '200000') } else { @() }
        Step "w3-$mode" { Bench "w3-$mode" (@('--mode', $mode, '--workload', 'build', '--project', "`"$project`"") + $extra) }
    }

    # V6：1 MB 的小缓冲区加回调里忙等，制造丢失（不增加负载）
    Step 'v6' { Bench 'v6-lost' @('--mode', 'precise', '--workload', 'writeload', '--writeload', "`"$load`"", '--buffer-mb', '1', '--slow-us', '300') }

    # V7：不带 TraceEvent 的原生 DLL 和 Dia2Lib / TraceReloggerLib 能否开关会话、收事件
    Step 'v7' {
        $src = Split-Path $bench
        $copy = Join-Path $env:TEMP ('DiskThinner-2a-v7-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        Copy-Item $src $copy -Recurse
        Remove-Item (Join-Path $copy 'amd64'), (Join-Path $copy 'x86'), (Join-Path $copy 'arm64') -Recurse -Force
        Remove-Item (Join-Path $copy 'Dia2Lib.dll'), (Join-Path $copy 'TraceReloggerLib.dll') -Force
        try {
            Bench 'v7-no-natives' @('--mode', 'light', '--workload', 'writeload', '--writeload', "`"$load`"") (Join-Path $copy 'EtwBench.exe')
        } finally {
            Remove-Item $copy -Recurse -Force
        }
    }

    # V9 + V21：开会话后强制结束进程，看会话是否残留、logman 能否清掉；会话开着时等普通权限的一侧列一次会话
    Step 'v9' {
        $name = 'DiskThinnerBench-orphan-' + (Get-Date -Format 'yyyyMMddHHmmss')
        $marker = Join-Path $OutDir 'orphan-started.txt'
        $v21done = Join-Path $OutDir 'v21-done.txt'
        Remove-Item $marker, $v21done -ErrorAction SilentlyContinue
        $p = Start-Process $bench -ArgumentList 'orphan', '--session', $name, '--out', "`"$marker`"", '--seconds', '600' -PassThru -WindowStyle Hidden
        for ($i = 0; $i -lt 60 -and -not (Test-Path $marker); $i++) { Start-Sleep -Milliseconds 500 }
        if (-not (Test-Path $marker)) { throw '孤儿会话没有开起来' }
        Add-Content $log "v9_session=$name"
        for ($i = 0; $i -lt 120 -and -not (Test-Path $v21done); $i++) { Start-Sleep 1 }
        Add-Content $log "v21_waited=$(Test-Path $v21done)"
        Stop-Process -Id $p.Id -Force
        Start-Sleep 2
        $alive = (logman query -ets | Out-String) -match [regex]::Escape($name)
        Add-Content $log "v9_session_after_kill=$alive"
        # 不管上面判断成什么，都试着停一次（测试自己开的会话，名字是这次生成的）
        logman stop $name -ets | Out-Null
        Start-Sleep 1
        Add-Content $log "v9_session_after_logman_stop=$((logman query -ets | Out-String) -match [regex]::Escape($name))"
    }

    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',')"
    $left = @(Get-ChildItem $env:TEMP -Directory -Filter 'DiskThinner-2a-*' -ErrorAction SilentlyContinue).Count
    Add-Content $log "leftover_dirs=$left"
}

if ($parts -contains 'bench2') {
    # 第二轮（5.2 节"离阈值不到 20% 时最多再跑 2 次"）：编译耗时按交替的顺序再测，抵消缓存和先后顺序的影响；
    # V6 用空闲时的事件（有突发）加更慢的回调制造丢失；V9 用修正后的判断再测一次
    Step 'w3-restore' { & $dotnet restore $project -nologo -v q | Out-Null }
    $i = 0
    foreach ($mode in 'precise', 'none', 'light', 'none', 'precise', 'light') {
        $i++
        Step "w3b-$i-$mode" { Bench "w3b-$i-$mode" @('--mode', $mode, '--workload', 'build', '--project', "`"$project`"") }
    }

    Step 'v6b' { Bench 'v6b-lost' @('--mode', 'precise', '--workload', 'idle', '--seconds', '90', '--buffer-mb', '1', '--slow-us', '3000') }

    Step 'v9b' {
        $name = 'DiskThinnerBench-orphan-' + (Get-Date -Format 'yyyyMMddHHmmss')
        $marker = Join-Path $OutDir 'orphan2-started.txt'
        Remove-Item $marker -ErrorAction SilentlyContinue
        $p = Start-Process $bench -ArgumentList 'orphan', '--session', $name, '--out', "`"$marker`"", '--seconds', '600' -PassThru -WindowStyle Hidden
        for ($k = 0; $k -lt 60 -and -not (Test-Path $marker); $k++) { Start-Sleep -Milliseconds 500 }
        Stop-Process -Id $p.Id -Force
        Start-Sleep 2
        Add-Content $log "v9b_session_after_kill=$((logman query -ets | Out-String) -match [regex]::Escape($name))"
        logman stop $name -ets | Out-Null
        Start-Sleep 1
        Add-Content $log "v9b_session_after_logman_stop=$((logman query -ets | Out-String) -match [regex]::Escape($name))"
    }

    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',')"
    Add-Content $log "leftover_dirs=$(@(Get-ChildItem $env:TEMP -Directory -Filter 'DiskThinner-2a-*' -ErrorAction SilentlyContinue).Count)"
}

# 提权测试：DT_ETW_TESTS=1 时 EtwSessionTests 才运行（accept 的一步；-Part etw 只跑这一步）
$testProject = Join-Path $root 'tests\DiskThinner.Monitor.Tests\DiskThinner.Monitor.Tests.csproj'
function RunEtwTests {
    $env:DT_ETW_TESTS = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $testLog = Join-Path $OutDir 'etw-tests.log'
    $p = Start-Process $dotnet -ArgumentList 'test', "`"$testProject`"", '-c', 'Release', '--no-build', '-nologo', '--filter', 'FullyQualifiedName~EtwSessionTests',
        '--results-directory', "`"$(Join-Path $OutDir 'test-results')`"" -NoNewWindow -PassThru -RedirectStandardOutput $testLog -RedirectStandardError "$testLog.err"
    if (-not $p.WaitForExit(15 * 60 * 1000)) { $p.Kill(); throw '提权测试超时' }
    $summary = (Select-String -Path $testLog -Pattern 'Passed!|Failed!' | ForEach-Object { $_.Line.Trim() }) -join ' '
    Add-Content $log "etw-tests exit=$($p.ExitCode) $summary"
    if ($p.ExitCode -ne 0) { throw "dotnet test 退出码 $($p.ExitCode)" }
    if ($summary -notmatch 'Skipped:\s+0,') { throw "有测试被跳过：$summary" }
}

if ($parts -contains 'etw') {
    Step 'etw-tests' { RunEtwTests }
    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',')"
}

# 1.3 节第 20 条：冷启动时间、发布包大小、"监控中（精确）+ MFT 扫描"的峰值内存（tools/verify/phase2a-perf.ps1，需要 -Before / -After 两个发布目录）
if ($parts -contains 'perf') {
    Step 'perf' {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'phase2a-perf.ps1') -Before $Before -After $After -OutDir (Join-Path $OutDir 'perf')
        Add-Content $log "perf exit=$LASTEXITCODE"
    }
}

# 第 6 步（2a.5）：有监控数据时的界面截图、--monitor-seconds 到时停止、关窗口时停止（tools/verify/phase2a-ui.ps1）
if ($parts -contains 'ui') {
    Step 'ui' {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'phase2a-ui.ps1') -OutDir (Join-Path $OutDir 'ui-admin')
        Add-Content $log "ui exit=$LASTEXITCODE"
        if ($LASTEXITCODE -ne 0) { throw "phase2a-ui.ps1 退出码 $LASTEXITCODE" }
    }
    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',')"
}

if ($parts -contains 'accept') {
    $exe = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
    $loadRelease = Join-Path $root 'tests\DiskThinner.WriteLoad\bin\Release\net10.0-windows\DiskThinner.WriteLoad.exe'
    $schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
    $taskName = '\DiskThinner\StopDiagnostic'
    $defaultDb = Join-Path $env:LOCALAPPDATA 'DiskThinner\monitor.db'
    $stamp = { if (Test-Path $defaultDb) { (Get-Item $defaultDb).LastWriteTimeUtc.Ticks } else { 'missing' } }
    $dbBefore = & $stamp
    $script:failures = 0
    $made = [System.Collections.Generic.List[string]]::new()

    function ReadReport([string] $path) {
        $h = @{}
        if (Test-Path $path) { Get-Content $path | ForEach-Object { $k, $v = $_ -split '=', 2; $h[$k] = $v } }
        $h
    }

    function Expect([hashtable] $r, [string] $key, [string] $value) {
        if ($r[$key] -ne $value) { throw "$key=$($r[$key])，期望 $value" }
    }

    function DiagSessions { @(DiskThinnerSessions | Where-Object { $_ -like 'DiskThinner-Diag-*' }) }

    function TaskExists { & $schtasks /Query /TN $taskName 2>$null | Out-Null; $LASTEXITCODE -eq 0 }

    # 运行 monitor 子命令并等它结束（-Wait 的超时用 WaitForExit 实现，超时就强制结束并报错）
    function RunMonitor([string[]] $arguments, [int] $timeoutSeconds) {
        $p = Start-Process $exe -ArgumentList (@('monitor') + $arguments) -PassThru
        if (-not $p.WaitForExit($timeoutSeconds * 1000)) { $p.Kill(); throw "monitor $($arguments -join ' ') 超过 $timeoutSeconds 秒没有结束" }
        $p.ExitCode
    }

    function WaitStarted([string] $report, [int] $seconds = 30) {
        for ($i = 0; $i -lt $seconds * 2; $i++) {
            $r = ReadReport $report
            if ($r['started'] -eq '1') { return $r }
            if ($r['state'] -eq 'stopped') { throw "监控没有开始：$($r['error'])" }
            Start-Sleep -Milliseconds 500
        }
        throw "$seconds 秒内没有开始"
    }

    function AcceptStep($name, [scriptblock] $body) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            & $body
            Add-Content $log "$name ok $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
        } catch {
            $script:failures++
            Add-Content $log "$name FAILED: $_"
        }
        # 第 7 步：每一步之后都不能留 DiskThinner-Diag- 会话
        $left = DiagSessions
        if ($left.Count -gt 0) {
            $script:failures++
            Add-Content $log "$name FAILED: 留下了会话 $($left -join ',')"
        }
    }

    function NewLoadDir {
        $d = Join-Path ([IO.Path]::GetTempPath()) ('DiskThinner-2a-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $d | Out-Null
        $made.Add($d)
        $d
    }

    # 第 1 步
    Add-Content $log "sessions_before=$((DiskThinnerSessions) -join ',')"
    Add-Content $log "default_db_before=$dbBefore"
    Add-Content $log "task_before=$(TaskExists)"

    # 第 2 步：到时停止
    AcceptStep 'a2-timeout' {
        $r2 = Join-Path $OutDir 'r-2.txt'
        $code = RunMonitor @('--seconds', '30', '--db', (Join-Path $OutDir 'mon-2.db'), '--report', $r2) 90
        $r = ReadReport $r2
        Add-Content $log "a2 exit=$code $((Get-Content $r2) -join ' ')"
        if ($code -ne 0) { throw "退出码 $code" }
        Expect $r 'end_reason' 'timeout'
        Expect $r 'session_stopped' 'true'
        if ([long]$r['received'] -le 0) { throw '没有收到事件' }
        if (TaskExists) { throw '正常结束后兜底任务还在' }
    }

    # 第 3 步：强制结束后残留，monitor --cleanup 停掉会话、标"程序意外退出"、删任务
    AcceptStep 'a3-kill-cleanup' {
        $r3 = Join-Path $OutDir 'r-3.txt'
        $db3 = Join-Path $OutDir 'mon-3.db'
        $p = Start-Process $exe -ArgumentList 'monitor', '--seconds', '600', '--db', $db3, '--report', $r3 -PassThru
        $r = WaitStarted $r3
        $name = $r['session_name']
        Add-Content $log "a3 session=$name task_registered=$(TaskExists)"
        Stop-Process -Id $p.Id -Force
        Start-Sleep 2
        $alive = (DiagSessions) -contains $name
        Add-Content $log "a3 session_after_kill=$alive"
        if (-not $alive) { throw '强制结束后会话没有残留（预期残留）' }
        $r3c = Join-Path $OutDir 'r-3c.txt'
        $code = RunMonitor @('--cleanup', '--db', $db3, '--report', $r3c) 60
        $c = ReadReport $r3c
        Add-Content $log "a3c exit=$code $((Get-Content $r3c) -join ' ')"
        if ($code -ne 0) { throw "cleanup 退出码 $code" }
        Expect $c 'residual_stopped' '1'
        Expect $c 'orphans_marked' '1'
        Expect $c 'task_deleted' '1'
        if ((DiagSessions) -contains $name) { throw '清理后会话还在' }
        if (TaskExists) { throw '清理后兜底任务还在' }
    }

    # 第 4 步：兜底任务（D7，V20）。等计划结束 + 1 分钟后任务以 SYSTEM 运行 logman 停掉会话；同时单独验证"过期后自动删除"
    if (-not $SkipWatchdog) {
        AcceptStep 'a4-watchdog' {
            # V20 过期删除：一个测试自己的任务，20 秒后触发、1 分钟后过期，DeleteExpiredTaskAfter=PT0S
            $expireName = '\DiskThinner\AcceptExpire-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
            $now = [DateTimeOffset]::Now
            $fmt = 'yyyy-MM-ddTHH:mm:sszzz'
            $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><TimeTrigger><StartBoundary>$($now.AddSeconds(20).ToString($fmt))</StartBoundary><EndBoundary>$($now.AddSeconds(60).ToString($fmt))</EndBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><StartWhenAvailable>true</StartWhenAvailable><DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter><ExecutionTimeLimit>PT1M</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>$env:SystemRoot\System32\cmd.exe</Command><Arguments>/c exit 0</Arguments></Exec></Actions>
</Task>
"@
            $xmlPath = Join-Path $OutDir 'expire-task.xml'
            [IO.File]::WriteAllText($xmlPath, $xml, [Text.Encoding]::Unicode)
            & $schtasks /Create /TN $expireName /XML $xmlPath /F | Out-Null
            $expireRegistered = $LASTEXITCODE -eq 0
            Add-Content $log "a4 v20_expire_task_registered=$expireRegistered"

            $r4 = Join-Path $OutDir 'r-4.txt'
            $db4 = Join-Path $OutDir 'mon-4.db'
            $p = Start-Process $exe -ArgumentList 'monitor', '--seconds', '60', '--db', $db4, '--report', $r4 -PassThru
            $r = WaitStarted $r4
            $name = $r['session_name']
            $registered = TaskExists
            Add-Content $log "a4 session=$name task_registered=$registered"
            if (-not $registered) { throw '兜底任务没有注册' }
            & $schtasks /Query /TN $taskName /XML > (Join-Path $OutDir 'watchdog-task.xml')
            Start-Sleep 10
            Stop-Process -Id $p.Id -Force
            $killedAt = Get-Date
            $deadline = $killedAt.AddSeconds(50 + 7 * 60)
            $stoppedAfter = $null
            $expireGoneAfter = $null
            while ((Get-Date) -lt $deadline -and ($null -eq $stoppedAfter -or ($expireRegistered -and $null -eq $expireGoneAfter))) {
                Start-Sleep 10
                $elapsed = [int]((Get-Date) - $killedAt).TotalSeconds
                if ($null -eq $stoppedAfter -and -not ((DiagSessions) -contains $name)) { $stoppedAfter = $elapsed }
                if ($expireRegistered -and $null -eq $expireGoneAfter) {
                    & $schtasks /Query /TN $expireName 2>$null | Out-Null
                    if ($LASTEXITCODE -ne 0) { $expireGoneAfter = $elapsed }
                }
            }
            Add-Content $log "a4 watchdog_stopped_after_kill_s=$stoppedAfter v20_expired_deleted_after_s=$expireGoneAfter"
            if ($expireRegistered -and $null -eq $expireGoneAfter) {
                & $schtasks /Delete /TN $expireName /F | Out-Null # 测试自己建的任务，没有自动删掉就自己删
            }
            if ($null -eq $stoppedAfter) {
                logman stop $name -ets | Out-Null
                throw '兜底任务没有在计划结束 + 6 分钟内停掉会话'
            }
            $r4c = Join-Path $OutDir 'r-4c.txt'
            $code = RunMonitor @('--cleanup', '--db', $db4, '--report', $r4c) 60
            $c = ReadReport $r4c
            Add-Content $log "a4c exit=$code $((Get-Content $r4c) -join ' ')"
            Expect $c 'residual_found' '0'
            Expect $c 'orphans_marked' '1'
            Expect $c 'task_deleted' '1'
            if (TaskExists) { throw '清理后兜底任务还在' }
        }
    }

    # 第 5 步：负载下的监控（精确模式核对解析器，轻量模式只看计数）。逐项比对由下面的 EtwSessionTests 做
    foreach ($mode in 'precise', 'light') {
        AcceptStep "a5-load-$mode" {
            $dir = NewLoadDir
            $tag = $mode.Substring(0, 1)
            $snap = Join-Path $OutDir "snap-5$tag.db"
            $p = Start-Process $exe -ArgumentList 'snapshot', '--drive', $dir, '--db', $snap -PassThru
            if (-not $p.WaitForExit(60000)) { $p.Kill(); throw '快照超时' }
            $r5 = Join-Path $OutDir "r-5$tag.txt"
            $m = Start-Process $exe -ArgumentList 'monitor', '--seconds', '60', '--mode', $mode, '--verify-parser', '--db', (Join-Path $OutDir "mon-5$tag.db"),
                '--snapshot-db', $snap, '--report', $r5 -PassThru
            WaitStarted $r5 | Out-Null
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $loadReport = Join-Path $OutDir "load-5$tag.json"
            $l = Start-Process (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList '/c', "`"`"$loadRelease`" --dir `"$dir`" --report `"$loadReport`"`"" -PassThru -WindowStyle Hidden
            if (-not $l.WaitForExit(180000)) { $l.Kill(); throw 'WriteLoad 超时' }
            Add-Content $log "a5-$mode load_exit=$($l.ExitCode) load_ms=$($sw.ElapsedMilliseconds)"
            if (-not $m.WaitForExit(120000)) { $m.Kill(); throw 'monitor 没有按时结束' }
            $r = ReadReport $r5
            Add-Content $log "a5-$mode exit=$($m.ExitCode) $((Get-Content $r5) -join ' ')"
            if ($m.ExitCode -ne 0) { throw "退出码 $($m.ExitCode)" }
            Expect $r 'end_reason' 'timeout'
            Expect $r 'session_stopped' 'true'
            Expect $r 'parse_mismatch' '0'
            if ([long]$r['received'] -le 0) { throw '没有收到事件' }
            if ($r['lost'] -ne '0') { Add-Content $log "a5-$mode warning: lost=$($r['lost'])" }
        }
    }

    # 第 5 步（续）：提权测试，含默认负载下精确模式的逐项比对（第 1.3 节第 7 条）
    AcceptStep 'a5-etw-tests' { RunEtwTests }

    # 第 7 步：只删这次建的负载目录（在 %TEMP% 下、名字是这次生成的 DiskThinner-2a-<guid>），默认 monitor.db 没被碰过
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    foreach ($d in $made) {
        $full = [IO.Path]::GetFullPath($d)
        if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $full -Leaf) -like 'DiskThinner-2a-*' -and (Test-Path $full)) {
            Remove-Item $full -Recurse -Force
        }
    }
    $dbAfter = & $stamp
    Add-Content $log "default_db_after=$dbAfter"
    if ("$dbAfter" -ne "$dbBefore") { $script:failures++; Add-Content $log 'FAILED: 默认 monitor.db 被改动了' }
    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',') task_after=$(TaskExists)"
    Add-Content $log "accept_failures=$script:failures"
}

Add-Content $log "done $(Get-Date -Format s)"
