# 导出写入监控用到的 ETW 事件模板（docs/phase2a-design.md 2.1、5.4 节），写成 tests/fixtures/monitor/templates.txt。
# 普通权限即可（读的是系统里注册的清单）。解析器的模板核对测试按这份文件拼字节，换了 Windows 版本时重新导出一次对比。
# 用法：pwsh -File tools/verify/export-etw-templates.ps1 [-Out tests/fixtures/monitor/templates.txt]
param(
    [string] $Out = (Join-Path $PSScriptRoot '..\..\tests\fixtures\monitor\templates.txt')
)

$ErrorActionPreference = 'Stop'

# 只导出用到的事件：Kernel-File 的 NameCreate / NameDelete / Write / DeletePath / RenamePath / SetLinkPath / CreateNewFile，
# Kernel-Process 的 ProcessStart / ProcessStop / ProcessRundown
$wanted = @{
    'Microsoft-Windows-Kernel-File'    = @(10, 11, 16, 26, 27, 28, 30)
    'Microsoft-Windows-Kernel-Process' = @(1, 2, 15)
}

$build = [Environment]::OSVersion.Version
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# ETW 事件模板，由 tools/verify/export-etw-templates.ps1 从本机清单导出。不要手工改。")
$lines.Add("# 系统：Windows $($build.Major).$($build.Minor).$($build.Build)")
$lines.Add("# 格式：事件 <提供程序> <事件号> v<版本> keywords=<十六进制>，然后每个字段一行：<名字> <类型>")
foreach ($provider in $wanted.Keys | Sort-Object) {
    $meta = Get-WinEvent -ListProvider $provider
    foreach ($e in $meta.Events | Where-Object { $wanted[$provider] -contains $_.Id } | Sort-Object Id, Version) {
        $lines.Add('')
        # Keywords 是多个关键字（含最高位的保留位，值是负数）的列表，按位或起来
        $kw = [uint64]0
        foreach ($k in $e.Keywords) { $kw = $kw -bor [uint64]::Parse(([BitConverter]::ToUInt64([BitConverter]::GetBytes([int64]$k.Value), 0)).ToString()) }
        $lines.Add(("event {0} {1} v{2} keywords=0x{3:X16}" -f $provider, $e.Id, $e.Version, $kw))
        if (-not $e.Template) {
            continue
        }

        [xml] $t = $e.Template
        foreach ($d in $t.template.data) {
            $lines.Add(("  {0} {1}" -f $d.name, $d.inType))
        }
    }
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllLines((Join-Path (Resolve-Path (Split-Path $Out)) (Split-Path $Out -Leaf)), $lines, [Text.UTF8Encoding]::new($false))
Write-Host "已写入 $Out：$(@($lines | Where-Object { $_ -like 'event *' }).Count) 个事件版本"
