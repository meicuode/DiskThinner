# M1 验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
#   1. WizTree 导出 C 盘目录占用
#   2. ScanBench 用 MFT 扫描并导出目录占用、记录扫描器峰值内存
#   3. DiskThinner 主程序用 MFT 扫描、截图、记录耗时和内存
#   4. 对比 1 和 2，生成 wiztree-compare.md
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m1-elevated.ps1', '-OutDir', '<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $WizTree = 'D:\Program Files\WizTree\WizTree64.exe',
    [string] $Drive = 'C:'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scanBench = Join-Path $root 'spikes\ScanBench\bin\Release\net10.0-windows\ScanBench.exe'
$app = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'elevated.log'

function Step($name, [scriptblock] $body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        Add-Content $log "$name ok $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
    } catch {
        Add-Content $log "$name failed: $_"
    }
}

Set-Content $log "start $(Get-Date -Format s)"

Step 'wiztree' {
    if (Test-Path $WizTree) {
        $csv = Join-Path $OutDir 'wiztree.csv'
        Start-Process -FilePath $WizTree -ArgumentList @($Drive, "/export=`"$csv`"", '/admin=1', '/exportfiles=0', '/exportmaxdepth=4') -Wait
    } else {
        Add-Content $log "wiztree not found: $WizTree"
    }
}

Step 'scanbench-mft' { & $scanBench mft $Drive --out $OutDir | Out-Null }

Step 'app-mft' {
    $p = Start-Process -FilePath $app -PassThru -ArgumentList @(
        '--scan', $Drive, '--select', "$Drive\Users",
        '--screenshot', "`"$(Join-Path $OutDir 'app-mft.png')`"",
        '--report', "`"$(Join-Path $OutDir 'app-mft.txt')`"",
        '--exit-after-scan')
    if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
}

Step 'compare' {
    if (Test-Path (Join-Path $OutDir 'wiztree.csv')) {
        & $scanBench wiztree (Join-Path $OutDir 'wiztree.csv') --out $OutDir | Out-Null
    }
}

Add-Content $log "done $(Get-Date -Format s)"
