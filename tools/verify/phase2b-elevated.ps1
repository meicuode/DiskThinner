# 阶段 2b 需要管理员的测试和验收（docs/phase2b-design.md 8.2 节；以管理员身份运行，会先弹出 UAC）。
#   -Part agent：以 DT_AGENT_TESTS=1 跑 AgentServiceTests：装一个测试服务（DiskThinnerAgentTest，测试自己的管道名、会话名和
#     %TEMP%\DiskThinner-2b-* 数据目录），核对启动、记录和查询、控制命令的权限、外部停止后重开、强制结束后重启、错误退出后重启、停止，
#     最后删除服务和测试目录。服务程序用 -AgentExe（AOT 发布版）。运行前先用普通权限构建：
#       dotnet publish src/DiskThinner.Agent -c Release -r win-x64 -o artifacts\phase2b\agent
#       dotnet build tests/DiskThinner.Agent.Tests -c Release
# 负载约束：本脚本设为低于正常优先级，子进程继承；同一时间只有一个测试进程。
# 用法：Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File','tools\verify\phase2b-elevated.ps1','-OutDir','<目录>','-Part','agent','-AgentExe','<exe>'
#   -Part accept：真实服务名 DiskThinnerAgent 的验收（第 1.3 节第 1–9、12 条），用 DiskThinner.exe agent 子命令（-AgentSource 是 AOT 发布目录）：
#     安装（预先建好、权限不对的数据目录会被重设）→ 服务配置、失败动作、会话、程序目录和数据目录的权限 → 普通权限（runas /trustlevel）能查到数据 →
#     负载 → 界面截图（管理员和普通权限）→ 外部停止后重开 → 强制结束后重启 → 停止、启动 → 更新（同版本、新版本、坏的新版本退回）→
#     卸载（保留数据）→ 重新安装接着用 → 卸载并删除数据。开始前已经装了服务、或者数据目录已经存在时不做（不碰已有的安装和数据）。
#     运行前先用普通权限构建：dotnet build src/DiskThinner.App -c Release; dotnet build tests/DiskThinner.WriteLoad -c Release
#   -Part perf：装上服务空闲 -IdleMinutes 分钟（默认 30），每分钟记一次 CPU 和工作集，然后卸载并删除数据（第 1.3 节第 14 条）。
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidatePattern('^(agent|accept|perf)(,(agent|accept|perf))*$')] [string] $Part = 'agent',
    [string] $AgentExe,
    [string] $AgentSource,
    [int] $IdleMinutes = 30
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

$exe = Join-Path $root 'src\DiskThinner.App\bin\Release\net10.0-windows\DiskThinner.exe'
$svc = 'DiskThinnerAgent'
$dataDir = Join-Path $env:ProgramData 'DiskThinner\Agent'
$progDir = Join-Path $env:ProgramFiles 'DiskThinner\Agent'
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())

function ReadReport([string] $path) {
    $h = @{}
    if (Test-Path $path) { Get-Content $path -Encoding utf8 | ForEach-Object { $k, $v = $_ -split '=', 2; $h[$k] = $v } }
    $h
}

# DiskThinner.exe agent ...：结果是 --report 的键值，另加 __exit
function Agent([string[]] $arguments, [string] $name, [string] $source = $AgentSource, [switch] $Quiet) {
    $r = Join-Path $OutDir "agent-$name.txt"
    Remove-Item $r -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList (@('agent') + $arguments + @('--agent-source', "`"$source`"", '--report', "`"$r`"")) -PassThru
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "agent $($arguments -join ' ') 超过 3 分钟没有结束" }
    $h = ReadReport $r
    $h['__exit'] = "$($p.ExitCode)"
    if (-not $Quiet) { Add-Content $log "agent-$name exit=$($p.ExitCode) $((Get-Content $r -Encoding utf8 -ErrorAction SilentlyContinue) -join ' ')" }
    $h
}

function WaitUntil([scriptblock] $condition, [int] $seconds, [string] $what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        if (& $condition) { return [int]$sw.Elapsed.TotalSeconds }
        Start-Sleep 3
    }
    throw "$what：$seconds 秒内没有做到"
}

function AgentSessions { @(DiskThinnerSessions | Where-Object { $_ -eq 'DiskThinner-Agent' }) }
function ServicePid { [int](Get-CimInstance Win32_Service -Filter "Name='$svc'").ProcessId }
function NewTempDir([string] $tag) {
    $d = Join-Path $temp ("DiskThinner-2b-$tag-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $d | Out-Null
    $script:made.Add($d)
    $d
}

if ($parts -contains 'accept') {
    $script:failures = 0
    $script:made = [System.Collections.Generic.List[string]]::new()
    $AgentSource = (Resolve-Path $AgentSource).Path
    $loadExe = Join-Path $root 'tests\DiskThinner.WriteLoad\bin\Release\net10.0-windows\DiskThinner.WriteLoad.exe'
    $snap = Join-Path $OutDir 'ui-snap.db'
    $mon = Join-Path $OutDir 'ui-mon.db'
    $userDb = Join-Path $OutDir 'ui-user.db'
    $settings = Join-Path $OutDir 'ui-settings.json'
    Remove-Item $snap, $mon, $userDb -ErrorAction SilentlyContinue
    Set-Content $settings '{ "monitor": { "enabled": "agent", "mode": "precise", "keepDays": 90 } }' -Encoding utf8

    function AcceptStep($name, [scriptblock] $body) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            & $body
            Add-Content $log "$name ok $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
        } catch {
            $script:failures++
            Add-Content $log "$name FAILED: $_"
        }
    }

    # 截图：-AsUser 时用 runas /trustlevel:0x20000 以普通权限（去掉 Administrators）启动
    function Shot([string] $name, [string] $dir, [string[]] $extra, [switch] $AsUser) {
        $png = Join-Path $OutDir "ui-$name.png"
        $rep = Join-Path $OutDir "ui-$name.txt"
        Remove-Item $png, $rep -ErrorAction SilentlyContinue
        $a = @('--scan', $dir, '--snapshot-db', $snap, '--monitor-db', $mon, '--settings', $settings, '--user-db', $userDb, '--no-auto-snapshot', '--primary-screen',
            '--exit-after-scan', '--agent-source', $AgentSource, '--screenshot', $png, '--report', $rep) + $extra
        if ($AsUser) {
            if (($a + $exe) -match ' ') { throw '路径里有空格，runas 的命令行不好转义' }
            Start-Process runas.exe -ArgumentList '/trustlevel:0x20000', ('"' + $exe + ' ' + ($a -join ' ') + '"') -WindowStyle Hidden
            WaitUntil { (Test-Path $rep) -and (Test-Path $png) } 180 "截图 $name" | Out-Null
            Start-Sleep 2
        } else {
            $p = Start-Process $exe -ArgumentList $a -PassThru
            if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "截图 $name 超时" }
        }
        $r = ReadReport $rep
        Add-Content $log "shot-$name elevated=$($r['elevated']) state=$($r['monitor_state']) agent=$($r['agent_service_state'])/$($r['agent_recording']) sessions_30d=$($r['monitor_sessions_30d']) agent_sessions=$($r['monitor_agent_sessions_30d']) settings=[$($r['agent_settings_status'])] status=[$($r['status_monitor'])] writers=[$($r['growth_writer_cells'])] error=$($r['automation_error'])"
        $r
    }

    # ---- 前提：没有装过、没有数据目录（不碰已有的） ----
    $existing = (Get-Service $svc -ErrorAction SilentlyContinue) -or (Test-Path $dataDir) -or (Test-Path $progDir)
    Add-Content $log "accept_precondition existing=$existing"
    if ($existing) {
        $script:failures++
        Add-Content $log 'accept SKIPPED: 已经有 DiskThinnerAgent 服务或者数据目录，验收不碰已有的安装和数据'
    } else {
        $loadDir = $null
        try {
            AcceptStep 'b0-ui-not-installed' {
                $d = NewTempDir 'ui0'
                Set-Content (Join-Path $d 'a.txt') 'x'
                $r = Shot 'not-installed' $d @('--page', 'settings', '--show-agent-dialog')
                if ($r['agent_settings_status'] -ne '没有安装') { throw "设置页：$($r['agent_settings_status'])" }
                if (-not (Test-Path (Join-Path $OutDir 'ui-not-installed-agent.png'))) { throw '没有安装对话框的截图' }
            }

            # 1.3 第 9 条：预先建好的数据目录（继承了 ProgramData 的权限，普通用户可以写），里面有一个不认识的文件
            AcceptStep 'b1-install' {
                New-Item -ItemType Directory -Force $dataDir | Out-Null
                Set-Content (Join-Path $dataDir 'planted.txt') 'x'
                $r = Agent @('--install', '--mode', 'precise', '--keep-days', '90', '--flush-seconds', '5') 'install'
                if ($r['ok'] -ne '1') { throw "安装失败：$($r['message']) $($r['log_tail'])" }
                if ($r['acl_reset'] -ne '1') { throw '预先建好的数据目录没有报告权限重设' }
                if (-not (Test-Path (Join-Path $dataDir 'planted.txt'))) { throw '不认识的文件被删了' }
                Remove-Item (Join-Path $dataDir 'planted.txt') # 测试自己放的
            }

            AcceptStep 'b2-config' {
                $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
                $k = Get-ItemProperty $key
                $fa = [byte[]]$k.FailureActions
                $reset = [BitConverter]::ToUInt32($fa, 0); $count = [BitConverter]::ToUInt32($fa, 12)
                $actions = @(for ($i = 0; $i -lt $count; $i++) { '{0}/{1}' -f [BitConverter]::ToUInt32($fa, 20 + 8 * $i), [BitConverter]::ToUInt32($fa, 24 + 8 * $i) })
                Add-Content $log "b2 start=$($k.Start) delayed=$($k.DelayedAutostart) account=$($k.ObjectName) sidtype=$($k.ServiceSidType) noncrash=$($k.FailureActionsOnNonCrashFailures) preshutdown=$($k.PreshutdownTimeout) reset=$reset actions=$($actions -join ',') image=$($k.ImagePath)"
                if ($k.Start -ne 2 -or $k.DelayedAutostart -ne 1 -or $k.ObjectName -ne 'LocalSystem' -or $k.ServiceSidType -ne 1 -or $k.FailureActionsOnNonCrashFailures -ne 1 -or $k.PreshutdownTimeout -ne 15000) { throw '服务配置不对' }
                if ($reset -ne 86400 -or ($actions -join ',') -ne '1/60000,1/300000,0/0') { throw "失败动作不对：$($actions -join ',')" }
                if (-not $k.ImagePath.StartsWith('"' + $progDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "程序路径不对：$($k.ImagePath)" }
                if ((AgentSessions).Count -ne 1) { throw "DiskThinner-Agent 会话不是一个：$((DiskThinnerSessions) -join ',')" }
            }

            AcceptStep 'b3-acl' {
                $weak = 'S-1-5-32-545', 'S-1-1-0', 'S-1-5-11', 'S-1-5-4'
                $sidType = [Security.Principal.SecurityIdentifier]
                $writable = @((Get-Acl $progDir).GetAccessRules($true, $true, $sidType) | Where-Object {
                        $_.AccessControlType -eq 'Allow' -and $weak -contains $_.IdentityReference.Value -and ([int64]$_.FileSystemRights -band 0xD0116) -ne 0 })
                $dataRules = @((Get-Acl $dataDir).GetAccessRules($true, $true, $sidType) | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
                $protected = (Get-Acl $dataDir).AreAccessRulesProtected
                Add-Content $log "b3 program_writable_by_users=$($writable.Count) data_rules=$($dataRules -join ',') data_protected=$protected"
                if ($writable.Count -gt 0) { throw '普通用户能写程序目录' }
                if (-not $protected -or ($dataRules -join ',') -ne 'S-1-5-18,S-1-5-32-544') { throw '数据目录的权限不对' }
            }

            AcceptStep 'b4-load' {
                $script:loadDir = NewTempDir 'load'
                New-Item -ItemType Directory (Join-Path $loadDir 'big') | Out-Null
                $f = [IO.File]::Create((Join-Path $loadDir 'seed.bin')); $f.SetLength(60MB); $f.Close()
                $p = Start-Process $exe -ArgumentList 'snapshot', '--drive', $loadDir, '--db', $snap -PassThru
                if (-not $p.WaitForExit(60000) -or $p.ExitCode -ne 0) { throw '快照失败' }
                Start-Sleep 2
                $small = Join-Path $loadDir 'small30'
                New-Item -ItemType Directory $small | Out-Null
                $bytes = New-Object byte[] 65536
                for ($i = 0; $i -lt 30; $i++) { [IO.File]::WriteAllBytes((Join-Path $small "f$i.bin"), $bytes) }
                $l = Start-Process $loadExe -ArgumentList '--dir', $loadDir, '--report', (Join-Path $OutDir 'load.json'), '--small', '200', '--delete', '20', '--rename', '20' -PassThru -WindowStyle Hidden
                if (-not $l.WaitForExit(180000)) { $l.Kill(); throw 'WriteLoad 超时' }
                $waited = WaitUntil { [long]((Agent @('--status', '--dir', $small) 'status-load' -Quiet)['dir_creates']) -ge 30 } 60 '负载落盘'
                $r = Agent @('--status', '--dir', $loadDir) 'status-load'
                Add-Content $log "b4 waited_s=$waited"
                if ([long]$r['dir_creates'] -lt 230 -or [long]$r['dir_growth'] -lt 30 * 65536) { throw "服务的数据不对：creates=$($r['dir_creates']) growth=$($r['dir_growth'])" }
                if ($r['agent_state'] -ne 'recording' -or $r['lost'] -ne '0') { Add-Content $log "b4 warning: state=$($r['agent_state']) lost=$($r['lost'])" }
            }

            # FR-3：普通权限（去掉 Administrators 的令牌）也能经管道查到数据
            AcceptStep 'b5-user-query' {
                $ru = Join-Path $OutDir 'agent-status-user.txt'
                Remove-Item $ru -ErrorAction SilentlyContinue
                if (($exe + $loadDir + $ru) -match ' ') { throw '路径里有空格' }
                Start-Process runas.exe -ArgumentList '/trustlevel:0x20000', ('"' + "$exe agent --status --dir $loadDir --report $ru" + '"') -WindowStyle Hidden
                WaitUntil { Test-Path $ru } 60 '普通权限的查询' | Out-Null
                Start-Sleep 1
                $r = ReadReport $ru
                Add-Content $log "b5 $((Get-Content $ru -Encoding utf8) -join ' ')"
                if ($r['agent_state'] -ne 'recording' -or [long]$r['dir_creates'] -lt 230) { throw '普通权限查不到数据' }
            }

            AcceptStep 'b6-ui-running' {
                $g = Shot 'running-growth' $loadDir @('--page', 'growth')
                if ($g['monitor_state'] -ne 'Agent' -or [int]$g['monitor_agent_sessions_30d'] -lt 1) { throw "增长分析：state=$($g['monitor_state'])" }
                $s = Shot 'running-settings' $loadDir @('--page', 'settings')
                if (-not $s['agent_settings_status'].StartsWith('运行中')) { throw "设置页：$($s['agent_settings_status'])" }
                Shot 'running-overview' $loadDir @('--page', 'overview') | Out-Null
                Shot 'running-explorer' $loadDir @('--page', 'explorer', '--select', (Join-Path $loadDir 'big')) | Out-Null
                $u = Shot 'user-settings' $loadDir @('--page', 'settings') -AsUser
                if ($u['elevated'] -ne 'False' -or -not $u['agent_settings_status'].StartsWith('运行中')) { throw "普通权限的设置页：elevated=$($u['elevated']) $($u['agent_settings_status'])" }
                Shot 'user-growth' $loadDir @('--page', 'growth') -AsUser | Out-Null
            }

            # 1.3 第 4 条：外部停止后 1 到 2 分钟内重开
            AcceptStep 'b7-external-stop' {
                logman stop DiskThinner-Agent -ets | Out-Null
                $s = WaitUntil { $r = Agent @('--status') 'status-ext' -Quiet; $r['agent_state'] -eq 'recording' -and [int]($r['external_stops']) -ge 1 } 150 '外部停止后重开'
                Add-Content $log "b7 restarted_after_s=$s"
                if ((AgentSessions).Count -ne 1) { throw '重开后会话不是一个' }
            }

            # 1.3 第 5 条：taskkill /F 后 SCM 1 分钟后重启，新服务清掉残留会话再开新会话，那一行标"程序意外退出"
            AcceptStep 'b8-kill' {
                Start-Sleep 6 # 等一次落盘
                $old = ServicePid
                taskkill /F /PID $old | Out-Null
                $s = WaitUntil { $p = ServicePid; $p -ne 0 -and $p -ne $old -and (Agent @('--status') 'status-kill' -Quiet)['agent_state'] -eq 'recording' } 150 '强制结束后重启'
                $r = Agent @('--status', '--dir', $loadDir) 'status-kill'
                Add-Content $log "b8 restarted_after_s=$s reasons=$($r['session_reasons'])"
                if ($r['session_reasons'] -notmatch 'crashed' -or $r['session_reasons'] -notmatch 'external') { throw "会话行：$($r['session_reasons'])" }
                if ((AgentSessions).Count -ne 1) { throw '重启后会话不是一个' }
            }

            # 1.3 第 6 条：停止后会话消失；再启动后继续记录
            AcceptStep 'b9-stop-start' {
                sc.exe stop $svc | Out-Null
                WaitUntil { (Get-Service $svc).Status -eq 'Stopped' } 30 '停止' | Out-Null
                if ((AgentSessions).Count -ne 0) { throw '停止后会话还在' }
                $r = Shot 'stopped-settings' $loadDir @('--page', 'settings')
                if ($r['agent_settings_status'] -ne '已停止' -or $r['monitor_state'] -ne 'AgentDown') { throw "停止后的设置页：$($r['agent_settings_status']) $($r['monitor_state'])" }
                sc.exe start $svc | Out-Null
                WaitUntil { (Agent @('--status') 'status-start' -Quiet)['agent_state'] -eq 'recording' } 60 '启动' | Out-Null
            }

            # 1.3 第 7 条：同版本 → 已是最新；改了的程序 → 新目录、旧目录删掉；坏的程序 → 退回
            AcceptStep 'b10-update' {
                $same = Agent @('--update') 'update-same'
                if ($same['ok'] -ne '1' -or $same['message'] -notmatch '最新') { throw "同版本更新：$($same['message'])" }
                $before = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc").ImagePath
                $src1 = NewTempDir 'src1'
                Copy-Item (Join-Path $AgentSource 'DiskThinner.Agent.exe'), (Join-Path $AgentSource 'e_sqlite3.dll') $src1
                $fs = [IO.File]::Open((Join-Path $src1 'DiskThinner.Agent.exe'), 'Append'); $fs.Write([byte[]](1..16), 0, 16); $fs.Close() # 末尾加 16 字节：程序照样能跑，哈希不同
                $u1 = Agent @('--update') 'update-new' $src1
                $after = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc").ImagePath
                $dirs = @(Get-ChildItem $progDir -Directory)
                Add-Content $log "b10 before=$before after=$after dirs=$($dirs.Count)"
                if ($u1['ok'] -ne '1' -or $after -eq $before -or $dirs.Count -ne 1) { throw "新版本更新：$($u1['message'])" }
                WaitUntil { (Agent @('--status') 'status-upd' -Quiet)['agent_state'] -eq 'recording' } 60 '更新后记录' | Out-Null
                $src2 = NewTempDir 'src2'
                Copy-Item (Join-Path $AgentSource 'e_sqlite3.dll') $src2
                Set-Content (Join-Path $src2 'DiskThinner.Agent.exe') 'not a program'
                $u2 = Agent @('--update') 'update-broken' $src2
                $restored = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc").ImagePath
                Add-Content $log "b10 broken_ok=$($u2['ok']) restored=$restored dirs=$(@(Get-ChildItem $progDir -Directory).Count)"
                if ($u2['ok'] -ne '0' -or $restored -ne $after -or @(Get-ChildItem $progDir -Directory).Count -ne 1) { throw "坏的新版本没有退回：$($u2['message'])" }
                WaitUntil { (Agent @('--status') 'status-restored' -Quiet)['agent_state'] -eq 'recording' } 60 '退回后记录' | Out-Null
            }

            # 1.3 第 8 条：卸载（不删数据）→ 重新安装接着用 → 卸载并删除数据
            AcceptStep 'b11-uninstall-keep' {
                $r = Agent @('--uninstall') 'uninstall-keep'
                if ($r['ok'] -ne '1' -or (Get-Service $svc -ErrorAction SilentlyContinue) -or (Test-Path $progDir) -or -not (Test-Path (Join-Path $dataDir 'monitor.db')) -or (AgentSessions).Count -ne 0) {
                    throw "卸载（保留数据）：$($r['message'])"
                }
            }

            AcceptStep 'b12-reinstall' {
                $r = Agent @('--install', '--mode', 'light', '--keep-days', '30') 'reinstall'
                if ($r['ok'] -ne '1' -or $r['acl_reset'] -ne '0') { throw "重新安装：$($r['message']) acl_reset=$($r['acl_reset'])" }
                $s = Agent @('--status', '--dir', $loadDir) 'status-reinstall'
                if ($s['mode'] -ne 'light' -or $s['keep_days'] -ne '30' -or [long]$s['dir_creates'] -lt 230) { throw "重新安装后：mode=$($s['mode']) creates=$($s['dir_creates'])" }
            }
        }
        finally {
            AcceptStep 'b13-uninstall-delete' {
                $r = Agent @('--uninstall', '--delete-data') 'uninstall-delete'
                $pf = Join-Path $env:ProgramFiles 'DiskThinner'
                if ($r['ok'] -ne '1' -or (Get-Service $svc -ErrorAction SilentlyContinue) -or (Test-Path $dataDir) -or (Test-Path $progDir) -or (Test-Path $pf) -or (AgentSessions).Count -ne 0) {
                    throw "卸载（删除数据）：$($r['message'])"
                }
            }

            foreach ($d in $script:made) {
                $full = [IO.Path]::GetFullPath($d)
                if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $full -Leaf) -like 'DiskThinner-2b-*' -and (Test-Path $full)) {
                    Remove-Item $full -Recurse -Force
                }
            }
        }
    }

    Add-Content $log "sessions_after=$((DiskThinnerSessions) -join ',')"
    Add-Content $log "accept_failures=$script:failures"
}

# 第 1.3 节第 14 条：装上真实服务，空闲 -IdleMinutes 分钟，每分钟记一次服务的 CPU（服务自己报的，从会话开始算）和工作集，最后卸载并删除数据
if ($parts -contains 'perf') {
    Step 'perf' {
        if ((Get-Service $svc -ErrorAction SilentlyContinue) -or (Test-Path $dataDir)) { throw '已经有服务或数据目录，不做' }
        $AgentSource = (Resolve-Path $AgentSource).Path
        try {
            $r = Agent @('--install', '--mode', 'precise') 'perf-install'
            if ($r['ok'] -ne '1') { throw "安装失败：$($r['message'])" }
            $samples = [System.Collections.Generic.List[string]]::new()
            for ($m = 1; $m -le $IdleMinutes; $m++) {
                Start-Sleep 60
                $s = Agent @('--status') 'perf-status' -Quiet
                $proc = Get-Process -Id (ServicePid) -ErrorAction SilentlyContinue
                $samples.Add("$m,$($s['cpu_percent']),$($s['working_set_bytes']),$($proc.PrivateMemorySize64),$($s['received']),$($s['lost'])")
                Set-Content (Join-Path $OutDir 'perf-samples.csv') (@('minute,cpu_percent,working_set,private,received,lost') + $samples)
            }
            Add-Content $log "perf last=$($samples[-1]) peak_ws=$(($samples | ForEach-Object { [long]($_ -split ',')[2] } | Measure-Object -Maximum).Maximum)"
        }
        finally {
            $u = Agent @('--uninstall', '--delete-data') 'perf-uninstall'
            Add-Content $log "perf uninstall ok=$($u['ok'])"
        }
    }
}

Add-Content $log "done $(Get-Date -Format s)"
