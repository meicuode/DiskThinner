# 打包发布（研发方案 M5）：两个 zip，放在 artifacts\release\<版本>\
#   DiskThinner-<版本>-win-x64.zip               依赖 .NET 10 桌面运行库（体积小）
#   DiskThinner-<版本>-win-x64-selfcontained.zip  单文件自包含（不需要装运行库）
# 另外生成 SHA256SUMS.txt。只构建和打包，不上传、不发布（发布由 .github/workflows/release.yml 在推送标签时做）。
# 用法：pwsh -File tools\release\build-release.ps1 [-Version 0.2.0]
#   不带 -Version 时用 Directory.Build.props 里的版本号；带了就按它构建（发布工作流传入标签里的版本号）。
param(
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.Version
}

# 主.次.修订，可带预发布后缀（0.2.0-beta.1）。版本号会拼进路径和文件名，格式不对直接报错
if ($Version -cnotmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$') { throw "版本号格式不对：$Version" }
$version = $Version
$out = Join-Path $root "artifacts\release\$version"
if (Test-Path $out) { throw "$out 已经存在，请先移走或改版本号（脚本不覆盖已有的发布文件）" }
New-Item -ItemType Directory -Force $out | Out-Null
$logs = Join-Path $out 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$project = Join-Path $root 'src\DiskThinner.App\DiskThinner.App.csproj'

function Publish([string] $name, [string[]] $extra) {
    $dir = Join-Path $out $name
    $log = Join-Path $logs "$name.log"
    $args = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $dir, '-nologo', '-p:DebugType=none', '-p:GenerateDocumentationFile=false',
        "-p:Version=$version") + $extra
    & $dotnet @args *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 30; throw "$name 发布失败" }
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $dir
    return $dir
}

$fd = Publish 'DiskThinner' @('--self-contained', 'false')
# 单文件里的程序集不压缩：压缩后 zip 只小 1 MB 左右（zip 本身已经压缩），但启动时要把程序集解压到内存，
# 实测峰值内存多出约 100 MB（87 MB → 193 MB），MFT 扫描整个盘时会超过 300 MB 的目标。
$sc = Publish 'DiskThinner-selfcontained' @('--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=false')

# 常驻监控服务（阶段 2b，docs/phase2b-design.md 5.8 节）：Native AOT 发布一次，两个包都放进 Agent\（只要 exe 和 e_sqlite3.dll）。
# AOT 警告当作错误：有任何 IL2xxx / IL3xxx 警告就不打包
$agentDir = Join-Path $out 'agent-publish'
$agentLog = Join-Path $logs 'agent.log'
& $dotnet publish (Join-Path $root 'src\DiskThinner.Agent\DiskThinner.Agent.csproj') -c Release -r win-x64 -o $agentDir -nologo -p:DebugType=none "-p:Version=$version" *> $agentLog
if ($LASTEXITCODE -ne 0) { Get-Content $agentLog -Tail 30; throw '服务的 AOT 发布失败' }
$aotWarnings = @(Select-String -Path $agentLog -Pattern 'warning (IL\d{4}|IL3\d{3})' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique)
if ($aotWarnings.Count -gt 0) { $aotWarnings | Select-Object -First 10; throw "服务的 AOT 发布有 $($aotWarnings.Count) 个警告" }
foreach ($dir in $fd, $sc) {
    $target = Join-Path $dir 'Agent'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $agentDir 'DiskThinner.Agent.exe'), (Join-Path $agentDir 'e_sqlite3.dll') $target
}

# 第 1.2 节：依赖运行库版解压后（含服务）不超过 10 MB
$fdBytes = (Get-ChildItem $fd -File -Recurse | Measure-Object Length -Sum).Sum
if ($fdBytes -gt 10MB) { throw ("依赖运行库版解压后 {0:N2} MB，超过 10 MB" -f ($fdBytes / 1MB)) }

$zips = @(
    @{ Dir = $fd; Zip = Join-Path $out "DiskThinner-$version-win-x64.zip" },
    @{ Dir = $sc; Zip = Join-Path $out "DiskThinner-$version-win-x64-selfcontained.zip" }
)
foreach ($z in $zips) {
    # 2b 起产品直接调用 ETW API：TraceEvent、它的原生 DLL 和互操作程序集一个都不要（docs/phase2b-design.md 5.7 节）；有就不打包
    $base = (Resolve-Path $z.Dir).Path.TrimEnd('\') + '\'
    $bad = @(Get-ChildItem $z.Dir -File -Recurse | Where-Object {
            $_.FullName.Substring($base.Length) -match '^(x86|arm64|amd64)\\' -or $_.Name -in 'Microsoft.Diagnostics.Tracing.TraceEvent.dll', 'Microsoft.Diagnostics.FastSerialization.dll', 'Dia2Lib.dll', 'TraceReloggerLib.dll', 'KernelTraceControl.dll', 'msdia140.dll' })
    if ($bad.Count -gt 0) { throw "$(Split-Path $z.Dir -Leaf) 里有不该带的文件：$(($bad | ForEach-Object Name) -join ', ')" }
    Compress-Archive -Path (Join-Path $z.Dir '*') -DestinationPath $z.Zip -CompressionLevel Optimal
}

$sums = $zips | ForEach-Object { "$((Get-FileHash $_.Zip -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_.Zip -Leaf)" }
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

foreach ($z in $zips) {
    $exe = Get-Item (Join-Path $z.Dir 'DiskThinner.exe')
    $all = @(Get-ChildItem $z.Dir -File -Recurse)
    # 文件清单（相对路径和大小），和加 TraceEvent 之前的对比写进阶段报告（docs/phase2a-design.md 1.3 节第 15 条）
    $base = (Resolve-Path $z.Dir).Path.TrimEnd('\') + '\'
    $list = $all | ForEach-Object { "{0}`t{1}" -f $_.FullName.Substring($base.Length), $_.Length } | Sort-Object
    Set-Content (Join-Path $out "files-$(Split-Path $z.Dir -Leaf).txt") $list -Encoding utf8
    "{0}: zip {1:N1} MB, exe {2:N1} MB, {3} 个文件, 共 {4:N1} MB" -f (Split-Path $z.Zip -Leaf), ((Get-Item $z.Zip).Length / 1MB), ($exe.Length / 1MB), $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB)
}
$sums
