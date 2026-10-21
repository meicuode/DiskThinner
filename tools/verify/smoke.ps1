# 发布包冒烟测试。GitHub Actions 在全新的 windows-2022 / windows-2025 上跑，也可以在本机或虚拟机上跑。
# 解压 zip，逐个启动：扫描一个测试文件夹；管理员下再用 MFT 扫描整个系统盘、生成清理计划、截确认框（只显示，不执行）；
# 跑 snapshot 子命令。只读：不执行任何清理；快照、操作记录、设置都写到 -OutDir 下。
# 结果写到 -OutDir\smoke.txt；在 GitHub Actions 里同时输出 ::notice / ::error 注解。有失败时退出码为 1。
# 用法：pwsh -File tools\verify\smoke.ps1 -PackageDir <放 zip 的目录> -OutDir <输出目录> [-Variant all|selfcontained|framework]
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateSet('all', 'selfcontained', 'framework')] [string] $Variant = 'all',
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$PackageDir = (Resolve-Path $PackageDir).Path
$summary = Join-Path $OutDir 'smoke.txt'
$ci = $env:GITHUB_ACTIONS -eq 'true'
$failures = [System.Collections.Generic.List[string]]::new()
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$os = Get-CimInstance Win32_OperatingSystem
$systemDrive = $env:SystemDrive
$crashDir = Join-Path $env:LOCALAPPDATA 'DiskThinner\logs'
$started = Get-Date

function Say([string] $text) {
    Add-Content $summary $text -Encoding utf8
    Write-Host $text
}

function Notice([string] $case, [string] $text) {
    Say "OK   $case：$text"
    if ($ci) { Write-Host "::notice title=冒烟测试 $case::$text" }
}

function Fail([string] $case, [string] $text) {
    $failures.Add("$case：$text")
    Say "FAIL $case：$text"
    if ($ci) { Write-Host "::error title=冒烟测试 $case::$text" }
}

function Q([string] $path) { '"' + $path + '"' }

function Mb($bytes) { '{0:N0} MB' -f ([double]$bytes / 1e6) }

Say "系统：$($os.Caption) $($os.Version)，管理员：$elevated，开始：$($started.ToString('s'))"

# ---- 解压 ----
$packages = @{}
foreach ($zip in Get-ChildItem $PackageDir -Filter 'DiskThinner-*.zip') {
    $kind = if ($zip.BaseName.EndsWith('-selfcontained')) { 'selfcontained' } else { 'framework' }
    if ($Variant -ne 'all' -and $Variant -ne $kind) { continue }
    $dir = Join-Path $OutDir "pkg-$kind"
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $dir -Force
    $exe = Join-Path $dir 'DiskThinner.exe'
    if (-not (Test-Path $exe)) { Fail $kind "$($zip.Name) 里没有 DiskThinner.exe"; continue }
    $packages[$kind] = $exe
    $version = (Get-Item $exe).VersionInfo.ProductVersion
    Say "包：$($zip.Name)（$('{0:N1}' -f ($zip.Length / 1MB)) MB），版本 $version"
}

if ($packages.Count -eq 0) { Fail '解压' "在 $PackageDir 里没有找到要测的 zip" }

# ---- 测试文件夹：几个普通文件和一个认得出的缓存目录 ----
$data = Join-Path $OutDir 'data'
foreach ($f in @('docs\a.bin', 'docs\b.bin', 'cache\Cache_Data\data_1', 'cache\Cache_Data\data_2', 'big\blob.bin')) {
    $path = Join-Path $data $f
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $size = if ($f -like 'big\*') { 8MB } else { 256KB }
    $bytes = [byte[]]::new($size)
    [Random]::new(42).NextBytes($bytes)
    [IO.File]::WriteAllBytes($path, $bytes)
}

function Read-Report([string] $path) {
    $r = @{}
    foreach ($line in Get-Content $path -Encoding utf8) {
        $k, $v = $line -split '=', 2
        $r[$k] = $v
    }
    return $r
}

# 启动主程序做一次扫描。返回报告（失败时返回 $null）
function Run-App([string] $case, [string] $exe, [string[]] $arguments) {
    $dir = Join-Path $OutDir $case
    New-Item -ItemType Directory -Force $dir | Out-Null
    $report = Join-Path $dir 'report.txt'
    $common = @('--no-auto-snapshot',
        '--snapshot-db', (Q (Join-Path $dir 'snap.db')),
        '--user-db', (Q (Join-Path $dir 'user.db')),
        '--settings', (Q (Join-Path $dir 'settings.json')),
        '--screenshot', (Q (Join-Path $dir 'screen.png')),
        '--report', (Q $report),
        '--exit-after-scan')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -PassThru -ArgumentList ($arguments + $common)
    $null = $p.Handle # 先取句柄，进程退出后才能拿到退出码
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Fail $case "超过 $TimeoutSeconds 秒没有退出（可能弹出了错误对话框）"
        return $null
    }

    if ($p.ExitCode -ne 0) { Fail $case "退出码 $($p.ExitCode)"; return $null }
    if (-not (Test-Path $report)) { Fail $case '没有生成报告'; return $null }
    $r = Read-Report $report
    if (-not ($r['rules'] -as [int]) -or $r['rules_error']) { Fail $case "规则没有加载：$($r['rules_error'])"; return $null }
    if ($r['automation_error']) { Say "WARN $case：截图失败：$($r['automation_error'])" }
    $r['elapsed'] = '{0:N1}' -f $sw.Elapsed.TotalSeconds
    return $r
}

function Run-Snapshot([string] $case, [string] $exe, [string] $drive) {
    $dir = Join-Path $OutDir $case
    New-Item -ItemType Directory -Force $dir | Out-Null
    $db = Join-Path $dir 'snapshots.db'
    $p = Start-Process -FilePath $exe -PassThru -ArgumentList @('snapshot', '--drive', (Q $drive), '--db', (Q $db))
    $null = $p.Handle
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Fail $case "超过 $TimeoutSeconds 秒没有退出"
        return
    }

    if ($p.ExitCode -ne 0) { Fail $case "退出码 $($p.ExitCode)（见 $env:LOCALAPPDATA\DiskThinner\logs\snapshot.log）"; return }
    if (-not (Test-Path $db) -or (Get-Item $db).Length -eq 0) { Fail $case '没有写出快照库'; return }
    Notice $case "快照库 $('{0:N0}' -f ((Get-Item $db).Length / 1KB)) KB"
}

foreach ($kind in 'selfcontained', 'framework') {
    if (-not $packages.ContainsKey($kind)) { continue }
    $exe = $packages[$kind]

    $r = Run-App "$kind-folder" $exe @('--scan', (Q $data), '--page', 'overview')
    if ($r) {
        if ([long]$r['nodes'] -lt 5) { Fail "$kind-folder" "只扫到 $($r['nodes']) 项" }
        else { Notice "$kind-folder" "扫描 $($r['nodes']) 项，规则 $($r['rules']) 条，用时 $($r['elapsed']) 秒，峰值 $(Mb $r['peak_working_set_bytes'])" }
    }

    Run-Snapshot "$kind-snapshot-folder" $exe $data

    if ($kind -eq 'selfcontained') {
        if (-not $elevated) {
            Say "SKIP selfcontained-volume：不是管理员，跳过整个系统盘的扫描（遍历扫描太慢）"
            continue
        }

        $v = Run-App 'selfcontained-volume' $exe @('--scan', $systemDrive, '--page', 'cleanup', '--show-confirm')
        if ($v) {
            if ($v['mode'] -ne 'Mft') { Fail 'selfcontained-volume' "管理员下应该用 MFT 扫描，实际是 $($v['mode'])（$($v['mft_unavailable_reason'])）" }
            else {
                Notice 'selfcontained-volume' ("MFT 扫描 $systemDrive {0:N0} 项 {1:N1} 秒，峰值 {2}，清理计划 {3} 项（默认勾选 {4} 项，预计释放 {5}），归属推断 {6} 处" -f `
                    [long]$v['nodes'], ([double]$v['scan_ms'] / 1000), (Mb $v['peak_working_set_bytes']), $v['plan_items'], $v['plan_selected'], (Mb $v['plan_estimated_bytes']), $v['inferred_roots'])
            }
        }

        Run-Snapshot 'selfcontained-snapshot-volume' $exe $systemDrive
    }
}

# ---- 运行期间不应该有崩溃报告 ----
$crashes = @(Get-ChildItem $crashDir -Filter 'crash-*.txt' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
foreach ($c in $crashes) {
    Fail '崩溃报告' "$($c.Name)：$((Get-Content $c.FullName -Encoding utf8 | Select-Object -Skip 4 -First 1))"
    Copy-Item $c.FullName $OutDir
}

Say "结束：$((Get-Date).ToString('s'))，失败 $($failures.Count) 项"
if ($failures.Count -gt 0) { exit 1 }
