using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Serializes every test that touches the process-wide <see cref="Adapters.VisionProviderRegistry"/>
/// (registration is static state). Factory + registry tests share this collection, so they
/// never race each other across xUnit's parallel classes.
/// / 串行化所有触碰进程级 VisionProviderRegistry 的测试（注册表为静态状态）。工厂与注册表测试
///   共用本集合，避免跨 xUnit 并行类互相竞争。
/// </summary>
[CollectionDefinition("adapter-registry", DisableParallelization = true)]
public sealed class AdapterRegistryCollection { }