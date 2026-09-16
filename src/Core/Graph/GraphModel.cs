using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Graph;

/// <summary>
/// Static topology + trigger config; owned by the engine, mutated only through commands. /* 静态拓扑+触发配置;由引擎持有，仅允许通过命令变更 */
/// </summary>
public sealed class GraphModel
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<GraphLink> _links = [];
    private readonly List<ValidationIssue> _issues = [];

    /// <summary>
    /// Node lookup by instance id. /* 按实例ID查询节点 */
    /// </summary>
    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;

    /// <summary>
    /// All links. /* 全部连线 */
    /// </summary>
    public IReadOnlySet<GraphLink> Links => _links;

    /// <summary>
    /// Latest static validation issues; empty when valid. /* 最近一次静态校验问题;有效时为空 */
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>
    /// Trigger configuration. /* 触发配置 */
    /// </summary>
    public TriggerConfig Trigger { get; set; } = new();

    /// <summary>
    /// Precomputed topological order of exec chains (exec port adjacency). /* 拓扑序(按控制流端口邻接) */
    /// </summary>
    public IReadOnlyList<GraphNode> TopologicalOrder => _topoOrder;
    private List<GraphNode> _topoOrder = [];

    /// <summary>
    /// Adds a node. /* 添加节点 */
    /// </summary>
    public bool AddNode(INode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.ContainsKey(node.Id)) return false;
        _nodes[node.Id] = new GraphNode(node);
        return true;
    }

    /// <summary>
    /// Removes a node and all its links. /* 移除节点及其全部连线 */
    /// </summary>
    public bool RemoveNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var gn)) return false;
        _links.RemoveWhere(l => ReferenceEquals(l.From.Owner, gn.Node) || ReferenceEquals(l.To.Owner, gn.Node));
        _nodes.Remove(id);
        RefreshConnectivity();
        return true;
    }

    /// <summary>
    /// Adds a validated link. Fails on direction/type/self/circular issues. /* 添加经校验的连线;方向/类型/自环/成环失败 */
    /// </summary>
    public GraphLink? Connect(IPort from, IPort to)
    {
        var issue = ValidateLink(from, to);
        if (issue is not null) return null;

        // Data inputs accept only a single source. /* 数据输入端口只允许单一来源 */
        if (to.Kind == PortKind.Data && _links.Any(l => ReferenceEquals(l.To, to)))
            return null;

        var link = new GraphLink(from, to);
        _links.Add(link);
        from.IsConnected = true;
        to.IsConnected = true;
        return link;
    }

    /// <summary>
    /// Removes a link and clears connectivity flags. /* 移除连线并清除连接标记 */
    /// </summary>
    public bool Disconnect(GraphLink link)
    {
        if (!_links.Remove(link)) return false;
        RefreshConnectivity();
        return true;
    }

    /// <summary>
    /// Raw link validation without mutating the graph. /* 不修改图的原始连线校验 */
    /// </summary>
    public ValidationIssue? ValidateLink(IPort from, IPort to)
    {
        if (from.Direction != PortDirection.Out)
            return new ValidationIssue(ValidationIssueKind.InvalidLink, $"{from} is not an output port.");
        if (to.Direction != PortDirection.In)
            return new ValidationIssue(ValidationIssueKind.InvalidLink, $"{to} is not an input port.");
        if (ReferenceEquals(from.Owner, to.Owner))
            return new ValidationIssue(ValidationIssueKind.InvalidLink, "Self-connection is not allowed.");
        if (from.Kind == PortKind.Data && to.Kind == PortKind.Data)
        {
            if (from.Type is null || to.Type is null)
                return new ValidationIssue(ValidationIssueKind.TypeMismatch, "Data port type descriptor missing.");
            if (!from.Type.IsAssignableTo(to.Type))
                return new ValidationIssue(ValidationIssueKind.TypeMismatch,
                    $"Type mismatch: {from.Type.AssignabilityPath} → {to.Type.AssignabilityPath}.");
        }
        else if (from.Kind != to.Kind)
        {
            return new ValidationIssue(ValidationIssueKind.InvalidLink,
                "Exec port cannot connect to Data port (or vice versa).");
        }

        if (CreatesCycle(from, to))
            return new ValidationIssue(ValidationIssueKind.Cycle, "Connection would create a cycle.");

        return null;
    }

    /// <summary>
    /// Runs full static validation and re-computes topology. Returns issue list. /* 全量静态校验并重算拓扑;返回问题列表 */
    /// </summary>
    public IReadOnlyList<ValidationIssue> Validate()
    {
        _issues.Clear();

        // 1. Structural errors: duplicate ids / empty contracts / suspended nodes. /* 结构错误：重复ID/空契约/挂起节点 */
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gn in _nodes.Values)
        {
            if (!seen.Add(gn.Id))
                _issues.Add(new ValidationIssue(ValidationIssueKind.DuplicateId, $"Duplicate node id '{gn.Id}'."));
            if (string.IsNullOrEmpty(gn.Contract.Namespace))
                _issues.Add(new ValidationIssue(ValidationIssueKind.MissingContract, $"Node '{gn.Id}' has empty contract."));
            if (gn.IsSuspended)
                _issues.Add(new ValidationIssue(ValidationIssueKind.MissingContract, $"Node '{gn.Id}' is suspended: {gn.SuspensionReason}"));
        }

        // 2. Re-validate every link. /* 重新校验每条连线 */
        foreach (var l in _links)
        {
            var v = ValidateLink(l.From, l.To);
            if (v.HasValue)
                _issues.Add(v.Value with { Message = $"Link {l}: {v.Value.Message}" });
        }

        // 2b. Data inputs accept at most one incoming link. · 数据输入端口至多一条入线
        foreach (var group in _links.GroupBy(l => l.To).Where(g => g.Count() > 1))
            _issues.Add(new ValidationIssue(ValidationIssueKind.InvalidLink,
                $"Data input '{group.Key}' has {group.Count()} sources; only one is allowed."));

        // 3. Dangling data inputs: connected execution must have all inputs fed. /* 悬空输入：已连接的执行链数据输入必须被喂给 */
        foreach (var gn in _nodes.Values)
        {
            var hasExecIn = gn.Inputs.Any(p => p.Kind == PortKind.Exec);
            if (!hasExecIn) continue;
            foreach (var p in gn.Inputs.Where(p => p.Kind == PortKind.Data && p.IsRequired))
            {
                if (!p.IsConnected)
                    _issues.Add(new ValidationIssue(ValidationIssueKind.DanglingPort,
                        $"Node '{gn.Id}' requires data input '{p.Name}' but it is disconnected."));
            }
        }

        // 4. Topological sort; cycle => Broken. /* 拓扑排序;成环即 Broken */
        var cycleNodes = new List<string>();
        _topoOrder = TopologySort.Sort(_nodes.Values, _links, cycleNodes);
        if (cycleNodes.Count > 0)
            _issues.Add(new ValidationIssue(ValidationIssueKind.Cycle, $"Graph has a cycle involving: {string.Join(", ", cycleNodes)}"));

        return _issues;
    }

    private void RefreshConnectivity()
    {
        var all = _nodes.Values.SelectMany(g => g.Node.Inputs).Concat(_nodes.Values.SelectMany(g => g.Node.Outputs));
        foreach (var p in all) p.IsConnected = false;
        foreach (var l in _links)
        {
            l.From.IsConnected = true;
            l.To.IsConnected = true;
        }
    }

    private bool CreatesCycle(IPort from, IPort to)
    {
        // Only exec links define ordering; build adjacency including the proposed edge. /* 仅控制流连线定义顺序;构建含拟加边的邻接表 */
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in _nodes.Values) adj[n.Id] = [];

        foreach (var l in _links)
            if (l.From.Kind == PortKind.Exec)
                adj[l.From.Owner.Id].Add(l.To.Owner.Id);

        if (from.Kind == PortKind.Exec && to.Kind == PortKind.Exec)
            adj[from.Owner.Id].Add(to.Owner.Id);

        return GraphAlgorithms.HasCycle(adj);
    }
}

/// <summary>
/// Result of a validation check. /* 校验结果 */
/// </summary>
public readonly record struct ValidationIssue(ValidationIssueKind Kind, string Message);

/// <summary>
/// Kinds of validation problems. /* 校验问题类型 */
/// </summary>
public enum ValidationIssueKind
{
    DuplicateId,
    MissingContract,
    InvalidLink,
    TypeMismatch,
    Cycle,
    DanglingPort
}

/// <summary>
/// Graph trigger configuration. /* 图的触发配置 */
/// </summary>
public sealed class TriggerConfig
{
    public TriggerSource Source { get; set; } = TriggerSource.Manual;
    public string? Tag { get; set; }
    public int DebounceMs { get; set; } = 10;
    public int QueueLimit { get; set; } = 1;
}