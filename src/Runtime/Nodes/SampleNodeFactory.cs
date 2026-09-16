using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;

namespace HalconWorkflow.Runtime.Nodes;

/// <summary>
/// Resolves built-in sample contracts for the headless host. 
/// 为无头宿主解析内置示例契约(真实节点由插件提供,System.Text.Json 支持自定义节点)
/// </summary>
public sealed class SampleNodeFactory : INodeFactory
{
    /// <summary>
    /// Creates a node for the contract, or null when unknown. Configuration keys:
    /// 'id' override, 'batch', 'ms' (delay), 'passThreshold' (decision).
    /// 为契约创建节点;未知返回 null。配置键：id/batch/ms/passThreshold
    /// </summary>
    public INode? Create(NodeContract contract, string id) => contract.Namespace switch
    {
        "test.start" => SampleNodes.Start(id),
        "vision.grabber" => SampleNodes.Grabber(id),
        "vision.threshold" => SampleNodes.Threshold(id),
        "app.decision" => SampleNodes.Decision(id),
        "app.result" => SampleNodes.LogResult(id),
        "test.delay" => SampleNodes.Delay(id),
        _ => null
    };
}