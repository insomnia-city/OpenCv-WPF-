using System.Runtime.InteropServices;

namespace HalconWorkflow.Native;

/// <summary>
/// Scope a native library is loaded into. Per ADR-006/§6.3 the native compute kernel is resolved
/// by the OS loader at process scope and can never be unloaded with an ALC.
/// / native 库的加载作用域。按 ADR-006/§6.3，原生计算内核由 OS 加载器在进程级解析，
///   绝不可能随 ALC 卸载。
/// </summary>
public enum NativeKernelLoadScope
{
    /// <summary>Process-wide, OS loader; not collectible. / 进程级，OS 加载器；不可回收</summary>
    Process,

    /// <summary>Collectible assembly load context; forbidden for native kernels. / 可回收 ALC；原生内核禁用</summary>
    AssemblyLoadContext
}

/// <summary>
/// Result of resolving the native compute kernel. A missing library is reported, never thrown, so
/// the factory can engage the managed fallback explicitly (§6.3). · 解析原生计算内核的结果。
///   缺失只报告不抛出，便于工厂显式启用托管回退(§6.3)。
/// </summary>
public readonly record struct NativeKernelLoad(
    string Library,
    bool IsLoaded,
    nint Handle,
    NativeKernelLoadScope Scope,
    string Message)
{
    /// <summary>True when the library could not be resolved; fallback required. / 库无法解析；需回退</summary>
    public bool IsMissing => !IsLoaded;
}

/// <summary>
/// Process-level native library resolver for the vx_* compute kernel. The cache is a static,
/// process-wide table — deliberately NOT owned by any <c>AssemblyLoadContext</c> — so the kernel
/// is loaded at most once per process and never torn down by an ALC unload (ADR-006). This is the
/// mechanical rule the stage-10 gate asserts. / vx_* 计算内核的进程级 native 库解析器。缓存是静态、
///   进程级的，刻意不归属任何 ALC，因此每进程最多加载一次，且不会被 ALC 卸载牵连(ADR-006)。
///   这是阶段 10 闸门所断言的规则。
/// </summary>
public static class NativeKernelLibrary
{
    /// <summary>Default library base name (x64: <c>vx_voxel.dll</c>). · 默认库名(x64: vx_voxel.dll)</summary>
    public const string DefaultLibraryName = "vx_voxel";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, NativeKernelLoad> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the native kernel once per process. Repeat calls return the cached handle.
    /// / 每进程只解析一次；重复调用返回同一缓存句柄。
    /// </summary>
    public static NativeKernelLoad Load(string libraryName = DefaultLibraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        lock (Gate)
        {
            if (Cache.TryGetValue(libraryName, out var cached)) return cached;

            var result = NativeLibrary.TryLoad(libraryName, out var handle)
                ? new NativeKernelLoad(libraryName, true, handle, NativeKernelLoadScope.Process,
                    $"native '{libraryName}' loaded process-wide (handle 0x{handle:X})")
                : new NativeKernelLoad(libraryName, false, 0, NativeKernelLoadScope.Process,
                    $"native '{libraryName}' not found; managed fallback engaged");
            Cache[libraryName] = result;
            return result;
        }
    }

    /// <summary>Whether a library has already been resolved (loaded or confirmed missing). / 库是否已解析</summary>
    public static bool IsResolved(string libraryName = DefaultLibraryName)
    {
        lock (Gate) return Cache.ContainsKey(libraryName);
    }

    /// <summary>Number of libraries recorded in the process table. / 进程表中已记录的库数量</summary>
    public static int ResolvedCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    /// <summary>Frees a library and drops it from the process table (tests / upgrades). / 释放库并从进程表移除</summary>
    public static void Unload(string libraryName = DefaultLibraryName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(libraryName, out var r) && r.IsLoaded && r.Handle != 0)
                NativeLibrary.Free(r.Handle);
            Cache.Remove(libraryName);
        }
    }
}

/// <summary>
/// Thrown when the native kernel is required but its library is absent. The message is explicit;
/// callers should prefer the managed fallback instead (§6.3).
/// / 当需要原生内核但其库缺失时抛出。消息显式；调用方应优先改用托管回退(§6.3)。
/// </summary>
public sealed class NativeKernelMissingException(string library)
    : Exception($"native compute kernel '{library}' is missing; install the vx_* runtime or use the managed fallback")
{
    /// <summary>Missing library name. / 缺失的库名</summary>
    public string Library { get; } = library;
}
