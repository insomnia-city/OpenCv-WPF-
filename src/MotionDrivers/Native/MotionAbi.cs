using System.Runtime.InteropServices;

namespace HalconWorkflow.MotionDrivers.Native;

/// <summary>
/// Managed seam over a vendor motion SDK C ABI. Kept tiny so a controller can be driven
/// by the real <see cref="DllImportMotionApi"/> in production and a fake in tests.
/// / 厂商运动 SDK C ABI 的托管接缝。保持精简：生产用真实 P/Invoke，测试注入假实现。
/// </summary>
public interface INativeMotionApi
{
    int Open(int cardNo);
    void Close(int cardNo);
    void Home(int cardNo, int axis);
    void MoveAbsolute(int cardNo, int axis, double pos, double vel, double acc);
    void MoveLine(int cardNo, int[] axes, double[] dest, double vel);
    void Stop(int cardNo, int mode);
    double ReadPosition(int cardNo, int axis);
    void SetDigitalOut(int cardNo, int port, int on);
}

/// <summary>
/// P/Invoke binding for the Googol-style C ABI (<c>gmotion.dll</c>). Entry points are
/// declared once; calling them without the native library throws <see cref="DllNotFoundException"/>,
/// which the factory avoids by probing first (§6.3). / Googol 风格 C ABI(gmotion.dll) 的
///   P/Invoke 绑定。入口点声明一次；库缺失时调用抛 DllNotFoundException，工厂先探测以规避。
/// </summary>
public sealed class DllImportMotionApi : INativeMotionApi
{
    private const string Dll = "gmotion";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GM_OpenCard(int cardNo);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_CloseCard(int cardNo);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_Home(int cardNo, int axis);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_MoveAbsolute(int cardNo, int axis, double pos, double vel, double acc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_MoveLine(int cardNo, int axisCount, int[] axes, double[] dest, double vel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_Stop(int cardNo, int mode);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern double GM_ReadPosition(int cardNo, int axis);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GM_SetDigitalOut(int cardNo, int port, int on);

    /// <inheritdoc />
    public int Open(int cardNo) => GM_OpenCard(cardNo);

    /// <inheritdoc />
    public void Close(int cardNo) => GM_CloseCard(cardNo);

    /// <inheritdoc />
    public void Home(int cardNo, int axis) => GM_Home(cardNo, axis);

    /// <inheritdoc />
    public void MoveAbsolute(int cardNo, int axis, double pos, double vel, double acc)
        => GM_MoveAbsolute(cardNo, axis, pos, vel, acc);

    /// <inheritdoc />
    public void MoveLine(int cardNo, int[] axes, double[] dest, double vel)
        => GM_MoveLine(cardNo, axes.Length, axes, dest, vel);

    /// <inheritdoc />
    public void Stop(int cardNo, int mode) => GM_Stop(cardNo, mode);

    /// <inheritdoc />
    public double ReadPosition(int cardNo, int axis) => GM_ReadPosition(cardNo, axis);

    /// <inheritdoc />
    public void SetDigitalOut(int cardNo, int port, int on) => GM_SetDigitalOut(cardNo, port, on);
}
