# 阶段 2b 需要管理员的测试和验收（docs/phase2b-design.md 8.2 节；以管理员身份运行，会先弹出 UAC）。
#   -Part agent：以 DT_AGENT_TESTS=1 跑 AgentServiceTests：装一个测试服务（DiskThinnerAgentTest，测试自己的管道名、会话名和
#     %TEMP%\DiskThinner-2b-* 数据目录），核对启动、记录和查询、控制命令的权限、外部停止后重开、强制结束后重启、错误退出后重启、停止，
#     最后删除服务和测试目录。服务程序用 -AgentExe（AOT 发布版）。运行前先用普通权限构建：
#       dotnet publish src/DiskThinner.Agent -c Release -r win-x64 -o artifacts\phase2b\agent
#       dotnet build tests/DiskThinner.Agent.Tests -c Release
# 负载约束：本脚本设为低于正常优先级，子进程继承；同一时间只有一个测试进程。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File','tools\verify\phase2b-elevated.ps1','-OutDir','<目录>','-Part','agent','-AgentExe','<exe>'
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidatePattern('^(agent)(,(agent))*$')] [string] $Part = 'agent',
    [string] $AgentExe
)

$ErrorActionPreference = 'Stop'
$parts = $Part -split ','
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$env:PATH = (Split-Path $dotnet) + ';' + $env:PATH

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
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

function DiskThinnerSessions { @(logman query -ets | Select-String -Pattern 'DiskThinner' | ForEach-Object { ($_.Line -split '\s+')[0] }) }

if ($parts -contains 'agent') {
    Step 'agent-tests' {
        $env:DT_AGENT_TESTS = '1'
        $env:DT_AGENT_REPORT_DIR = $OutDir
        if ($AgentExe) { $env:DT_AGENT_EXE = (Resolve-Path $AgentExe).Path }
        $env:DOTNET_CLI_UI_LANGUAGE = 'en'
        $project = Join-Path $root 'tests\DiskThinner.Agent.Tests\DiskThinner.Agent.Tests.csproj'
        $testLog = Join-Path $OutDir 'agent-tests.log'
        $p = Start-Process $dotnet -ArgumentList 'test', "`"$project`"", '-c', 'Release', '--no-build', '-nologo', '--filter', 'FullyQualifiedName~AgentServiceTests',
            '--results-directory', "`"$(Join-Path $OutDir 'test-results')`"" -NoNewWindow -PassThru -RedirectStandardOutput $testLog -RedirectStandardError "$testLog.err"
        if (-not $p.WaitForExit(15 * 60 * 1000)) { $p.Kill(); throw '提权测试超时' }
        $summary = (Select-String -Path $testLog -Pattern 'Passed!|Failed!' | ForEach-Object { $_.Line.Trim() }) -join ' '
        Add-Content $log "agent-tests exit=$($p.ExitCode) $summary"
        Get-ChildItem $OutDir -Filter 'agent-tests-*.txt' | ForEach-Object { Add-Content $log "notes $($_.Name): $((Get-Content $_.FullName) -join ' ')" }
        if ($p.ExitCode -ne 0) { throw "dotnet test 退出码 $($p.ExitCode)" }
        if ($summary -notmatch 'Skipped:\s+0,') { throw "有测试被跳过：$summary" }
    }
    $left = @(sc.exe query DiskThinnerAgentTest 2>$null | Select-String 'SERVICE_NAME').Count
    Add-Content $log "test_service_left=$left sessions_after=$((DiskThinnerSessions) -join ',')"
}

Add-Content $log "done $(Get-Date -Format s)"
