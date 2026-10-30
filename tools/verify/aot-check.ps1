# 常驻服务的 Native AOT 发布检查（docs/phase2b-design.md 5.8 节）：发布 src/DiskThinner.Agent，AOT 警告（IL2xxx / IL3xxx）当作错误。
# CI 的构建任务调用；本机也可以跑。任何失败（包括脚本本身的异常）都写成 GitHub 注解：CI 的日志不登录看不到。
#   pwsh -File tools/verify/aot-check.ps1 [-OutDir artifacts/agent-aot]
param(
    [string] $OutDir = 'artifacts/agent-aot'
)

$ErrorActionPreference = 'Continue'
$ci = $env:GITHUB_ACTIONS -eq 'true'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

function Report([string] $title, [string] $message) {
    if ($ci) { Write-Host "::error title=$title::$message" } else { Write-Host "ERROR $title：$message" }
}

try {
    $out = Join-Path $root $OutDir
    New-Item -ItemType Directory -Force $out | Out-Null
    $log = Join-Path $out 'publish.log'
    $project = Join-Path $root 'src\DiskThinner.Agent\DiskThinner.Agent.csproj'
    $output = & dotnet publish $project -c Release -r win-x64 -o $out -nologo 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { "$_" } | Set-Content $log -Encoding utf8
    $lines = @(Get-Content $log -Encoding utf8)
    $warnings = @($lines | Where-Object { $_ -match 'warning IL\d{4}' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    foreach ($w in $warnings) { Report 'AOT 警告' $w }
    if ($code -ne 0) {
        $errors = @($lines | Where-Object { $_ -match ' error |error [A-Z]+\d+' } | ForEach-Object { $_.Trim() } | Select-Object -Unique -First 10)
        foreach ($e in $errors + @($lines | Select-Object -Last 5)) { Report 'AOT 发布' $e }
        Write-Host "dotnet publish 退出码 $code"
        exit 1
    }

    if ($warnings.Count -gt 0) { Write-Host "AOT 发布有 $($warnings.Count) 个警告"; exit 1 }
    $exe = Join-Path $out 'DiskThinner.Agent.exe'
    if (-not (Test-Path $exe)) { Report 'AOT 发布' "没有生成 $exe"; exit 1 }
    Write-Host ("AOT 发布：DiskThinner.Agent.exe {0:N2} MB，没有 AOT 警告" -f ((Get-Item $exe).Length / 1MB))
    exit 0
}
catch {
    Report 'AOT 检查脚本出错' "$_"
    exit 1
}
