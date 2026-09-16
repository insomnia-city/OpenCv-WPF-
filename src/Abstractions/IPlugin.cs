using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Abstractions;

/// <summary>
/// Plugin registration callback: plugins pull their services into the host container (§10). 
/// 插件注册回调：插件把自身服务汇入宿主容器(§10)
/// </summary>
public interface IPluginServiceRegistrar
{
    /// <summary>
    /// Registers a node contract → node factory; used at graph load time. 
    /// 注册节点契约 → 节点工厂;供装载图时使用
    /// </summary>
    void RegisterNode(NodeContract contract, Func<string, INode?> factory);

    /// <summary>
    /// Registers a singleton/transient service into the host container. 
    /// 向宿主容器注册单例/瞬态服务
    /// </summary>
    void RegisterService<TService>(TService instance) where TService : class;
}

/// <summary>
/// Entry point of every plugin assembly; the host discovers and invokes this on load. 
/// 每个插件程序集的入口;宿主装载时发现并调用
/// </summary>
public interface IServiceRegistrar
{
    /// <summary>
    /// Called once per plugin load. · 每个插件装载时调用一次
    /// </summary>
    void Register(IPluginServiceRegistrar registrar);
}

/// <summary>
/// Node metadata for UI/parameter panel (resource-key driven names/descriptions). 
/// 节点的 UI/参数面板元数据(资源键驱动名称/描述)
/// </summary>
public static class NodeMetadata
{
    /// <summary>
    /// Localized display name of the node. · 节点的本地化显示名
    /// </summary>
    public const string DisplayNameKey = "displayName";

    /// <summary>
    /// Localized description of the node. · 节点的本地化描述
    /// </summary>
    public const string DescriptionKey = "description";
}