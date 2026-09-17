using System.Globalization;

namespace HalconWorkflow.Nodes.Motion;

/// <summary>
/// Engineering-unit ↔ native-count conversion and CSV parsing for motion nodes.
/// Motion commands are authored in engineering units; the controller works in native
/// counts, so every move applies <see cref="ToNative"/> and every read applies
/// <see cref="FromNative"/>. / 运动节点的工程单位↔原生计数换算与 CSV 解析。运动命令以工程
///   单位编写、控制器以原生计数工作，故每次移动应用 ToNative，每次读取应用 FromNative。
/// </summary>
public static class MotionUnits
{
    /// <summary>Converts an engineering value to native counts. / 工程值 → 原生计数</summary>
    public static double ToNative(double engineering, double scale) => engineering * scale;

    /// <summary>Converts native counts to engineering units (identity when scale is 0). / 原生计数 → 工程单位(scale 为 0 时原样)</summary>
    public static double FromNative(double native, double scale) => scale == 0 ? native : native / scale;

    /// <summary>Parses a comma/semicolon/space separated list of doubles. / 解析逗号/分号/空格分隔的浮点列表</summary>
    public static double[] ParseValues(string text)
    {
        var parts = Split(text);
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                throw new FormatException($"'{parts[i]}' is not a number (use invariant culture, e.g. 12.5)");
        }
        return result;
    }

    /// <summary>Parses a comma/semicolon/space separated list of axis indices. / 解析逗号/分号/空格分隔的轴号列表</summary>
    public static int[] ParseAxes(string text)
    {
        var parts = Split(text);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                throw new FormatException($"'{parts[i]}' is not an axis index");
        }
        return result;
    }

    private static string[] Split(string text) =>
        (text ?? "").Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
