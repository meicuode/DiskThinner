# 阶段 2a 和研发方案 1.3 节性能目标的对照（docs/phase2a-design.md 1.3 节第 20 条）：
#   1. 冷启动到窗口显示的时间：两个发布目录（加 TraceEvent 之前的 0.1.0 和现在的）的依赖运行库版，交替各启动 -Runs 次，取中位数；
#   2. 管理员下：自包含版"监控中（精确）+ MFT 扫描整个系统盘"的峰值内存（--report 的 peak_working_set_bytes）。
# 只读：库、设置都写在 -OutDir 下；扫描的是一个空的测试目录（冷启动）和系统盘（只读，不清理）。
#   pwsh -File tools\verify\phase2a-perf.ps1 -Before artifacts\release\0.1.0 -After artifacts\release\0.2.0-dev.5 -OutDir <目录>
param(
    [Parameter(Mandatory)] [string] $Before,
    [Parameter(Mandatory)] [string] $After,
    [Parameter(Mandatory)] [string] $OutDir,
    [int] $Runs = 3
)

$ErrorActionPreference = 'Stop'
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'perf.log'
Set-Content $log "start $(Get-Date -Format s)"
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Content $log "elevated=$elevated"

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$dir = Join-Path $temp ('DiskThinner-2a-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
Set-Content (Join-Path $dir 'a.txt') 'x'

function Common([string] $tag) {
    @('--snapshot-db', (Join-Path $OutDir "$tag-snap.db"), '--monitor-db', (Join-Path $OutDir "$tag-mon.db"), '--settings', (Join-Path $OutDir "$tag-settings.json"),
        '--user-db', (Join-Path $OutDir "$tag-user.db"), '--no-auto-snapshot', '--primary-screen')
}

# 启动到主窗口句柄出现（窗口创建并显示）的毫秒数；然后正常关闭窗口
function ColdStart([string] $exe, [string] $tag) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $exe -ArgumentList (@('--scan', $dir, '--page', 'overview') + (Common $tag)) -PassThru
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 10
    }
    $ms = [int]$sw.Elapsed.TotalMilliseconds
    Start-Sleep 2
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(20000)) { $p.Kill() }
    Start-Sleep 2
    return $ms
}

try {
    $b = Join-Path (Resolve-Path $Before).Path 'DiskThinner\DiskThinner.exe'
    $a = Join-Path (Resolve-Path $After).Path 'DiskThinner\DiskThinner.exe'
    [void](ColdStart $b 'warm-b'); [void](ColdStart $a 'warm-a') # 第一次启动有磁盘缓存的影响，不计
    $tb = @(); $ta = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        $tb += ColdStart $b 'b'
        $ta += ColdStart $a 'a'
    }
    $median = { param($xs) ($xs | Sort-Object)[[int][Math]::Floor($xs.Count / 2)] }
    Add-Content $log "cold_start_before_ms=$($tb -join ',') median=$(& $median $tb)"
    Add-Content $log "cold_start_after_ms=$($ta -join ',') median=$(& $median $ta)"
    foreach ($pkg in @(@{ Name = 'before'; Path = $Before }, @{ Name = 'after'; Path = $After })) {
        $fd = Join-Path (Resolve-Path $pkg.Path).Path 'DiskThinner'
        $files = @(Get-ChildItem $fd -File -Recurse)
        Add-Content $log ("package_{0}_framework_bytes={1} files={2}" -f $pkg.Name, ($files | Measure-Object Length -Sum).Sum, $files.Count)
    }

    if ($elevated) {
        $sc = Join-Path (Resolve-Path $After).Path 'DiskThinner-selfcontained\DiskThinner.exe'
        $report = Join-Path $OutDir 'mft-monitor.txt'
        $drive = $env:SystemDrive + '\'
        $p = Start-Process $sc -ArgumentList (@('--scan', $drive, '--page', 'overview', '--monitor-seconds', '120', '--monitor-mode', 'precise', '--exit-after-scan', '--report', $report) + (Common 'mft')) -PassThru
        if (-not $p.WaitForExit(300000)) { $p.Kill(); throw 'MFT 扫描超过 5 分钟没有结束' }
        $r = @{}
        Get-Content $report -Encoding utf8 | ForEach-Object { $k, $v = $_ -split '=', 2; $r[$k] = $v }
        Add-Content $log "mft_monitor exit=$($p.ExitCode) mode=$($r['mode']) nodes=$($r['nodes']) scan_ms=$($r['scan_ms']) peak_ws=$($r['peak_working_set_bytes']) monitor_state=$($r['monitor_state']) monitor_received=$($r['monitor_received']) device_dpi=$($r['device_dpi'])"
        $left = @(logman query -ets | Select-String -Pattern 'DiskThinner-Diag-')
        Add-Content $log "sessions_left=$($left.Count)"
    }
}
catch {
    Add-Content $log "FAILED: $_"
}
finally {
    $full = [IO.Path]::GetFullPath($dir)
    if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $full -Leaf) -like 'DiskThinner-2a-*' -and (Test-Path $full)) {
        Remove-Item $full -Recurse -Force
    }
}

Add-Content $log "done $(Get-Date -Format s)"
