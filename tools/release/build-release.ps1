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

$zips = @(
    @{ Dir = $fd; Zip = Join-Path $out "DiskThinner-$version-win-x64.zip" },
    @{ Dir = $sc; Zip = Join-Path $out "DiskThinner-$version-win-x64-selfcontained.zip" }
)
foreach ($z in $zips) {
    Compress-Archive -Path (Join-Path $z.Dir '*') -DestinationPath $z.Zip -CompressionLevel Optimal
}

$sums = $zips | ForEach-Object { "$((Get-FileHash $_.Zip -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_.Zip -Leaf)" }
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

foreach ($z in $zips) {
    $exe = Get-Item (Join-Path $z.Dir 'DiskThinner.exe')
    $files = @(Get-ChildItem $z.Dir -File).Count
    "{0}: zip {1:N1} MB, exe {2:N1} MB, {3} 个文件" -f (Split-Path $z.Zip -Leaf), ((Get-Item $z.Zip).Length / 1MB), ($exe.Length / 1MB), $files
}
$sums
