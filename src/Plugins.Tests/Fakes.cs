using HalconWorkflow.Abstractions;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Plugins.Tests;

/// <summary>Minimal node returned by the in-test registrar. · 测试内注册器返回的最小节点。</summary>
public sealed class GoodNode(string id) : INode
{
    public string Id { get; } = id;
    public NodeContract Contract { get; } = new("test.good", 1);
    public IReadOnlyList<IPort> Inputs { get; } = [];
    public IReadOnlyList<IPort> Outputs { get; } = [];
    public NodeState State { get; private set; } = NodeState.Idle;
    public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Service contract used to prove round-tripping through the catalog. · 用于验证目录服务往返的契约。</summary>
public interface IProbeService
{
    string Name { get; }
}

/// <summary>Concrete probe service. · 具体探测服务。</summary>
public sealed class ProbeService : IProbeService
{
    public string Name => "probe";
}

/// <summary>
/// Public registrar; the only one in this test assembly, so assembly scanning finds exactly this.
/// · 唯一的公开注册器，确保程序集扫描只找到它。
/// </summary>
public sealed class GoodRegistrar : IServiceRegistrar
{
    public void Register(IPluginServiceRegistrar registrar)
    {
        registrar.RegisterNode(new NodeContract("test.good", 1), id => new GoodNode(id));
        registrar.RegisterService<IProbeService>(new ProbeService());
    }
}

/// <summary>
/// Internal registrar: excluded from assembly scanning (which requires public types) but usable
/// directly through the registrar seam to prove per-registrar isolation.
/// · 内部注册器：程序集扫描（要求公开类型）会跳过，但可经注册器接缝直接用于验证逐个隔离。
/// </summary>
internal sealed class ThrowingRegistrar : IServiceRegistrar
{
    public void Register(IPluginServiceRegistrar registrar)
        => throw new InvalidOperationException("registrar boom");
}
