using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Serialization;

/// <summary>
/// Creates node instances from contracts at load time (implemented by plugin layers). 
/// 装载时按契约创建节点实例（由插件层实现）
/// </summary>
public interface INodeFactory
{
    /// <summary>
    /// Creates a node instance for the given contract. Returns null when the contract is unknown. 
    /// 为契约创建节点实例；契约未知时返回 null
    /// </summary>
    INode? Create(NodeContract contract, string id);
}

/// <summary>
/// Error thrown when a graph references an unknown contract. /* 图引用了未知契约时抛出 */
/// </summary>
public sealed class ContractNotFoundException : Exception
{
    public ContractNotFoundException(NodeContract contract)
        : base($"No plugin provides contract '{contract}'. The node will be suspended.") { }
}