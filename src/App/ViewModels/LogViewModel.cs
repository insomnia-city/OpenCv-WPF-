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

    /// <summary>Adds a formatted entry, evicting the tail when over capacity. · 追加记录并淘汰超限尾部</summary>
    public void Add(string level, string message)
    {
        Entries.Add(new LogEntry(DateTimeOffset.Now, level, message));
        while (Entries.Count > _capacity) Entries.RemoveAt(0);
    }

    /// <summary>Clears the log. · 清空日志</summary>
    public void Clear() => Entries.Clear();
}