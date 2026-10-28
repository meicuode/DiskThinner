# 阶段 2a.5 界面截图（docs/phase2a-design.md 8.2 节 accept 第 6 步的普通权限部分，以及管理员下的数据准备）。
# 在 %TEMP%\DiskThinner-2a-<guid>\ 下造一个测试目录：存一个快照，跑写入负载（管理员下同时开精确模式的写入监控），再存一个快照，
# 然后用同一套库截增长分析、概览、设置、空间浏览详情和"开启写入监控"对话框。所有库和设置都写在 -OutDir 下，不碰默认位置的数据。
# 结束后只删除这次建的 DiskThinner-2a-<guid> 目录。
#   普通权限：pwsh -File tools\verify\phase2a-ui.ps1 -OutDir <目录>            （没有监控数据，截"未监控"和普通权限的对话框）
#   管理员：  由 phase2a-elevated.ps1 -Part ui 调用（有监控数据，另外验证 --monitor-seconds 到时停止和关窗口停止）
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exe = Join-Path $root "src\DiskThinner.App\bin\$Configuration\net10.0-windows\DiskThinner.exe"
$load = Join-Path $root "tests\DiskThinner.WriteLoad\bin\$Configuration\net10.0-windows\DiskThinner.WriteLoad.exe"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'ui.log'
Set-Content $log "start $(Get-Date -Format s)"
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Content $log "elevated=$elevated"
$failures = 0

function Run([string[]] $arguments, [int] $timeoutSeconds = 180) {
    $p = Start-Process $exe -ArgumentList $arguments -PassThru
    if (-not $p.WaitForExit($timeoutSeconds * 1000)) { $p.Kill(); throw "DiskThinner $($arguments -join ' ') 超过 $timeoutSeconds 秒没有结束" }
    return $p.ExitCode
}

function ReadReport([string] $path) {
    $h = @{}
    if (Test-Path $path) { Get-Content $path -Encoding utf8 | ForEach-Object { $k, $v = $_ -split '=', 2; $h[$k] = $v } }
    $h
}

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$dir = Join-Path $temp ('DiskThinner-2a-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$snap = Join-Path $OutDir 'snap.db'
$mon = Join-Path $OutDir 'mon.db'
$settings = Join-Path $OutDir 'settings.json'
$user = Join-Path $OutDir 'user.db'
Remove-Item $snap, $mon, $settings -ErrorAction SilentlyContinue
try {
    # ---- 数据：第一个快照 → （管理员下开监控）写入负载 → 第二个快照 ----
    # 快照只单独记录 50 MB 以上的目录，热点阈值 100 MB：先放一个 160 MB 的 big\，负载再写 128 MB
    New-Item -ItemType Directory (Join-Path $dir 'big') | Out-Null
    $f = [IO.File]::Create((Join-Path $dir 'seed.bin')); $f.SetLength(60MB); $f.Close()
    if ((Run @('snapshot', '--drive', $dir, '--db', $snap)) -ne 0) { throw '第一个快照失败' }
    Start-Sleep 2
    $monitor = $null
    if ($elevated) {
        $r = Join-Path $OutDir 'r-ui-monitor.txt'
        $monitor = Start-Process $exe -ArgumentList 'monitor', '--seconds', '60', '--mode', 'precise', '--db', $mon, '--snapshot-db', $snap, '--report', $r -PassThru
        for ($i = 0; $i -lt 60 -and (ReadReport $r)['started'] -ne '1'; $i++) { Start-Sleep -Milliseconds 500 }
        Add-Content $log "monitor started=$((ReadReport $r)['started'])"
    }

    $l = Start-Process $load -ArgumentList '--dir', $dir, '--report', (Join-Path $OutDir 'load.json'), '--small', '200', '--delete', '20', '--rename', '20' -PassThru -WindowStyle Hidden
    if (-not $l.WaitForExit(180000)) { $l.Kill(); throw 'WriteLoad 超时' }
    Add-Content $log "load exit=$($l.ExitCode)"
    if ($monitor) {
        if (-not $monitor.WaitForExit(120000)) { $monitor.Kill(); throw 'monitor 没有按时结束' }
        Add-Content $log "monitor exit=$($monitor.ExitCode) $((Get-Content (Join-Path $OutDir 'r-ui-monitor.txt')) -join ' ')"
    }

    # 第二个快照不存：界面里的"当前扫描"就是终点

    # ---- 截图 ----
    $common = @('--scan', $dir, '--snapshot-db', $snap, '--monitor-db', $mon, '--settings', $settings, '--user-db', $user, '--no-auto-snapshot', '--primary-screen', '--exit-after-scan')
    $tag = if ($elevated) { 'admin' } else { 'user' }
    foreach ($shot in @(
            @{ Name = 'growth'; Args = @('--page', 'growth') },
            @{ Name = 'overview'; Args = @('--page', 'overview') },
            @{ Name = 'settings'; Args = @('--page', 'settings') },
            @{ Name = 'explorer'; Args = @('--page', 'explorer', '--select', (Join-Path $dir 'big')) },
            @{ Name = 'dialog'; Args = @('--page', 'overview', '--show-monitor-dialog') })) {
        $png = Join-Path $OutDir "$tag-$($shot.Name).png"
        $report = Join-Path $OutDir "$tag-$($shot.Name).txt"
        $code = Run ($common + $shot.Args + @('--screenshot', $png, '--report', $report))
        $rep = ReadReport $report
        Add-Content $log "$($shot.Name) exit=$code monitor_state=$($rep['monitor_state']) sessions_30d=$($rep['monitor_sessions_30d']) growth_writer_cells=$($rep['growth_writer_cells']) overview_writer_cells=$($rep['overview_writer_cells']) automation_error=$($rep['automation_error'])"
        if ($code -ne 0 -or -not (Test-Path $png)) { $failures++; Add-Content $log "$($shot.Name) FAILED" }
    }

    # ---- 管理员：--monitor-seconds 到时停止（窗口开着），关窗口时停止 ----
    if ($elevated) {
        $monLog = Join-Path $env:LOCALAPPDATA 'DiskThinner\logs\monitor.log'
        $crashDir = Join-Path $env:LOCALAPPDATA 'DiskThinner\logs'
        $before = Get-Date
        $mon6 = Join-Path $OutDir 'mon-6.db'
        $p = Start-Process $exe -ArgumentList ($common[0..($common.Count - 2)] + @('--monitor-db', $mon6, '--monitor-seconds', '20', '--page', 'growth')) -PassThru
        Start-Sleep 40
        $alive = -not $p.HasExited
        $left = @(logman query -ets | Select-String -Pattern 'DiskThinner-Diag-')
        $ended = (Get-Content $monLog -Tail 30 -ErrorAction SilentlyContinue | Select-String -Pattern 'monitor stopped .* reason=timeout').Count
        Add-Content $log "a6-timeout window_alive=$alive sessions_left=$($left.Count) timeout_logged=$ended"
        if (-not $alive -or $left.Count -gt 0 -or $ended -eq 0) { $failures++; Add-Content $log 'a6-timeout FAILED' }
        if ($alive) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(30000) }

        $mon7 = Join-Path $OutDir 'mon-7.db'
        # 截图在监控进行中（自动化先等写入监控开始再截图）：工具栏和状态栏是"诊断中"，覆盖时段里有进行中的一行
        $code = Run ($common + @('--monitor-db', $mon7, '--monitor-seconds', '600', '--page', 'growth', '--report', (Join-Path $OutDir 'a6-exit.txt'),
                '--screenshot', (Join-Path $OutDir 'admin-running.png')))
        Start-Sleep 2
        $left = @(logman query -ets | Select-String -Pattern 'DiskThinner-Diag-')
        $exitLogged = (Get-Content $monLog -Tail 30 -ErrorAction SilentlyContinue | Select-String -Pattern 'monitor stopped .* reason=app-exit').Count
        Add-Content $log "a6-exit exit=$code sessions_left=$($left.Count) app_exit_logged=$exitLogged report_state=$((ReadReport (Join-Path $OutDir 'a6-exit.txt'))['monitor_state'])"
        if ($code -ne 0 -or $left.Count -gt 0 -or $exitLogged -eq 0) { $failures++; Add-Content $log 'a6-exit FAILED' }
        $crashes = @(Get-ChildItem $crashDir -Filter 'crash-*.txt' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $before })
        Add-Content $log "a6 crashes=$($crashes.Count)"
        if ($crashes.Count -gt 0) { $failures++ }
    }
}
catch {
    $failures++
    Add-Content $log "FAILED: $_"
}
finally {
    # 只删这次建的目录（在 %TEMP% 下、名字是这次生成的 DiskThinner-2a-<guid>）
    $full = [IO.Path]::GetFullPath($dir)
    if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $full -Leaf) -like 'DiskThinner-2a-*' -and (Test-Path $full)) {
        Remove-Item $full -Recurse -Force
    }
}

Add-Content $log "ui_failures=$failures"
Add-Content $log "done $(Get-Date -Format s)"
exit ([int]($failures -gt 0))
