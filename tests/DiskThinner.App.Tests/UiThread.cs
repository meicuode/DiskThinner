using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace DiskThinner.App.Tests;

/// <summary>
/// 会创建窗口的测试（STA 线程上的窗体测试、启动主程序的 UiAutomationTests）都放在这个集合里：一个接一个地跑，也不和别的测试类同时跑。
/// xUnit 默认按测试类并行，同一个测试进程里几个 STA 线程同时创建、销毁窗口时会互相卡住（CI 上测试步骤被 blame 结束过两次）。
/// 主程序只有一个界面线程，不会这样，所以只需要让界面测试串行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiTestCollection
{
    public const string Name = "界面测试";
}

/// <summary>
/// 在单独的 STA 线程上运行界面测试，最多等 <see cref="Timeout"/>（比 CI 的 --blame-hang-timeout 90s 短）。
/// 卡住时这个测试自己失败并写明卡在哪一步，后面的界面测试直接失败，不再等整个测试进程被结束。
/// 需要转储时把 blame 的超时设得比它短，例如 --blame-hang-timeout 30s。
/// </summary>
internal static class UiThread
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static string? s_stuck;

    [ThreadStatic]
    private static Progress? t_progress;

    private sealed record StepInfo(string Text, TimeSpan At);

    private sealed class Progress
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile StepInfo _step = new("开始", TimeSpan.Zero);

        public void Set(string text) => _step = new StepInfo(text, _clock.Elapsed);

        public string Describe()
        {
            var step = _step;
            return $"“{step.Text}”（第 {step.At.TotalSeconds:0.0} 秒开始，之后过了 {(_clock.Elapsed - step.At).TotalSeconds:0.0} 秒）";
        }
    }

    /// <summary>在新的 STA 线程上运行 <paramref name="body"/>，最多等 <see cref="Timeout"/>。</summary>
    public static void Run(Action body, [CallerMemberName] string test = "")
    {
        Assert.True(s_stuck is null, "前面的界面测试卡住了，这个测试不再运行。" + s_stuck);
        ExceptionDispatchInfo? error = null;
        var progress = new Progress();
        var thread = new Thread(() =>
        {
            t_progress = progress;
            try
            {
                Application.EnableVisualStyles();
                body();
                progress.Set("测试内容已结束，线程正在退出");
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
                progress.Set("测试内容抛出了 " + ex.GetType().Name + "，线程正在退出");
            }
        })
        {
            IsBackground = true, // 卡住的线程不挡住测试进程退出
            Name = "界面测试 " + test,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(Timeout))
        {
            s_stuck = $"{test} 在 STA 线程上 {Timeout.TotalSeconds:0} 秒没有结束，最后一步是{progress.Describe()}。";
            Assert.Fail(s_stuck);
        }

        error?.Throw();
    }

    /// <summary>记下测试走到了哪一步（卡住时写进失败信息）。只在 <see cref="Run"/> 的线程上有效。</summary>
    public static void Step(string text) => t_progress?.Set(text);

    /// <summary>在屏幕外的窗体里放一个页面并显示出来。</summary>
    public static Form Host(Control page, int width = 1300, int height = 850)
    {
        Step("创建并显示窗体");
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(width, height), ShowInTaskbar = false };
        page.Dock = DockStyle.Fill;
        form.Controls.Add(page);
        form.Show();
        Application.DoEvents();
        form.Disposed += (_, _) => Step("窗体已销毁");
        Step("窗体已显示，运行测试内容");
        return form;
    }
}
