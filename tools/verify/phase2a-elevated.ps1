# 阶段 2a 需要管理员的实测和验收（docs/phase2a-design.md 5.2、8.2 节；以管理员身份运行，会先弹出 UAC）。
#   -Part bench：ETW 开销实测和 V 清单（2a.1）。三种配置（不开会话 / 轻量 / 精确）× 三种负载
#     W1 空闲、W2 测试写入负载（DiskThinner.WriteLoad）、W3 编译本仓库的主程序；另外测 V6（制造丢失）、V7（不带原生 DLL）、
#     V9（进程被强制结束后会话残留，logman 能否清掉）、V21（普通权限列会话，由普通权限的一侧配合）。
# 负载约束（8.4 节）：本脚本设为低于正常优先级，子进程继承；同一时间只有一个 EtwBench 和一个负载。
# 只删除测试自己在 %TEMP%\DiskThinner-2a-* 下建的目录（EtwBench 自己删）。运行前先用普通权限构建：
#   dotnet build spikes/EtwBench -c Release; dotnet build tests/DiskThinner.WriteLoad -c Release; dotnet restore src/DiskThinner.App
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File','tools\verify\phase2a-elevated.ps1','-OutDir','<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateSet('bench', 'bench2')] [string] $Part = 'bench',
    [int] $IdleSeconds = 180
)

$ErrorActionPreference = 'Stop'
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

if ($Part -eq 'bench') {
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

if ($Part -eq 'bench2') {
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

Add-Content $log "done $(Get-Date -Format s)"
