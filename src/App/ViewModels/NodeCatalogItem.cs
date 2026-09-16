using HalconWorkflow.Core.Contracts;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// An entry in the node library palette: key, localizable display text and a factory producing a fresh node. 
/// 节点库条目：键、可本地化显示名与生成新节点的工厂
/// </summary>
public sealed record NodeCatalogItem(string Key, string DisplayName, string Contract, Func<string, INode> Factory);