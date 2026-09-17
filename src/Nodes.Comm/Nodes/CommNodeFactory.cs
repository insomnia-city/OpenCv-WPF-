using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Comm.Nodes;

namespace HalconWorkflow.Nodes.Comm;

/// <summary>
/// Creates comm nodes from contracts: comm.read:1, comm.write:1, comm.wait:1.
/// / 根据契约创建通讯节点
/// </summary>
public sealed class CommNodeFactory : INodeFactory
{
    public INode? Create(NodeContract contract, string id) => contract switch
    {
        { Namespace: "comm.read", Version: 1 } => new CommReadNode(id),
        { Namespace: "comm.write", Version: 1 } => new CommWriteNode(id),
        { Namespace: "comm.wait", Version: 1 } => new CommWaitNode(id),
        _ => null,
    };
}