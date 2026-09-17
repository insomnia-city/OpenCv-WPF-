using System.Runtime.InteropServices;

namespace HalconWorkflow.MotionDrivers.Native;

/// <summary>
/// Scope a native library is loaded into. Per ADR-006/§6.3 native vendor SDKs are
/// resolved by the OS loader at process scope and can never be unloaded with an ALC.
/// / native 库的加载作用域。按 ADR-006/§6.3，厂商原生 SDK 由 OS 加载器在进程级解析，
///   绝不可能随 ALC 卸载。
/// </summary>
public enum NativeLoadScope
{
    /// <summary>Process-wide, OS loader; not collectible. / 进程级，OS 加载器；不可回收</summary>
    Process,

    /// <summary>Collectible assembly load context; forbidden for native vendor SDKs. / 可回收 ALC；原生厂商 SDK 禁用</summary>
    AssemblyLoadContext
}

/// <summary>
/// Result of resolving a native vendor library. Missing libraries are reported, never
/// thrown, so the motion factory can engage the software fallback explicitly (§6.3).
/// / 解析原生厂商库的结果。缺失只报告不抛出，便于运动工厂显式启用软件回退（§6.3）。
/// </summary>
public readonly record struct NativeLoadResult(
    string Library,
    bool IsLoaded,
    nint Handle,
    NativeLoadScope Scope,
    string Message)
{
    /// <summary>True when the library could not be resolved; fallback required. / 库无法解析；需回退</summary>
    public bool IsMissing => !IsLoaded;
}

/// <summary>
/// Process-level native library resolver for motion vendor SDKs. The cache is a static,
/// process-wide table — deliberately NOT owned by any <c>AssemblyLoadContext</c> — so a
/// library is loaded at most once per process (ADR-006). This is the mechanical rule the
/// stage-7 gate asserts. / 运动厂商 SDK 的进程级 native 库解析器。缓存是静态、进程级的，
///   刻意不归属任何 ALC，因此每个进程最多加载一次（ADR-006）。这是阶段 7 闸门所断言的规则。
/// </summary>
public static class NativeMotionLibrary
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NativeLoadResult> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a native library once per process. Repeat calls return the cached handle.
    /// / 每进程只解析一次；重复调用返回同一缓存句柄。
    /// </summary>
    public static NativeLoadResult Load(string libraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        lock (Gate)
        {
            if (Cache.TryGetValue(libraryName, out var cached)) return cached;

            var result = NativeLibrary.TryLoad(libraryName, out var handle)
                ? new NativeLoadResult(libraryName, true, handle, NativeLoadScope.Process,
                    $"native '{libraryName}' loaded process-wide (handle 0x{handle:X})")
                : new NativeLoadResult(libraryName, false, 0, NativeLoadScope.Process,
                    $"native '{libraryName}' not found; software fallback engaged");
            Cache[libraryName] = result;
            return result;
        }
    }

    /// <summary>Whether a library has already been resolved (loaded or missing). / 库是否已解析(已加载或已确认为缺失)</summary>
    public static bool IsResolved(string libraryName)
    {
        lock (Gate) return Cache.ContainsKey(libraryName);
    }

    /// <summary>Number of libraries recorded in the process table. / 进程表中已记录的库数量</summary>
    public static int ResolvedCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    /// <summary>Frees a library and drops it from the process table (tests / upgrades). / 释放库并从进程表移除(测试/升级)</summary>
    public static void Unload(string libraryName)
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
/// Thrown when a native vendor controller is required but its SDK is absent. The message
/// is explicit and actionable; callers should prefer the phantom fallback instead (§6.3).
/// / 当需要原生厂商控制器但其 SDK 缺失时抛出。消息显式且可操作；调用方应优先改用幻影回退（§6.3）。
/// </summary>
public sealed class NativeLibraryMissingException(string library)
    : Exception($"native motion SDK '{library}' is missing; install the vendor driver or use the phantom fallback")
{
    /// <summary>Missing library name. / 缺失的库名</summary>
    public string Library { get; } = library;
}
