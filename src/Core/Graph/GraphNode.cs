using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Graph;

/// <summary>
/// Node instance placed in the graph: wraps an INode plus placement meta. /* 图中的节点实例：包装 INode 并携带布局元数据 */
/// </summary>
public sealed class GraphNode
{
    public GraphNode(INode node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }

    /// <summary>
    /// The wrapped plugin node. /* 被包装的插件节点 */
    /// </summary>
    public INode Node { get; }

    /// <summary>
    /// Id forwarding to the node. /* ID 转发到节点 */
    /// </summary>
    public string Id => Node.Id;

    /// <summary>
    /// Contract forwarding. /* 契约转发 */
    /// </summary>
    public NodeContract Contract => Node.Contract;

    /// <summary>
    /// Canvas position. /* 画布坐标 */
    /// </summary>
    public (double X, double Y) Position { get; set; }

    /// <summary>
    /// Reference to the recipe containing this node's parameters; null = no params. /* 引用包含本节点参数的 Recipe；null 表示无参数 */
    /// </summary>
    public string? RecipeId { get; set; }

    /// <summary>
    /// Whether the node is suspended (missing plugin / contract mismatch). /* 是否挂起（缺插件/契约不匹配） */
    /// </summary>
    public bool IsSuspended { get; set; }

    /// <summary>
    /// Suspension reason when suspended. /* 挂起原因 */
    /// </summary>
    public string? SuspensionReason { get; set; }

    /// <summary>
    /// Input ports. /* 输入端口 */
    /// </summary>
    public IReadOnlyList<IPort> Inputs => Node.Inputs;

    /// <summary>
    /// Output ports. /* 输出端口 */
    /// </summary>
    public IReadOnlyList<IPort> Outputs => Node.Outputs;

    /// <summary>
    /// Resolves a port by name; throws KeyNotFoundException when missing. /* 按名称解析端口;缺失抛 KeyNotFoundException */
    /// </summary>
    public IPort GetInput(string name) => Node.Inputs.FirstOrDefault(p => p.Name == name)
        ?? throw new KeyNotFoundException($"Input port '{name}' not found on node '{Id}'");

    /// <summary>
    /// Resolves an output port by name; throws KeyNotFoundException when missing. /* 按名称解析输出端口;缺失抛 KeyNotFoundException */
    /// </summary>
    public IPort GetOutput(string name) => Node.Outputs.FirstOrDefault(p => p.Name == name)
        ?? throw new KeyNotFoundException($"Output port '{name}' not found on node '{Id}'");
}