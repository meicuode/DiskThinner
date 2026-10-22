# 测试盘上的扫描验收（研发方案第 6 节集成测试；以管理员身份运行，会先弹出 UAC）：
#   1. 建一个 1 GB 的 VHDX 测试盘（NTFS，卷标 DTTEST），挂上
#   2. DT_SCAN_TEST_VOLUME 指向测试盘，跑 Scanner.Tests 里的 TestVolumeTests：测试在盘上造出压缩、稀疏、备用数据流、
#      硬链接、junction、1000 层嵌套和 255 个字符的文件名，比对 MFT 扫描、遍历扫描和期望值（测试会核对卷标，不是 DTTEST 就报错）
#   3. 卸下测试盘。VHDX 文件留在 -OutDir 里，不需要时可以自己删掉
# 除了测试盘上测试自己建的目录，不删除任何东西。运行前先用普通权限构建（dotnet build），这里用 --no-build。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\verify\scan-elevated.ps1', '-OutDir', '<目录>'
param(
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'elevated.log'
Set-Content $log "start $(Get-Date -Format s)"
$vhd = Join-Path $OutDir ("dtscan-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.vhdx')

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
    Add-Content $log "volume=$($script:volume) label=$($v.FileSystemLabel) fs=$($v.FileSystem) cluster=$((Get-CimInstance Win32_Volume -Filter "DriveLetter='$($v.DriveLetter):'").BlockSize)"
}

if ($volume) {
    Step 'tests-on-vhd' {
        $env:DT_SCAN_TEST_VOLUME = $volume
        $env:DOTNET_CLI_UI_LANGUAGE = 'en'
        $testLog = Join-Path $OutDir 'vhd-tests.log'
        & $dotnet test (Join-Path $root 'tests\DiskThinner.Scanner.Tests\DiskThinner.Scanner.Tests.csproj') --no-build -nologo `
            --filter 'FullyQualifiedName~DiskThinner.Scanner.Tests.TestVolumeTests' `
            --logger 'console;verbosity=normal' --logger "trx;LogFileName=$(Join-Path $OutDir 'vhd-tests.trx')" *> $testLog
        $code = $LASTEXITCODE
        Remove-Item Env:DT_SCAN_TEST_VOLUME
        $summary = Select-String -Path $testLog -Pattern 'Passed!|Failed!|Total tests|^\s*(Passed|Failed|Skipped) ' | ForEach-Object { $_.Line.Trim() }
        Add-Content $log "tests exit=$code"
        Add-Content $log $summary
        $left = @(Get-ChildItem $volume -Directory -Filter 'dt-scan-*' -ErrorAction SilentlyContinue).Count
        Add-Content $log "leftover_dirs=$left"
    }

    Step 'vhd-detach' {
        DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk')
    }
}

Add-Content $log "done $(Get-Date -Format s)"
