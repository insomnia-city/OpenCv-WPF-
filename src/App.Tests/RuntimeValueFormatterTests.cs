using HalconWorkflow.App.Services;
using HalconWorkflow.Nodes.Vision.Imaging;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-13 unit tests: scope-value caption formatting for the canvas badges.
/// / 阶段13 单测：画布徽标使用的运行期 scope 值短文本格式化
/// </summary>
public sealed class RuntimeValueFormatterTests
{
    [Fact]
    public void Format_Null_IsEmpty()
    {
        Assert.Equal("", RuntimeValueFormatter.Format(null));
    }

    [Fact]
    public void Format_Primitives_UseCurrentCulture()
    {
        Assert.Equal("42", RuntimeValueFormatter.Format(42));
        Assert.Equal("3.14", RuntimeValueFormatter.Format(3.14));
        Assert.Equal("true", RuntimeValueFormatter.Format(true).ToLowerInvariant());
        Assert.Equal("hello", RuntimeValueFormatter.Format("hello"));
    }

    [Fact]
    public void Format_VisionFrame_IsCompactSummary()
    {
        var frame = new VisionFrame(320, 240, PixFormat.Gray8, new byte[320 * 240], FrameDomain.Mat);
        Assert.Equal("320×240 Gray8", RuntimeValueFormatter.Format(frame));
    }

    [Fact]
    public void Format_MeasurementResult_IsCompact()
    {
        var m = new MeasurementResult(12.5, 4);
        var text = RuntimeValueFormatter.Format(m);
        Assert.StartsWith("d=", text, StringComparison.Ordinal);
        Assert.EndsWith("px e=4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ByteArray_ReportsLength()
    {
        Assert.Equal("400 bytes", RuntimeValueFormatter.Format(new byte[400]));
    }

    [Fact]
    public void Format_LongString_IsTruncated()
    {
        var longText = new string('x', 200);
        var text = RuntimeValueFormatter.Format(longText);
        Assert.Equal(41, text.Length);   // 40 chars + ellipsis · 40 字符 + 省略号
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }
}