using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Vision.Nodes;

namespace HalconWorkflow.Nodes.Vision;

/// <summary>
/// Node factory: maps vision.* contracts to node instances (§12 contract first).
/// "vision.threshold" is version-gated: v1 belongs to the legacy scaffold nodes
/// (handle-based), v2+ belongs here (§12 migration chain).
/// / 节点工厂：将 vision.* 契约映射到节点实例（§12 契约优先）。
///   vision.threshold 按版本分流：v1 归旧脚手架节点（句柄式），v2+ 归本库（§12 迁移链）。
/// </summary>
public sealed class VisionNodeFactory : INodeFactory
{
    public INode? Create(NodeContract contract, string id) => contract switch
    {
        { Namespace: "vision.grab" } => new GrabNode(id),
        { Namespace: "vision.threshold" } when contract.Version >= 2 => new ThresholdNode(id),
        { Namespace: "vision.measure" } => new MeasureNode(id),
        { Namespace: "vision.hdev" } => new HdevNode(id),
        { Namespace: "vision.tomat" } => new ToMatNode(id),
        { Namespace: "vision.tohobject" } => new ToHObjectNode(id),
        _ => null,
    };
}