using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Log entry shown in the log panel. · 日志面板的一条记录
/// </summary>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message)
{
    public string Display => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>
/// Ring-buffered execution/session log behind the log panel. 
/// 日志面板背后的环形缓冲(会话/执行日志)
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly int _capacity;

    public LogViewModel(int capacity = 1000)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Entries in display order; oldest first. · 按时间正序的记录项</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Guards <see cref="Entries"/> against concurrent mutation. · 保护 Entries 免遭并发修改</summary>
    private readonly object _gate = new();

    /// <summary>Adds a formatted entry, evicting the tail when over capacity. · 追加记录并淘汰超限尾部</summary>
    public void Add(string level, string message)
    {
        // Log rows are appended from the scheduler thread while UI/tests read them;
        // without this lock an in-flight Add makes any enumeration throw
        // "Collection was modified". / 日志行由调度线程追加、UI/测试并发读取；
        // 无此锁时，正在进行的 Add 会让任何枚举抛 "Collection was modified"。
        lock (_gate)
        {
            Entries.Add(new LogEntry(DateTimeOffset.Now, level, message));
            while (Entries.Count > _capacity) Entries.RemoveAt(0);
        }
    }

    /// <summary>
    /// Point-in-time copy for off-thread readers. Prefer this over enumerating
    /// <see cref="Entries"/> from any thread other than the UI thread.
    /// / 供非 UI 线程读取的时点副本。非 UI 线程应使用本方法而非直接枚举 Entries。
    /// </summary>
    public LogEntry[] Snapshot()
    {
        lock (_gate) return Entries.ToArray();
    }

    /// <summary>Clears the log. · 清空日志</summary>
    public void Clear()
    {
        lock (_gate) Entries.Clear();
    }
}