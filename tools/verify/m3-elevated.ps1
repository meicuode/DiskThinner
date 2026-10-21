# M3 验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
#   1. snapshot 子命令用 MFT 扫描整个盘、保存两次快照：单次快照的大小、耗时
#   2. 主程序用同一个快照库：概览、增长分析的截图和内存
#   3. 以管理员身份注册每日快照任务（最高权限），查询后立即移除
# 快照写到 -OutDir 下的独立快照库，不影响 %LOCALAPPDATA%\DiskThinner\snapshots.db。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m3-elevated.ps1', '-OutDir', '<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Drive = 'C:'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$app = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$db = Join-Path $OutDir 'snapshots.db'
Remove-Item $db -ErrorAction SilentlyContinue
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

foreach ($i in 1, 2) {
    Step "snapshot-$i" {
        $p = Start-Process -FilePath $app -PassThru -Wait -ArgumentList @('snapshot', '--drive', $Drive, '--db', "`"$db`"")
        Add-Content $log "snapshot-$i exit=$($p.ExitCode) db_bytes=$((Get-Item $db).Length)"
    }
}

function RunApp($run, [string[]] $extra) {
    Step "app-$run" {
        $args = @('--scan', $Drive, '--snapshot-db', "`"$db`"",
            '--screenshot', "`"$(Join-Path $OutDir "app-$run.png")`"",
            '--report', "`"$(Join-Path $OutDir "app-$run.txt")`"",
            '--exit-after-scan') + $extra
        $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
    }
}

RunApp 'mem-100' @('--secondary-screen', '--page', 'explorer')
RunApp 'overview' @('--primary-screen', '--page', 'overview')
RunApp 'growth' @('--primary-screen', '--page', 'growth')

Step 'schedule' {
    $p = Start-Process -FilePath $app -PassThru -Wait -ArgumentList @('snapshot', '--register', '--time', '03:17')
    $xml = & schtasks.exe /Query /TN '\DiskThinner\DailySnapshot' /XML | Out-String
    Set-Content (Join-Path $OutDir 'task.xml') $xml
    $q = Start-Process -FilePath $app -PassThru -Wait -ArgumentList @('snapshot', '--unregister')
    Add-Content $log "register exit=$($p.ExitCode) highest=$($xml.Contains('<RunLevel>HighestAvailable</RunLevel>')) unregister exit=$($q.ExitCode)"
}

Add-Content $log "done $(Get-Date -Format s)"
