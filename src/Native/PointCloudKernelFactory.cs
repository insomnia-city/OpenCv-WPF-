namespace HalconWorkflow.Native;

/// <summary>
/// Outcome of probing for the native point-cloud kernel. When <see cref="IsNative"/> is false the
/// caller fell back to the managed kernel and <see cref="Message"/> says why (§6.3).
/// / 探测原生点云内核的结果。IsNative 为 false 时已回退托管内核，Message 说明原因(§6.3)。
/// </summary>
public readonly record struct PointCloudKernelProbe(
    string Library,
    bool IsNative,
    string Backend,
    string Message);

/// <summary>
/// Selects a point-cloud kernel: the native vx_* ABI when the process-level library resolves and
/// binds, otherwise the managed fallback with an explicit notice (§6.3) — never a silent wrong
/// result, only "slower, still correct". / 选择点云内核：进程级库可解析并绑定则用原生 vx_* ABI，
///   否则回退托管实现并显式提示(§6.3)——绝不静默给出错误结果，只是"更慢但仍正确"。
/// </summary>
public static class PointCloudKernelFactory
{
    /// <summary>Native library name probed by <see cref="Create"/> / <see cref="Probe"/>. · Create/Probe 探测的原生库名</summary>
    public const string LibraryName = NativeKernelLibrary.DefaultLibraryName;

    /// <summary>Probes the native kernel without constructing it. · 探测原生内核但不构造</summary>
    public static PointCloudKernelProbe Probe()
    {
        var load = NativeKernelLibrary.Load(LibraryName);
        if (!load.IsLoaded)
            return new PointCloudKernelProbe(LibraryName, false, ManagedPointCloudKernel.BackendId, load.Message);

        var table = VxNativeFunctionTable.TryBind(load.Handle);
        return table is null
            ? new PointCloudKernelProbe(LibraryName, false, ManagedPointCloudKernel.BackendId,
                $"native '{LibraryName}' loaded but vx_* exports could not be bound; managed fallback engaged")
            : new PointCloudKernelProbe(LibraryName, true, $"native:{table.Version}", load.Message);
    }

    /// <summary>
    /// Creates a kernel: native when present and bound, otherwise the managed software fallback.
    /// / 创建内核：存在且可绑定时用原生，否则回退托管软件实现。
    /// </summary>
    public static IPointCloudKernel Create()
    {
        var load = NativeKernelLibrary.Load(LibraryName);
        if (load.IsLoaded && VxNativeFunctionTable.TryBind(load.Handle) is { } table)
            return new NativeVoxelKernel(table);
        return new ManagedPointCloudKernel();
    }

    /// <summary>Creates the managed fallback explicitly (tests / headless hosts). · 显式创建托管回退(测试/无头宿主)</summary>
    public static IPointCloudKernel CreateManaged() => new ManagedPointCloudKernel();
}
