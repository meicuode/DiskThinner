using System.Diagnostics;
using System.Globalization;
using DiskThinner.Cleanup.Native;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Storage;

namespace DiskThinner.Cleanup;

public sealed record ExecutorOptions
{
    /// <summary>只允许清理这个目录下面的位置（测试用；产品里为 null）。</summary>
    public string? RequiredRoot { get; init; }

    public AccountInfo? Account { get; init; }

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 有文件被占用时问用户：返回 true 表示同意请这些程序正常关闭（不强制结束）后再试一次。为 null 时直接跳过并记录。
    /// 在执行线程上调用，界面需要自己切回界面线程。
    /// </summary>
    public Func<PlanItem, IReadOnlyList<FileHolder>, bool>? AskCloseApps { get; init; }

    public Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;

    /// <summary>用户在设置里追加的保护目录。执行时立即生效（计划是在添加之前生成的也一样）。</summary>
    public IReadOnlyList<ProtectionEntry> ExtraProtection { get; init; } = [];
}

/// <summary>执行时的进度：第几项、这一项删了多少。</summary>
public readonly record struct ExecProgress(int Index, int Count, string Title, int Files, long Bytes);

/// <summary>一项的执行结果。</summary>
public sealed record ItemResult(PlanItem Item, AuditResult Result, long FreedBytes, string Text, DeleteOutcome? Delete = null, CommandResult? Command = null);

/// <summary>一次清理的结果。</summary>
public sealed record CleanupRun(string RunId, IReadOnlyList<ItemResult> Results, long FreedBytes, long EstimatedBytes, TimeSpan Duration, bool Cancelled);

/// <summary>
/// 清理执行器：逐项复核 → 删除或运行官方命令 → 用执行前后的可用空间之差记实际释放 → 写一条操作记录。
/// 手动清理的项目不执行（重新测量时由界面写记录）。
/// </summary>
public sealed class CleanupExecutor(RuleEngine engine, AuditLog? audit, ExecutorOptions options)
{
    private readonly ProtectionCheck _protection = engine.CreateProtectionCheck(options.ExtraProtection);

    public CleanupRun Execute(IReadOnlyList<PlanItem> items, IProgress<ExecProgress>? progress, CancellationToken token)
    {
        var runId = options.UtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var sw = Stopwatch.StartNew();
        var results = new List<ItemResult>();
        var toRun = items.Where(i => i.WillRun).ToList();
        for (var i = 0; i < toRun.Count; i++)
        {
            var item = toRun[i];
            if (token.IsCancellationRequested)
            {
                results.Add(Finish(item, runId, AuditResult.NotRun, 0, L.T("取消了，没有执行"), null, null, log: false));
                continue;
            }

            progress?.Report(new ExecProgress(i, toRun.Count, item.Title, 0, 0));
            var index = i;
            var itemProgress = progress is null ? null : new SyncProgress<DeleteProgress>(p => progress.Report(new ExecProgress(index, toRun.Count, item.Title, p.Files, p.Bytes)));
            results.Add(RunOne(item, runId, itemProgress, token));
        }

        return new CleanupRun(runId, results, results.Sum(r => r.FreedBytes), toRun.Sum(i => i.EstimatedBytes), sw.Elapsed, token.IsCancellationRequested);
    }

    private ItemResult RunOne(PlanItem item, string runId, IProgress<DeleteProgress>? progress, CancellationToken token)
    {
        var verify = Verifier.Verify(item, engine, _protection, options);
        if (verify.Status == VerifyStatus.Missing)
        {
            return Finish(item, runId, AuditResult.NotRun, 0, L.T("执行时已经不在了"), null, null);
        }

        if (verify.Status == VerifyStatus.Refused)
        {
            item.State = PlanItemState.NotRun;
            item.ResultText = verify.Reason;
            Append(new AuditEntry
            {
                AtUtc = options.UtcNow(),
                RunId = runId,
                Action = AuditAction.Blocked,
                Target = item.Path,
                Title = item.Title,
                Bytes = item.Bytes,
                Basis = "执行前复核：" + verify.Reason,
                Result = AuditResult.NotRun,
            });
            return new ItemResult(item, AuditResult.NotRun, 0, verify.Reason!);
        }

        var method = item.Method!;
        var volume = Path.GetPathRoot(item.Path)!;
        var before = FreeBytes(volume);
        if (method.Kind == MethodKind.Command)
        {
            var cmd = CommandRunner.Run(method.Command!, method.CommandPath!, options.CommandTimeout, token);
            var freed = Freed(volume, before);
            var result = cmd.Succeeded ? AuditResult.Succeeded : cmd.Cancelled ? AuditResult.NotRun : AuditResult.Failed;
            var text = cmd.Succeeded ? L.F("成功，释放 {0}", ByteSize.Format(freed))
                : cmd.TimedOut ? L.F("超过 {0:0} 分钟没有结束，已停止", options.CommandTimeout.TotalMinutes)
                : cmd.Cancelled ? L.T("取消了")
                : cmd.Error ?? L.F("退出码 {0}", cmd.ExitCode);
            return Finish(item, runId, result, freed, text, null, cmd);
        }

        var d = method.Delete!;
        var cutoff = (method.MinAgeDays ?? d.OlderThanDays) is { } days ? options.UtcNow().AddDays(-days) : (DateTime?)null;
        var outcome = Delete(item, d, verify, cutoff, progress, token);
        if (outcome.LockedCount > 0 && !outcome.Cancelled && options.AskCloseApps is { } ask)
        {
            var holders = RestartManager.WhoLocks(outcome.Locked);
            if (holders.Count > 0 && ask(item, holders))
            {
                RestartManager.RequestClose(outcome.Locked);
                var again = Delete(item, d, verify, cutoff, progress, token);
                outcome = Merge(outcome, again);
            }
        }

        var freedBytes = Freed(volume, before);
        var status = outcome.Cancelled ? (outcome.FilesDeleted > 0 ? AuditResult.Partial : AuditResult.NotRun)
            : outcome.ErrorCount == 0 && outcome.LockedCount == 0 ? AuditResult.Succeeded
            : outcome.FilesDeleted > 0 || outcome.DirsDeleted > 0 ? AuditResult.Partial
            : AuditResult.Failed;
        var r = Finish(item, runId, status, freedBytes, Describe(outcome, freedBytes), outcome, null);

        if (outcome.ProtectedCount > 0)
        {
            var (path, reason) = outcome.Protected[0];
            Append(new AuditEntry
            {
                AtUtc = options.UtcNow(),
                RunId = runId,
                Action = AuditAction.Blocked,
                Target = path,
                Title = item.Title,
                Basis = "保护名单：" + reason,
                Result = AuditResult.NotRun,
                Files = outcome.ProtectedCount,
                Detail = outcome.ProtectedCount > 1 ? $"这一项里共有 {outcome.ProtectedCount} 处受保护，都跳过了" : null,
            });
        }

        return r;
    }

    private static DeleteOutcome Delete(PlanItem item, DeleteAction d, VerifyResult verify, DateTime? cutoff, IProgress<DeleteProgress>? progress, CancellationToken token)
    {
        var deleter = new Deleter(verify.Scope!, verify.AllowSoft, cutoff, progress, token);
        if (!item.IsDirectory)
        {
            return deleter.DeleteFile(item.Path);
        }

        return deleter.DeleteDirectory(item.Path, removeSelf: d.Scope == DeleteScope.Self, keepLatest: d.KeepLatest);
    }

    private static DeleteOutcome Merge(DeleteOutcome first, DeleteOutcome second)
    {
        // 第二遍只处理第一遍剩下的：删掉的累加，被占用、出错的以第二遍为准
        second.FilesDeleted += first.FilesDeleted;
        second.BytesDeleted += first.BytesDeleted;
        second.SharedBytes += first.SharedBytes;
        second.DirsDeleted += first.DirsDeleted;
        second.LinksRemoved += first.LinksRemoved;
        return second;
    }

    private ItemResult Finish(PlanItem item, string runId, AuditResult result, long freed, string text, DeleteOutcome? outcome, CommandResult? cmd, bool log = true)
    {
        item.State = result switch
        {
            AuditResult.Succeeded => PlanItemState.Done,
            AuditResult.Partial => PlanItemState.Partial,
            AuditResult.Failed => PlanItemState.Failed,
            _ => PlanItemState.NotRun,
        };
        item.ResultText = text;
        item.FreedBytes = freed;
        if (log)
        {
            var method = item.Method!;
            var detail = cmd is null ? outcome is null ? null : Detail(outcome)
                : string.IsNullOrEmpty(cmd.Output) ? cmd.Error : (cmd.Error is null ? "" : cmd.Error + "\n") + cmd.Output;
            Append(new AuditEntry
            {
                AtUtc = options.UtcNow(),
                RunId = runId,
                Action = method.Kind == MethodKind.Command ? AuditAction.Command : AuditAction.Delete,
                Target = method.Kind == MethodKind.Command ? $"{method.Command!.Display}（{method.CommandPath}）" : item.Path,
                Title = item.Title,
                Bytes = item.Bytes,
                FreedBytes = result == AuditResult.NotRun ? null : freed,
                Basis = $"规则 {item.Rule?.Id}" + (item.Group == PlanGroup.Irreversible ? " · 用户确认了明细" : ""),
                Result = result,
                Detail = detail,
                Files = outcome?.FilesDeleted ?? 0,
                Errors = (outcome?.ErrorCount ?? 0) + (outcome?.LockedCount ?? 0),
            });
        }

        return new ItemResult(item, result, freed, text, outcome, cmd);
    }

    private void Append(AuditEntry entry) => audit?.Append(entry);

    private static string Describe(DeleteOutcome o, long freed)
    {
        var parts = new List<string> { L.F("释放 {0}", ByteSize.Format(freed)) };
        if (o.LockedCount > 0)
        {
            parts.Add(L.F("{0} 个文件被占用已跳过", o.LockedCount));
        }

        if (o.ErrorCount > 0)
        {
            parts.Add(L.F("{0} 个出错", o.ErrorCount));
        }

        if (o.ProtectedCount > 0)
        {
            parts.Add(L.F("{0} 处受保护已跳过", o.ProtectedCount));
        }

        if (o.Cancelled)
        {
            parts.Add(L.T("中途取消"));
        }

        return string.Join(L.T("，"), parts);
    }

    /// <summary>写进操作记录的明细：数量、跳过的原因、前几条被占用和出错的路径。</summary>
    internal static string Detail(DeleteOutcome o)
    {
        var lines = new List<string>
        {
            L.F("删除 {0} 个文件（{1}）、{2} 个目录、{3} 个链接", o.FilesDeleted, ByteSize.Format(o.BytesDeleted), o.DirsDeleted, o.LinksRemoved),
        };
        if (o.SharedBytes > 0)
        {
            lines.Add(L.F("其中 {0} 还有别的硬链接名，没有释放", ByteSize.Format(o.SharedBytes)));
        }

        if (o.Kept > 0)
        {
            lines.Add(L.F("按条件保留 {0} 项", o.Kept));
        }

        if (o.OtherReparseSkipped > 0)
        {
            lines.Add(L.F("跳过 {0} 个占位符或特殊链接", o.OtherReparseSkipped));
        }

        if (o.LockedCount > 0)
        {
            var holders = RestartManager.WhoLocks(o.Locked);
            var who = holders.Count == 0 ? "" : L.F("（{0}）", string.Join(L.T("、"), holders.Select(h => h.Name).Distinct().Take(5)));
            lines.Add(L.F("{0} 个文件被占用已跳过{1}：{2}", o.LockedCount, who, string.Join(L.T("；"), o.Locked.Take(5))));
        }

        if (o.ErrorCount > 0)
        {
            lines.Add(L.F("{0} 个出错：{1}", o.ErrorCount, string.Join(L.T("；"), o.Errors.Take(5))));
        }

        if (o.Cancelled)
        {
            lines.Add(L.T("中途取消"));
        }

        return string.Join("\n", lines);
    }

    public static long FreeBytes(string volume) =>
        Win32.GetDiskFreeSpaceEx(volume, out _, out _, out var free) ? free : 0;

    private static long Freed(string volume, long before) => Math.Max(0, FreeBytes(volume) - before);

    /// <summary>直接在调用线程上报告的 IProgress（Progress&lt;T&gt; 会切到捕获的同步上下文，这里不需要）。</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
