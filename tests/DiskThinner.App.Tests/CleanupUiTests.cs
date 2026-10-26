using DiskThinner.App.Dialogs;
using DiskThinner.App.Explorer;
using DiskThinner.App.Pages;
using DiskThinner.Cleanup;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using Microsoft.Data.Sqlite;

namespace DiskThinner.App.Tests;

/// <summary>清理计划页、确认框、操作记录页、详情面板的"加入清理计划"。只用内存里的树和临时库，不删除任何东西。</summary>
[Collection(UiTestCollection.Name)]
public sealed class CleanupUiTests : IDisposable
{
    private const long Mb = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-cleanup-ui-" + Guid.NewGuid().ToString("N"));

    public CleanupUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Form Host(Control page) => UiThread.Host(page, width: 1400);

    private static LocationRule Rule(string id, string pattern, Risk risk, Kind kind, params RuleAction[] actions) => new()
    {
        Id = id,
        Mode = MatchMode.Dir,
        Pattern = pattern,
        Kind = kind,
        Risk = risk,
        Tier = Tier.Reviewed,
        Purpose = id + " 的作用",
        Impact = "影响",
        Actions = actions,
    };

    /// <summary>临时文件（安全）、uv 缓存（需重新下载，有官方命令）、会话记录（无法恢复）、分页文件（只能系统处理）。</summary>
    private static (NodeTree Tree, Classification C, CleanupPlan Plan) Sample()
    {
        var tree = TestTrees.Build(
            (@"Users\demo\AppData\Local\Temp\t.tmp", 300 * Mb),
            (@"Users\demo\AppData\Local\uv\cache\a.whl", 800 * Mb),
            (@"Users\demo\.tool\sessions\s.jsonl", 20 * Mb),
            (@"pagefile.sys", 3L << 30));
        var rules = new RuleSet([],
        [
            Rule("t.temp", @"%LOCALAPPDATA%\Temp", Risk.Safe, Kind.Temp, new DeleteAction(DeleteScope.Contents)),
            Rule("t.uv", @"%LOCALAPPDATA%\uv\cache", Risk.Low, Kind.PackageCache, new CommandAction("uv.cache-clean"), new DeleteAction(DeleteScope.Contents)),
            Rule("t.sessions", @"%USERPROFILE%\.tool\sessions", Risk.Medium, Kind.SessionHistory, new DeleteAction(DeleteScope.Children)),
            new LocationRule
            {
                Id = "sys.pagefile", Mode = MatchMode.File, Pattern = @"%SYSTEMDRIVE%\pagefile.sys", Kind = Kind.System, Risk = Risk.Guide, Tier = Tier.Reviewed,
                Purpose = "分页文件", Impact = "影响", Actions = [new GuideAction("在系统设置里调整")],
            },
        ]);
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        var plan = PlanBuilder.Build(tree, c, new PlanOptions
        {
            CurrentProfile = @"C:\Users\demo",
            ResolveCommand = cmd => cmd.Id == "uv.cache-clean" ? @"C:\tools\uv.exe" : null,
        });
        return (tree, c, plan);
    }

    [Fact]
    public void PlanPageShowsGroupsAndUpdatesTheSummary() => UiThread.Run(() =>
    {
        var (_, _, plan) = Sample();
        var page = new CleanupPage();
        using var form = Host(page);
        page.SetPlan(plan, null);

        // 四个分组行 + 四项
        Assert.Equal(8, page.Grid.Rows.Count);
        Assert.Equal(["安全", "需重新下载", "无法恢复", "已拦截"], page.Grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is PlanGroup).Select(r => r.Cells[1].Value as string));
        Assert.StartsWith("已选 3 项", page.SummaryText);
        Assert.True(page.RunButton.Enabled);

        var uv = plan.Items.Single(i => i.Group == PlanGroup.Redownload);
        Assert.Equal("uv cache clean", uv.Method!.Display);
        var blocked = plan.Items.Single(i => i.Group == PlanGroup.Blocked);
        Assert.False(blocked.Selectable);

        // 改成手动清理：不计入执行，也不计入预计释放
        var row = page.Grid.Rows.Cast<DataGridViewRow>().Single(r => ReferenceEquals(r.Tag, uv));
        row.Cells[5].Value = PlanMethod.Manual.Display;
        Assert.Equal(MethodKind.Manual, uv.Method!.Kind);
        Assert.StartsWith("已选 2 项", page.SummaryText);
        Assert.False(uv.WillRun);

        // 全部取消勾选：执行按钮不可用
        foreach (var r in page.Grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is PlanItem { Selectable: true }))
        {
            r.Cells[0].Value = false;
        }

        Assert.False(page.RunButton.Enabled);
    });

    [Fact]
    public void ConfirmDialogListsWhatCannotBeUndoneAndDefaultsToGoingBack() => UiThread.Run(() =>
    {
        var (_, _, plan) = Sample();
        var toRun = plan.ToRun.ToList();
        using var dlg = new ConfirmCleanupDialog(toRun, manualCount: 1);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(-4000, -4000);
        dlg.Show();
        Application.DoEvents();

        var texts = All(dlg).Select(c => c.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("确认清理 3 项", StringComparison.Ordinal));
        Assert.Contains(texts, t => t == "以下内容删除后无法恢复");
        Assert.Contains(texts, t => t.Contains("运行官方清理命令（uv）", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("另有 1 项是手动清理", StringComparison.Ordinal));
        Assert.Equal("返回修改", dlg.ActiveControl?.Text);
        Assert.Equal("返回修改", (dlg.CancelButton as Button)?.Text);
        Assert.Null(dlg.AcceptButton); // 回车不会直接开始清理
    });

    [Fact]
    public void HistoryPageShowsEntriesCardsAndFilters() => UiThread.Run(() =>
    {
        var log = new AuditLog(Path.Combine(_dir, "user.db"));
        var t = new DateTime(2026, 10, 18, 13, 2, 0, DateTimeKind.Utc);
        log.Append(new AuditEntry { AtUtc = t, RunId = "r1", Action = AuditAction.Delete, Target = @"C:\Windows\LiveKernelReports", Bytes = 3L << 30, FreedBytes = 3L << 30, Basis = "规则 sys.crashdump", Result = AuditResult.Succeeded });
        log.Append(new AuditEntry { AtUtc = t.AddMinutes(1), RunId = "r1", Action = AuditAction.Command, Target = "uv cache clean", Bytes = 3L << 30, FreedBytes = 2L << 30, Basis = "规则 dev.uv.cache", Result = AuditResult.Succeeded });
        log.Append(new AuditEntry { AtUtc = t.AddMinutes(2), RunId = "r1", Action = AuditAction.Blocked, Target = @"C:\Users\demo\.codex\auth.json", Basis = "保护名单：Codex CLI 登录凭据", Result = AuditResult.NotRun });

        var page = new HistoryPage();
        using var form = Host(page);
        page.SetLog(log, null);

        Assert.Equal(3, page.List.Items.Count);
        Assert.Equal("5.00 GB", page.FreedCard.ValueText);
        Assert.Equal("拦截", page.List.Items[0].SubItems[1].Text);
        Assert.Equal("成功，释放 2.00 GB", page.List.Items[1].SubItems[5].Text);
    });

    [Fact]
    public void DetailPanelOffersToAddTheRuleLocationToThePlan() => UiThread.Run(() =>
    {
        var (tree, c, _) = Sample();
        var panel = new DetailPanel();
        using var form = Host(panel);
        DetailPanel.PlanRequest? request = null;
        panel.AddToPlanRequested += (_, r) => request = r;
        var cache = tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache");
        panel.ShowNode(tree, tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache\a.whl"), null, c);
        Application.DoEvents();

        var buttons = All(panel).OfType<Button>().ToList();
        var run = buttons.Single(b => b.Text.StartsWith("运行 uv cache clean", StringComparison.Ordinal));
        Assert.Contains(buttons, b => b.Text.StartsWith("加入清理计划", StringComparison.Ordinal));
        run.PerformClick();
        Assert.Equal(new DetailPanel.PlanRequest(cache, "uv.cache-clean"), request); // 作用在规则命中的目录上，不是选中的文件
    });

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c))
            {
                yield return d;
            }
        }
    }
}
