using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Graph;

/// <summary>
/// Directed connection between an output port and an input port. /* 输出端口到输入端口的单向连接 */
/// </summary>
public sealed class GraphLink
{
    public GraphLink(IPort from, IPort to)
    {
        if (from.Direction != PortDirection.Out)
            throw new ArgumentException("'from' port must be an output port.", nameof(from));
        if (to.Direction != PortDirection.In)
            throw new ArgumentException("'to' port must be an input port.", nameof(to));
        From = from;
        To = to;
    }

    /// <summary>
    /// Source output port. /* 源输出端口 */
    /// </summary>
    public IPort From { get; }

    /// <summary>
    /// Target input port. /* 目标输入端口 */
    /// </summary>
    public IPort To { get; }

    public override string ToString() => $"{From} → {To}";
}