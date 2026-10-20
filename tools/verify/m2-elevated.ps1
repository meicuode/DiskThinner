# M2 验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
#   1. dtrule probe：MFT 扫描整个盘，统计规则覆盖率
#   2. 主程序 MFT 扫描，分别截取概览页和空间浏览页，记录规则匹配耗时
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m2-elevated.ps1', '-OutDir', '<目录>'
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

Step 'probe' {
    & dotnet $dtrule probe (Join-Path $root 'rules') --drive "$Drive\" --out (Join-Path $OutDir 'probe-mft.md') *> (Join-Path $OutDir 'probe-mft.log')
}

foreach ($page in 'overview', 'explorer') {
    Step "app-$page" {
        $args = @('--scan', $Drive, '--page', $page,
            '--screenshot', "`"$(Join-Path $OutDir "app-$page.png")`"",
            '--report', "`"$(Join-Path $OutDir "app-$page.txt")`"",
            '--exit-after-scan')
        if ($page -eq 'explorer') { $args += @('--select', "`"$Drive\Windows\WinSxS`"") }
        $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
    }
}

Add-Content $log "done $(Get-Date -Format s)"
