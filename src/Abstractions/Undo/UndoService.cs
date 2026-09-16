using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Abstractions.Undo;

/// <summary>
/// Undo stack with redo, composite commands, and 200-step cap (§9.2). Thread-safe. 
/// 撤销栈(含重做/组合命令/上限200步)(§9.2)，线程安全
/// </summary>
public sealed class UndoService : IUndoService
{
    private readonly object _gate = new();
    private readonly LinkedList<IUndoableCommand> _undo = new();
    private readonly LinkedList<IUndoableCommand> _redo = new();
    private const int MaxDepth = 200;

    /// <inheritdoc />
    public int CanUndoCount { get { lock (_gate) return _undo.Count; } }

    /// <inheritdoc />
    public int CanRedoCount { get { lock (_gate) return _redo.Count; } }

    /// <inheritdoc />
    public async Task PushAndRunAsync(IUndoableCommand cmd, CancellationToken ct)
    {
        await cmd.DoAsync(ct);
        lock (_gate)
        {
            _undo.AddLast(cmd);
            _redo.Clear();
            while (_undo.Count > MaxDepth) _undo.RemoveFirst();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UndoAsync(CancellationToken ct)
    {
        IUndoableCommand? cmd;
        lock (_gate)
        {
            if (_undo.Last is null) return false;
            cmd = _undo.Last.Value;
            _undo.RemoveLast();
            _redo.AddLast(cmd);
        }
        await cmd.UndoAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RedoAsync(CancellationToken ct)
    {
        IUndoableCommand? cmd;
        lock (_gate)
        {
            if (_redo.Last is null) return false;
            cmd = _redo.Last.Value;
            _redo.RemoveLast();
            _undo.AddLast(cmd);
        }
        await cmd.RedoAsync(ct);
        return true;
    }

    /// <summary>Clears all stacks (switch project / load new graph). · 清空全部栈(切换工程/载入新图)</summary>
    public void Clear() { lock (_gate) { _undo.Clear(); _redo.Clear(); } }
}

/// <summary>
/// Composite command: executes a list of commands sequentially. One undo reverts all in reverse order. 
/// 组合命令：顺序执行若干命令；一次撤销按逆序回滚全部
/// </summary>
public sealed class CompositeCommand(string description, IReadOnlyList<IUndoableCommand> steps) : IUndoableCommand
{
    /// <inheritdoc />
    public string Description => description;

    /// <inheritdoc />
    public async Task DoAsync(CancellationToken ct)
    {
        foreach (var s in steps) await s.DoAsync(ct);
    }

    /// <inheritdoc />
    public async Task UndoAsync(CancellationToken ct)
    {
        foreach (var s in steps.Reverse()) await s.UndoAsync(ct);
    }

    /// <inheritdoc />
    public async Task RedoAsync(CancellationToken ct)
    {
        foreach (var s in steps) await s.RedoAsync(ct);
    }
}

/// <summary>Undoable graph edit commands driving the kernel. · 驱动内核的可撤销图编辑命令</summary>
public static class GraphCommands
{
    /// <summary>Adds a node; undo removes it and any links it created. · 添加节点;撤销移除节点及创建的连线</summary>
    public static IUndoableCommand AddNode(GraphModel graph, INode node, double x, double y)
        => new AddNodeImpl(graph, node, x, y);

    /// <summary>Removes a node; undo restores it and its links. · 移除节点;撤销恢复节点及连线</summary>
    public static IUndoableCommand RemoveNode(GraphModel graph, string nodeId)
        => new RemoveNodeImpl(graph, nodeId);

    /// <summary>Connects two ports; undo disconnects. · 连线;撤销断开</summary>
    public static IUndoableCommand Connect(GraphModel graph, IPort from, IPort to)
        => new ConnectImpl(graph, from, to);

    /// <summary>Disconnects a link; undo reconnects. · 断开连线;撤销重新连线</summary>
    public static IUndoableCommand Disconnect(GraphModel graph, GraphLink link)
        => new DisconnectImpl(graph, link);

    private sealed class AddNodeImpl(GraphModel g, INode node, double x, double y) : IUndoableCommand
    {
        public string Description => $"Add node '{node.Id}'";
        public Task DoAsync(CancellationToken ct)
        {
            g.AddNode(node);
            g.Nodes[node.Id].Position = (x, y);
            return Task.CompletedTask;
        }
        public Task UndoAsync(CancellationToken ct) { g.RemoveNode(node.Id); return Task.CompletedTask; }
        public Task RedoAsync(CancellationToken ct) => DoAsync(ct);
    }

private sealed class RemoveNodeImpl(GraphModel g, string nodeId) : IUndoableCommand
{
    private readonly List<(IPort from, IPort to)> _linkSnapshot = new();
    private INode? _node;
    private (double X, double Y)? _position;

    public string Description => $"Remove node '{nodeId}'";

    public Task DoAsync(CancellationToken ct)
    {
        // Snapshot the kernel node + position + every incident link so Undo can restore the state.
        // 快照内核节点/坐标/关联连线，使撤销可以完整恢复
        if (!g.Nodes.TryGetValue(nodeId, out var gn)) return Task.CompletedTask;
        _node = gn.Node;
        _position = gn.Position;
        _linkSnapshot.Clear();
        foreach (var l in g.Links.Where(l => ReferenceEquals(l.From.Owner, gn.Node) || ReferenceEquals(l.To.Owner, gn.Node)))
            _linkSnapshot.Add((l.From, l.To));
        g.RemoveNode(nodeId);
        return Task.CompletedTask;
    }

    public Task UndoAsync(CancellationToken ct)
    {
        // Engine roll-back: re-add the captured INode, restore position and feed data links back. · 引擎回滚：恢复节点/坐标/连线
        if (_node is null) return Task.CompletedTask;
        g.AddNode(_node);
        if (_position is { } p && g.Nodes.TryGetValue(nodeId, out var gn)) gn.Position = p;
        foreach (var (from, to) in _linkSnapshot) g.Connect(from, to);
        return Task.CompletedTask;
    }

    public Task RedoAsync(CancellationToken ct) => DoAsync(ct);
}

    private sealed class ConnectImpl(GraphModel g, IPort from, IPort to) : IUndoableCommand
    {
        private GraphLink? _created;
        public string Description => $"Connect {from.Owner.Id}:{from.Name} → {to.Owner.Id}:{to.Name}";
        public Task DoAsync(CancellationToken ct)
        {
            _created = g.Connect(from, to);
            return Task.CompletedTask;
        }
        public Task UndoAsync(CancellationToken ct)
        {
            if (_created is not null) g.Disconnect(_created);
            _created = null;
            return Task.CompletedTask;
        }
        public Task RedoAsync(CancellationToken ct) => DoAsync(ct);
    }

    private sealed class DisconnectImpl(GraphModel g, GraphLink link) : IUndoableCommand
    {
        public string Description => $"Disconnect link";
        public Task DoAsync(CancellationToken ct) { g.Disconnect(link); return Task.CompletedTask; }
        public Task UndoAsync(CancellationToken ct) { g.Connect(link.From, link.To); return Task.CompletedTask; }
        public Task RedoAsync(CancellationToken ct) => DoAsync(ct);
    }
}