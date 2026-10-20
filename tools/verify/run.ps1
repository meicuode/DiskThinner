# 在独立进程里运行构建或测试，完整输出写到 artifacts\logs\<名称>.log，屏幕上只打印摘要。
# 用于避免长输出在代理终端里被截断。
#   pwsh -File tools\verify\run.ps1 build              构建整个解决方案
#   pwsh -File tools\verify\run.ps1 release            发布配置构建
#   pwsh -File tools\verify\run.ps1 test               构建并运行全部测试
#   pwsh -File tools\verify\run.ps1 test <项目路径>     构建并运行一个测试项目
param(
    [Parameter(Mandatory)] [ValidateSet('build', 'test', 'release')] [string] $Action,
    [string] $Project = 'DiskThinner.slnx'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$logs = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$script:LastCode = 0
# 从隐藏窗口启动时 PATH 里不一定有 dotnet，优先用完整路径
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

function Invoke-Logged([string] $name, [string[]] $arguments) {
    $log = Join-Path $logs "$name.log"
    $p = Start-Process $dotnet -ArgumentList $arguments -WorkingDirectory $root -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $p.WaitForExit()
    $lines = Get-Content $log
    $lines | Select-String -Pattern ' error |: error |warning [A-Z]+\d+' |
        ForEach-Object { ($_.Line -replace '\s*\[[A-Z]:\\[^\]]*\]$', '').Trim() } |
        Select-Object -Unique -First 30 | ForEach-Object { Write-Host $_ }
    $lines | Select-String -Pattern 'Warning\(s\)|Error\(s\)|Passed!|Failed!|^\s+Failed |Error Message|Expected:|Actual:' |
        ForEach-Object { $_.Line.Trim() } | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
    Write-Host "$name exit=$($p.ExitCode)"
    $script:LastCode = $p.ExitCode
}

switch ($Action) {
    'build' { Invoke-Logged 'build' @('build', $Project, '-nologo', '-v', 'q') }
    'release' { Invoke-Logged 'release' @('build', $Project, '-c', 'Release', '-nologo', '-v', 'q') }
    'test' {
        Invoke-Logged 'build' @('build', $Project, '-nologo', '-v', 'q')
        if ($script:LastCode -eq 0) {
            Invoke-Logged 'test' @('test', $Project, '--no-build', '-nologo', '--blame-hang-timeout', '90s')
        }
    }
}

exit $script:LastCode
