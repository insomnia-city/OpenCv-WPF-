using HalconWorkflow.Abstractions;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;

namespace SamplePlugin;

/// <summary>
/// A trivial externally-supplied node, registered under the <c>sample.plugin:1</c> contract.
/// / 一个简单的外部节点，注册于 <c>sample.plugin:1</c> 契约下。
/// </summary>
public sealed class SampleNode(string id) : INode
{
    public string Id { get; } = id;
    public NodeContract Contract { get; } = new("sample.plugin", 1);
    public IReadOnlyList<IPort> Inputs { get; } = [];
    public IReadOnlyList<IPort> Outputs { get; } = [];
    public NodeState State { get; private set; } = NodeState.Idle;
    public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Marker service proving a plugin can push services into the host container. · 证明插件可向宿主容器注入服务。</summary>
public sealed record SampleMarker(string Name, int Value);

/// <summary>
/// External plugin entry point (ADR-007): the host discovers this public registrar, instantiates
/// it inside the plugin's load context and invokes <see cref="Register"/>.
/// / 外部插件入口（ADR-007）：宿主发现该公开注册器，在插件装载上下文中实例化并调用 <see cref="Register"/>。
/// </summary>
public sealed class SampleRegistrar : IServiceRegistrar
{
    public void Register(IPluginServiceRegistrar registrar)
    {
        registrar.RegisterNode(new NodeContract("sample.plugin", 1), id => new SampleNode(id));
        registrar.RegisterService(new SampleMarker("sample-plugin", 42));
    }
}
