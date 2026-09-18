using System.Windows;
using System.Windows.Threading;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Last-resort crash safety for the field shell: logs unhandled UI/domain/task exceptions
/// so a shop-floor session degrades to a visible log line instead of vanishing. Injectable
/// sink keeps the message formatting unit-testable; install is called once from startup.
/// / 现场外壳的最后防线：把未处理的 UI/域/任务异常落日志，让现场会话降级为可见日志而非直接消失。
///   日志汇可注入，使消息格式化可单测;Install 在启动时调用一次。
/// </summary>
public static class CrashGuard
{
    /// <summary>
    /// Formats an exception for the session log: source, type, message and stack trace.
    /// / 将异常格式化为会话日志：来源、类型、消息与堆栈。
    /// </summary>
    public static string Describe(Exception ex, string source)
        => $"[{source}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";

    /// <summary>
    /// Hooks the WPF dispatcher, the app domain and the task scheduler. UI exceptions are
    /// logged and swallowed so the shell keeps running; the other two are logged best-effort.
    /// / 挂接 WPF 调度器、应用域与任务调度器。UI 异常记录后吞掉，使外壳继续运行;其余两类尽力记录。
    /// </summary>
    public static void Install(Application app, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(log);

        app.DispatcherUnhandledException += (_, e) =>
        {
            log(Describe(e.Exception, "ui"));
            e.Handled = true; // keep the field app alive; the fault is on record · 现场应用保持存活，故障已留痕
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log(Describe(ex, "domain"));
            else log($"[domain] non-exception fatal: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log(Describe(e.Exception, "task"));
            e.SetObserved();
        };
    }
}