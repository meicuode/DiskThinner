# M4 验收中需要管理员权限的部分（以管理员身份运行，会先弹出 UAC）：
#   1. 建一个 1 GB 的 VHDX 测试盘（NTFS，卷标 DTTEST），挂上
#   2. 在测试盘上跑删除类测试（DT_CLEAN_TEST_ROOT 指向测试盘；测试会核对卷标，不是 DTTEST 就直接报错），
#      核对可用空间的实际变化；MFT 扫描测试盘，确认硬链接不计入预计释放
#   3. 主程序：MFT 扫描 C 盘生成清理计划，截清理计划页和确认框（只显示，不执行）；用测试产生的操作记录截操作记录页
#   4. 卸下测试盘。VHDX 文件留在 -OutDir 里，不需要时可以自己删掉
# 除了测试盘上测试自己建的文件，不删除任何东西。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\m4-elevated.ps1', '-OutDir', '<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$app = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'elevated.log'
Set-Content $log "start $(Get-Date -Format s)"
$vhd = Join-Path $OutDir ("dttest-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.vhdx')
$audit = Join-Path $OutDir 'audit.db'
Remove-Item $audit -ErrorAction SilentlyContinue

function Step($name, [scriptblock] $body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        Add-Content $log "$name ok $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
    } catch {
        Add-Content $log "$name failed: $_"
    }
}

function DiskPart([string[]] $lines) {
    $script = Join-Path $OutDir 'diskpart.txt'
    Set-Content $script $lines -Encoding ascii
    $out = & diskpart.exe /s $script 2>&1 | Out-String
    Add-Content (Join-Path $OutDir 'diskpart.log') $out
    if ($LASTEXITCODE -ne 0) { throw "diskpart exit=$LASTEXITCODE" }
}

$volume = $null
Step 'vhd-create' {
    DiskPart @(
        "create vdisk file=`"$vhd`" maximum=1024 type=expandable",
        "select vdisk file=`"$vhd`"",
        'attach vdisk',
        'create partition primary',
        'format fs=ntfs label=DTTEST quick',
        'assign'
    )
    Start-Sleep -Seconds 2
    $v = Get-DiskImage -ImagePath $vhd | Get-Disk | Get-Partition | Get-Volume | Where-Object FileSystemLabel -eq 'DTTEST'
    if (-not $v -or -not $v.DriveLetter) { throw '找不到测试盘的盘符' }
    $script:volume = "$($v.DriveLetter):\"
    Add-Content $log "volume=$($script:volume) label=$($v.FileSystemLabel) fs=$($v.FileSystem)"
}

if ($volume) {
    Step 'tests-on-vhd' {
        $env:DT_CLEAN_TEST_ROOT = $volume
        $env:DT_CLEAN_TEST_AUDIT = $audit
        $env:DOTNET_CLI_UI_LANGUAGE = 'en'
        $testLog = Join-Path $OutDir 'vhd-tests.log'
        & $dotnet test (Join-Path $root 'tests\DiskThinner.Cleanup.Tests\DiskThinner.Cleanup.Tests.csproj') --no-build -nologo `
            --logger 'console;verbosity=normal' *> $testLog
        $code = $LASTEXITCODE
        Remove-Item Env:DT_CLEAN_TEST_ROOT, Env:DT_CLEAN_TEST_AUDIT
        $summary = Select-String -Path $testLog -Pattern 'Passed!|Failed!|Total tests|Failed ' | ForEach-Object { $_.Line.Trim() }
        Add-Content $log "tests exit=$code"
        Add-Content $log $summary
        $left = @(Get-ChildItem $volume -Directory -Filter 'dt-*' -ErrorAction SilentlyContinue).Count
        Add-Content $log "leftover_fixtures=$left"
    }

    Step 'app-log' {
        # 操作记录页：用测试在测试盘上产生的记录；扫描测试盘（很快），不执行任何清理
        $args = @('--scan', $volume, '--user-db', "`"$audit`"", '--snapshot-db', "`"$(Join-Path $OutDir 'snap-vhd.db')`"", '--no-auto-snapshot',
            '--primary-screen', '--page', 'log', '--screenshot', "`"$(Join-Path $OutDir 'app-log.png')`"",
            '--report', "`"$(Join-Path $OutDir 'app-log.txt')`"", '--exit-after-scan')
        $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
        if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
    }

    Step 'vhd-detach' {
        DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk')
    }
}

Step 'app-cleanup' {
    # 清理计划页和确认框：MFT 扫描 C 盘（只读），确认框截图后直接关掉，不执行
    $args = @('--scan', 'C:', '--user-db', "`"$(Join-Path $OutDir 'user-c.db')`"", '--snapshot-db', "`"$(Join-Path $OutDir 'snap-c.db')`"", '--no-auto-snapshot',
        '--primary-screen', '--page', 'cleanup', '--show-confirm', '--screenshot', "`"$(Join-Path $OutDir 'app-cleanup.png')`"",
        '--report', "`"$(Join-Path $OutDir 'app-cleanup.txt')`"", '--exit-after-scan')
    $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
}

Step 'app-mem-100' {
    $args = @('--scan', 'C:', '--user-db', "`"$(Join-Path $OutDir 'user-c.db')`"", '--snapshot-db', "`"$(Join-Path $OutDir 'snap-c.db')`"", '--no-auto-snapshot',
        '--secondary-screen', '--page', 'explorer', '--screenshot', "`"$(Join-Path $OutDir 'app-mem-100.png')`"",
        '--report', "`"$(Join-Path $OutDir 'app-mem-100.txt')`"", '--exit-after-scan')
    $p = Start-Process -FilePath $app -PassThru -ArgumentList $args
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'timeout' }
}

Add-Content $log "done $(Get-Date -Format s)"
