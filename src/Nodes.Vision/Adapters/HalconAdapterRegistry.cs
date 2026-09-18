namespace HalconWorkflow.Nodes.Vision.Adapters;

/// <summary>
/// Process-wide point for the deployment site to plug a real-SDK adapter into
/// <see cref="VisionEngineFactory"/>. The factory probes the machine for a licensed
/// Halcon runtime and, when one is present, asks this registry for an adapter. No
/// adapter registered  ⇒ the resolver falls back to the deterministic phantom engine
/// (§6.3) instead of failing. Registration is the deployment team's responsibility and
/// is never done implicitly by this library.
/// / 进程级部署接入点：部署现场把真实 SDK 适配器注册给 VisionEngineFactory。工厂先探测本机
///   是否有授权 Halcon 运行时，命中时向本注册表索取适配器;未注册 ⇒ 解析器回退确定性幻影引擎
///   （§6.3）而不是失败。注册由部署团队负责，本库绝不隐式注册。
/// </summary>
public static class HalconAdapterRegistry
{
    private static readonly object Lock = new();
    private static Func<IHalconAdapter>? _factory;

    /// <summary>Whether an adapter factory is currently registered. / 当前是否已注册适配器工厂</summary>
    public static bool IsRegistered
    {
        get { lock (Lock) return _factory is not null; }
    }

    /// <summary>
    /// Registers the adapter factory. Latest registration wins; passing null throws.
    /// The factory is invoked when the resolver first needs a real engine (pool lease
    /// creation), so construction cost is paid once and the failed-construction case is
    /// reported back to the resolver rather than crashing the executor.
    /// / 注册适配器工厂。后注册覆盖先注册;传 null 抛异常。工厂在解析器首次需要真实引擎时
    ///   （池借出）被调用——构造成本只付一次，且构造失败会回传给解析器而非击穿执行线程。
    /// </summary>
    public static void Register(Func<IHalconAdapter> adapterFactory)
    {
        ArgumentNullException.ThrowIfNull(adapterFactory);
        lock (Lock) _factory = adapterFactory;
    }

    /// <summary>
    /// Clears any registered factory (mainly test fixture teardown). / 清空已注册工厂（主要供测试夹具收尾）
    /// </summary>
    public static void Unregister()
    {
        lock (Lock) _factory = null;
    }

    /// <summary>
    /// Creates an adapter through the registered factory, if any. Returns false when no
    /// factory or when construction failed (<paramref name="detail"/> carries the message).
    /// / 经已注册工厂创建适配器。无工厂返回 false;构造失败返回 false 且 detail 携带异常消息。
    /// </summary>
    internal static bool TryCreate(out IHalconAdapter? adapter, out string? detail)
    {
        Func<IHalconAdapter>? factory;
        lock (Lock) factory = _factory;
        if (factory is null)
        {
            adapter = null;
            detail = null;
            return false;
        }
        try
        {
            adapter = factory();
            detail = null;
            return true;
        }
        catch (Exception ex)
        {
            adapter = null;
            detail = ex.Message;
            return false;
        }
    }
}