using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Tries factories in order when deserializing graphs (sample nodes, then flow nodes). 
/// 反序列化图时按顺序尝试各节点工厂(示例节点→流程节点)
/// </summary>
internal sealed class CombinedNodeFactory(params INodeFactory[] factories) : INodeFactory
{
    /// <inheritdoc />
    public INode? Create(NodeContract contract, string id)
    {
        foreach (var f in factories)
        {
            var node = f.Create(contract, id);
            if (node is not null) return node;
        }
        return null;
    }
}