using System.Collections;
using System.Globalization;
using HalconWorkflow.Nodes.Vision.Imaging;
using HalconWorkflow.Nodes.Vision.Nodes;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Short caption text for a live scope value shown under a port (§5.4, stage-13). Empty for null.
/// / 端口徽标显示的运行期 scope 值短文本(§5.4,阶段13)；null 返回空串(徽标自动收缩)
/// </summary>
public static class RuntimeValueFormatter
{
    public static string Format(object? value) => value switch
    {
        null => "",
        VisionFrame frame => $"{frame.Width}×{frame.Height} {frame.Format}",
        MeasurementResult m => $"d={m.Distance.ToString("F2", CultureInfo.CurrentCulture)}px e={m.Edges}",
        string s => s.Length <= 40 ? s : s[..40] + "…",
        byte[] bytes => bytes.Length.ToString(CultureInfo.CurrentCulture) + " bytes",
        Array array => "["
            + array.Length.ToString(CultureInfo.CurrentCulture)
            + " " + (array.GetType().GetElementType()?.Name ?? "?") + "]",
        IConvertible c => Convert.ToString(c, CultureInfo.CurrentCulture) ?? "",
        IEnumerable e => "[collection]",
        _ => value.ToString() ?? ""
    };
}