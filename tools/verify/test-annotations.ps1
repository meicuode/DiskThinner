# 把 dotnet test 生成的 trx 里失败的用例写成 GitHub Actions 的 ::error 注解（不登录也能在运行页面上看到），
# 同时打印到日志。测试进程崩溃或卡住（--blame-hang-timeout）时 trx 里只有运行信息，没有那个测试的名字；
# 名字从 blame 的 Sequence_*.xml 里读（Completed="False" 的就是进程结束时还在跑的测试）。
# 这个脚本只用来显示原因，总是以 0 退出：它自己失败会让人以为是它出了问题，盖住真正的测试失败。
# 用法：pwsh -File tools/verify/test-annotations.ps1 -ResultsDir TestResults
param(
    [Parameter(Mandatory)] [string] $ResultsDir,
    [int] $MaxAnnotations = 20
)

$ErrorActionPreference = 'Stop'

# 注解的数据部分要转义 % 和换行；title 这类属性还要转义 : 和 ,（理论测试的名字里有，不转义会被截断）
function Escape([string] $text) { $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }
function EscapeProperty([string] $text) { (Escape $text).Replace(':', '%3A').Replace(',', '%2C') }

function Annotate([string] $title, [string] $message) {
    $script:count++
    if ($script:count -le $MaxAnnotations) {
        Write-Host "::error title=$(EscapeProperty $title)::$(Escape $message)"
    }

    Write-Host "---- $title"
    Write-Host $message
}


$script:count = 0
$files = @(Get-ChildItem $ResultsDir -Recurse -Filter *.trx -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Annotate '没有测试结果' "在 $ResultsDir 里没有找到 trx：测试进程可能在启动时就崩溃了，或者构建没有生成测试程序集。"
}

$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
foreach ($file in $files) {
    try {
        [xml] $xml = Get-Content $file.FullName -Raw -Encoding utf8
        $summary = Select-Xml -Xml $xml -XPath '//t:ResultSummary' -Namespace $ns | Select-Object -First 1
        $counters = Select-Xml -Xml $xml -XPath '//t:ResultSummary/t:Counters' -Namespace $ns | Select-Object -First 1
        $outcome = $summary.Node.outcome
        $c = $counters.Node
        # trx 的文件名只有机器名和时间，用里面的测试程序集名标明是哪一个
        $storage = (Select-Xml -Xml $xml -XPath '//t:TestMethod/@codeBase' -Namespace $ns | Select-Object -First 1).Node.Value
        $assembly = if ($storage) { [IO.Path]::GetFileNameWithoutExtension($storage) } else { $file.BaseName }
        Write-Host "$($file.Name)（$assembly）：$outcome，共 $($c.total) 个，通过 $($c.passed)，失败 $($c.failed)"

        foreach ($r in Select-Xml -Xml $xml -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $ns) {
            $node = $r.Node
            $message = (Select-Xml -Xml $node -XPath './t:Output/t:ErrorInfo/t:Message' -Namespace $ns | Select-Object -First 1).Node.InnerText
            $stack = (Select-Xml -Xml $node -XPath './t:Output/t:ErrorInfo/t:StackTrace' -Namespace $ns | Select-Object -First 1).Node.InnerText
            $frames = @("$stack" -split "`n" | Where-Object { $_ -match ' in .*:line \d+' } | Select-Object -First 3 | ForEach-Object { $_.Trim() })
            Annotate "测试失败：$($node.testName)" ("$message`n" + ($frames -join "`n")).Trim()
        }

        # 测试进程崩溃、卡住被 blame 结束时，原因在 RunInfos 里（[FAIL] 行和 blame 的说明也在这里，和上面重复的不再输出）
        if ($outcome -ne 'Completed' -and $outcome -ne 'Passed') {
            foreach ($info in Select-Xml -Xml $xml -XPath '//t:ResultSummary/t:RunInfos/t:RunInfo' -Namespace $ns) {
                $text = "$((Select-Xml -Xml $info.Node -XPath './t:Text' -Namespace $ns).Node.InnerText)".Trim()
                if ($text -and $text -notmatch '\[FAIL\]$' -and $text -notmatch 'All tests finished running') {
                    Annotate "测试运行：$assembly" $text
                }
            }
        }
    }
    catch {
        Write-Host "读取 $($file.Name) 时出错：$_"
    }
}

# blame 记下的测试顺序：进程被结束时还没跑完的测试（xUnit 并行跑不同的测试类，可能有好几个，卡住的是其中之一）。
# 同一份文件会复制到结果目录的两个地方，按文件名去重。
$sequences = @(Get-ChildItem $ResultsDir -Recurse -Filter 'Sequence_*.xml' -ErrorAction SilentlyContinue | Sort-Object Name -Unique)
$dumps = @(Get-ChildItem $ResultsDir -Recurse -Filter '*.dmp' -ErrorAction SilentlyContinue | Sort-Object Name -Unique | ForEach-Object Name)
foreach ($file in $sequences) {
    try {
        [xml] $xml = Get-Content $file.FullName -Raw -Encoding utf8
        $all = @($xml.TestSequence.Test)
        $running = @($all | Where-Object { $_.Completed -ne 'True' })
        if ($running.Count -eq 0) { continue }
        $assembly = [IO.Path]::GetFileNameWithoutExtension($running[0].Source)
        $names = ($running | ForEach-Object { $_.DisplayName }) -join "`n"
        $message = "进程结束时还在运行（共开始了 $($all.Count) 个测试）：`n$names"
        if ($dumps.Count -gt 0) { $message += "`n转储文件在 test-results 产物里：$($dumps -join '、')" }
        Annotate "没跑完的测试：$assembly" $message
    }
    catch {
        Write-Host "读取 $($file.Name) 时出错：$_"
    }
}

# 测试步骤失败了，上面却什么都没找到：也要留一条注解，不然运行页面上只有"exit code 1"
if ($script:count -eq 0) {
    Annotate '测试步骤失败，但没有找到失败的用例' "trx 里的用例都通过了，也没有没跑完的测试。可能是测试进程退出时出错，或者 dotnet test 本身出错；详细原因见这一步的日志和 test-results 产物。"
}

if ($script:count -gt $MaxAnnotations) {
    Write-Host "::error title=还有更多失败::共 $($script:count) 条，只显示了前 $MaxAnnotations 条注解，其余见日志。"
}

exit 0
