using System.Runtime.InteropServices;

namespace HalconWorkflow.Native;

/// <summary>
/// Binds the vx_* exports from the process-loaded native handle once and marshals calls over the
/// stable C ABI (§6.3). Uses <see cref="NativeLibrary.GetExport"/> instead of DllImport so the
/// single process-level handle from <see cref="NativeKernelLibrary"/> is respected — the kernel is
/// never re-resolved per call. · 从进程级加载的 native 句柄一次性绑定 vx_* 导出并以稳定 C ABI
///   编组调用(§6.3)。用 NativeLibrary.GetExport 而非 DllImport，以遵循 NativeKernelLibrary 的
///   进程级唯一句柄——内核绝不按调用重复解析。
/// </summary>
public sealed class VxNativeFunctionTable : IVxFunctionTable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoxelDownsampleFn(nint xyz, nuint n, ref NativeVoxelParams p,
        nint output, ref nuint outN, nuint cap, nint cancel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EstimateNormalsFn(nint xyz, nuint n, int k, nint normals, nuint cap, nint cancel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VersionFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LastErrorFn(int handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVoxelParams
    {
        public double LeafX;
        public double LeafY;
        public double LeafZ;
    }

    private readonly VoxelDownsampleFn _downsample;
    private readonly EstimateNormalsFn _normals;
    private readonly VersionFn _version;
    private readonly LastErrorFn _lastError;

    private VxNativeFunctionTable(VoxelDownsampleFn downsample, EstimateNormalsFn normals,
        VersionFn version, LastErrorFn lastError)
    {
        _downsample = downsample;
        _normals = normals;
        _version = version;
        _lastError = lastError;
        Version = Marshal.PtrToStringAnsi(version()) ?? "unknown";
    }

    /// <inheritdoc />
    public string Version { get; }

    /// <summary>
    /// Binds all required exports from a loaded handle. Returns null when the handle is zero or any
    /// export is missing, so callers can fall back without an exception crossing the boundary.
    /// / 从已加载句柄绑定全部必需导出。句柄为零或任一导出缺失时返回 null，便于调用方无异常回退。
    /// </summary>
    public static VxNativeFunctionTable? TryBind(nint handle)
    {
        if (handle == 0) return null;
        try
        {
            var downsample = Bind<VoxelDownsampleFn>(handle, "vx_voxel_downsample");
            var normals = Bind<EstimateNormalsFn>(handle, "vx_estimate_normals");
            var version = Bind<VersionFn>(handle, "vx_version");
            var lastError = Bind<LastErrorFn>(handle, "vx_last_error");
            return new VxNativeFunctionTable(downsample, normals, version, lastError);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MissingMethodException or ArgumentException)
        {
            return null;
        }
    }

    private static T Bind<T>(nint handle, string export) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, export));

    /// <inheritdoc />
    public unsafe VxStatus VoxelDownsample(ReadOnlySpan<float> xyz, int pointCount, VoxelGridSpec spec,
        Span<float> output, out int outCount, ref int cancel)
    {
        var p = new NativeVoxelParams { LeafX = spec.LeafX, LeafY = spec.LeafY, LeafZ = spec.LeafZ };
        var capacity = (nuint)(output.Length / 3);
        var written = capacity;
        int status;
        fixed (float* xyzPtr = xyz)
        fixed (float* outputPtr = output)
        fixed (int* cancelPtr = &cancel)
        {
            status = _downsample((nint)xyzPtr, (nuint)pointCount, ref p,
                (nint)outputPtr, ref written, capacity, (nint)cancelPtr);
        }
        outCount = (int)written;
        return (VxStatus)status;
    }

    /// <inheritdoc />
    public unsafe VxStatus EstimateNormals(ReadOnlySpan<float> xyz, int pointCount, int neighbors,
        Span<float> normals, ref int cancel)
    {
        var capacity = (nuint)(normals.Length / 3);
        int status;
        fixed (float* xyzPtr = xyz)
        fixed (float* normalsPtr = normals)
        fixed (int* cancelPtr = &cancel)
        {
            status = _normals((nint)xyzPtr, (nuint)pointCount, neighbors, (nint)normalsPtr, capacity, (nint)cancelPtr);
        }
        return (VxStatus)status;
    }

    /// <inheritdoc />
    public string LastError(int handle) => Marshal.PtrToStringAnsi(_lastError(handle)) ?? "";

    /// <summary>Delegates are managed; the native library stays loaded process-wide (ADR-006). · 委托为托管对象；原生库进程级常驻</summary>
    public void Dispose()
    {
        // Intentionally does not unload: a process-level native kernel must not be torn down with
        // a short-lived proxy (ADR-006). · 刻意不卸载：进程级原生内核不得随短命代理一起拆除(ADR-006)。
    }
}
