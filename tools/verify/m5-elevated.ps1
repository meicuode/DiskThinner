# M5 验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
# 两个发布包（依赖运行库的、单文件自包含的）各用 MFT 扫描一次 C 盘：能启动、规则能加载、峰值内存；
# 自包含版再跑一次 snapshot 子命令。只读扫描，快照、操作记录、设置都写到 -OutDir 下，不删除任何东西。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m5-elevated.ps1', '-OutDir', '<目录>', '-Release', '<发布目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $Release
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
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

foreach ($v in @(@{ Name = 'fd'; Exe = Join-Path $Release 'DiskThinner\DiskThinner.exe' }, @{ Name = 'sc'; Exe = Join-Path $Release 'DiskThinner-selfcontained\DiskThinner.exe' })) {
    Step "app-$($v.Name)" {
        $d = Join-Path $OutDir $v.Name
        New-Item -ItemType Directory -Force $d | Out-Null
        $args = @('--scan', 'C:', '--no-auto-snapshot', '--snapshot-db', "`"$d\snap.db`"", '--user-db', "`"$d\user.db`"", '--settings', "`"$d\settings.json`"",
            '--secondary-screen', '--page', 'overview', '--screenshot', "`"$d\overview.png`"", '--report', "`"$d\report.txt`"", '--exit-after-scan')
        $p = Start-Process -FilePath $v.Exe -PassThru -ArgumentList $args
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
        Add-Content $log "$($v.Name) exit=$($p.ExitCode)"
    }
}

Step 'snapshot-sc' {
    $db = Join-Path $OutDir 'sc\snap-cmd.db'
    $p = Start-Process -FilePath (Join-Path $Release 'DiskThinner-selfcontained\DiskThinner.exe') -PassThru -Wait -ArgumentList @('snapshot', '--drive', 'C:', '--db', "`"$db`"")
    Add-Content $log "snapshot exit=$($p.ExitCode) db_bytes=$((Get-Item $db).Length)"
}

Add-Content $log "done $(Get-Date -Format s)"
