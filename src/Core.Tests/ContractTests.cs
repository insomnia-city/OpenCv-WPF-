using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Core.Tests;

public class NodeContractTests
{
    [Theory]
    [InlineData("vision.threshold:1", "vision.threshold", 1)]
    [InlineData("core/db.write:2", "core/db.write", 2)]
    public void Parse_ValidString_ReturnsContract(string text, string ns, int version)
    {
        var c = NodeContract.Parse(text);
        Assert.Equal(ns, c.Namespace);
        Assert.Equal(version, c.Version);
        Assert.Equal(text, c.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon")]
    [InlineData(":1")]
    [InlineData("ns:")]
    [InlineData("ns:abc")]
    public void Parse_InvalidString_Throws(string text)
    {
        Assert.Throws<FormatException>(() => NodeContract.Parse(text));
    }

    [Fact]
    public void Compatibility_SameNsSameMajor_True()
    {
        var a = new NodeContract("vision.threshold", 3);
        var b = new NodeContract("vision.threshold", 3);
        Assert.True(a.IsCompatibleWith(b));
        Assert.True(b.IsCompatibleWith(a));
    }

    [Fact]
    public void Compatibility_DifferentNs_False()
    {
        var a = new NodeContract("vision.threshold", 1);
        var b = new NodeContract("vision.match", 1);
        Assert.False(a.IsCompatibleWith(b));
    }
}

public class TypeDescriptorTests
{
    [Fact]
    public void Image_AssignableTo_VisionObject()
    {
        Assert.True(ImageDescriptor.Instance.IsAssignableTo(VisionObjectDescriptor.Instance));
    }

    [Fact]
    public void Integer_AssignableTo_Number()
    {
        Assert.True(IntegerDescriptor.Instance.IsAssignableTo(NumberDescriptor.Instance));
    }

    [Fact]
    public void Integer_NotAssignableTo_RealDirectly()
    {
        // Integer and Real are siblings; promotion is a config-level rule, not subtyping. 
        // 整数与实数为兄弟类型;隐式提升是配置规则，非子类型关系
        Assert.False(IntegerDescriptor.Instance.IsAssignableTo(RealDescriptor.Instance));
    }

    [Fact]
    public void Image_NotAssignableTo_Number()
    {
        Assert.False(ImageDescriptor.Instance.IsAssignableTo(NumberDescriptor.Instance));
    }

    [Fact]
    public void Registry_ResolvesBuiltinByName()
    {
        Assert.Same(ImageDescriptor.Instance, BuiltinTypes.Find("Image"));
        Assert.Null(BuiltinTypes.Find("Unknown"));
    }
}