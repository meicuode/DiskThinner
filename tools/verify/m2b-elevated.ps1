# M2 下半验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
#   1. dtrule probe：MFT 扫描整个盘，统计规则覆盖率和归属推断
#   2. 主程序峰值内存：和 M1 一样的条件（100% 缩放的显示器、不加载规则）对比加载规则和归属之后
#   3. 主程序截图：概览、按目录、按归属汇总、按类别汇总（150% 缩放的主显示器）
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m2b-elevated.ps1', '-OutDir', '<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Drive = 'C:'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dtrule = Join-Path $root 'tools\dtrule\bin\Release\net10.0-windows\dtrule.dll'
$app = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
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

function RunApp($run, [string[]] $extra) {
    # 参数不能叫 $name：脚本块在 Step 里执行，会取到 Step 自己的 $name（PowerShell 的动态作用域）
    Step "app-$run" {
        $args = @('--scan', $Drive,
            '--screenshot', "`"$(Join-Path $OutDir "app-$run.png")`"",
            '--report', "`"$(Join-Path $OutDir "app-$run.txt")`"",
            '--exit-after-scan') + $extra
        $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
    }
}

Step 'probe' {
    & dotnet $dtrule probe (Join-Path $root 'rules') --drive "$Drive\" --out (Join-Path $OutDir 'probe-mft.md') *> (Join-Path $OutDir 'probe-mft.log')
}

# 内存：每种条件跑两次，取较小的峰值（排除系统偶发的抖动）
foreach ($i in 1, 2) {
    RunApp "mem-norules-100-$i" @('--secondary-screen', '--page', 'explorer', '--no-rules')
    RunApp "mem-rules-100-$i" @('--secondary-screen', '--page', 'explorer')
    RunApp "mem-rules-150-$i" @('--primary-screen', '--page', 'explorer')
}

RunApp 'overview' @('--primary-screen', '--page', 'overview')
RunApp 'explorer-dir' @('--primary-screen', '--page', 'explorer', '--select', "`"$Drive\Program Files`"")
RunApp 'explorer-owner' @('--primary-screen', '--page', 'explorer', '--view', 'owner')
RunApp 'explorer-kind' @('--primary-screen', '--page', 'explorer', '--view', 'kind')

Add-Content $log "done $(Get-Date -Format s)"
