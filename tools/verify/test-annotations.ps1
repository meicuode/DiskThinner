# 把 dotnet test 生成的 trx 里失败的用例写成 GitHub Actions 的 ::error 注解（不登录也能在运行页面上看到），
# 同时打印到日志。测试进程崩溃或卡住（--blame-hang-timeout）时 trx 里只有运行信息，也一并输出。
# 用法：pwsh -File tools/verify/test-annotations.ps1 -ResultsDir TestResults
param(
    [Parameter(Mandatory)] [string] $ResultsDir,
    [int] $MaxAnnotations = 20
)

$ErrorActionPreference = 'Stop'

# 注解的数据部分要转义 % 和换行
function Escape([string] $text) { $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

function Annotate([string] $title, [string] $message) {
    $script:count++
    if ($script:count -le $MaxAnnotations) {
        Write-Host "::error title=$(Escape $title)::$(Escape $message)"
    }

    Write-Host "---- $title"
    Write-Host $message
}

$script:count = 0
$files = @(Get-ChildItem $ResultsDir -Recurse -Filter *.trx -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Annotate '没有测试结果' "在 $ResultsDir 里没有找到 trx：测试进程可能在启动时就崩溃了，或者构建没有生成测试程序集。"
    exit 0
}

$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
foreach ($file in $files) {
    [xml] $xml = Get-Content $file.FullName -Raw -Encoding utf8
    $summary = Select-Xml -Xml $xml -XPath '//t:ResultSummary' -Namespace $ns | Select-Object -First 1
    $counters = Select-Xml -Xml $xml -XPath '//t:ResultSummary/t:Counters' -Namespace $ns | Select-Object -First 1
    $outcome = $summary.Node.outcome
    $c = $counters.Node
    Write-Host "$($file.Name)：$outcome，共 $($c.total) 个，通过 $($c.passed)，失败 $($c.failed)"

    foreach ($r in Select-Xml -Xml $xml -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $ns) {
        $node = $r.Node
        $message = (Select-Xml -Xml $node -XPath './t:Output/t:ErrorInfo/t:Message' -Namespace $ns | Select-Object -First 1).Node.InnerText
        $stack = (Select-Xml -Xml $node -XPath './t:Output/t:ErrorInfo/t:StackTrace' -Namespace $ns | Select-Object -First 1).Node.InnerText
        $frames = @($stack -split "`n" | Where-Object { $_ -match ' in .*:line \d+' } | Select-Object -First 3 | ForEach-Object { $_.Trim() })
        Annotate "测试失败：$($node.testName)" ("$message`n" + ($frames -join "`n")).Trim()
    }

    # 测试进程崩溃、卡住被 blame 结束时，原因在 RunInfos 里
    if ($outcome -ne 'Completed' -and $outcome -ne 'Passed') {
        foreach ($info in Select-Xml -Xml $xml -XPath '//t:ResultSummary/t:RunInfos/t:RunInfo' -Namespace $ns) {
            $text = (Select-Xml -Xml $info.Node -XPath './t:Text' -Namespace $ns).Node.InnerText
            if ($text) { Annotate "测试运行：$($file.BaseName)" $text.Trim() }
        }
    }
}

if ($script:count -gt $MaxAnnotations) {
    Write-Host "::error title=还有更多失败::共 $($script:count) 条，只显示了前 $MaxAnnotations 条注解，其余见日志。"
}
