using HalconWorkflow.Core.Contracts;

namespace HalconWorkflow.Core.Types;

/// <summary>
/// Standard leaf type descriptors. Subtypes declare their parent via <see cref="Parent"/>. /* 标准叶子类型描述符;子类通过 Parent 声明其父类 */
/// </summary>
public abstract class TypeDescriptorBase : ITypeDescriptor
{
    protected TypeDescriptorBase(string name, TypeDescriptorBase? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Parent descriptor; null for the root of the hierarchy. /* 父描述符;层级根为 null */
    /// </summary>
    protected TypeDescriptorBase? Parent { get; }

    /// <inheritdoc />
    public virtual bool IsAssignableTo(ITypeDescriptor target)
    {
        if (target is TypeDescriptorBase t)
        {
            for (var cur = this; cur is not null; cur = cur.Parent)
                if (cur == t) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public virtual string AssignabilityPath
    {
        get
        {
            var names = new List<string>();
            for (var cur = this; cur is not null; cur = cur.Parent)
                names.Add(cur.Name);
            names.Reverse();
            return string.Join(" ⊂ ", names);
        }
    }

    public override string ToString() => AssignabilityPath;
}

/// <summary>
/// Root descriptor every data type descends from. /* 所有数据类型的根描述符 */
/// </summary>
public sealed class VisionObjectDescriptor : TypeDescriptorBase
{
    public VisionObjectDescriptor() : base("VisionObject", null) { }
    public static VisionObjectDescriptor Instance { get; } = new();
}

/// <summary>
/// "Image" (HObject handle semantics). /* "Image"（HObject 句柄语义） */
/// </summary>
public sealed class ImageDescriptor : TypeDescriptorBase
{
    public ImageDescriptor() : base("Image", VisionObjectDescriptor.Instance) { }
    public static ImageDescriptor Instance { get; } = new();
}

/// <summary>
/// "Region" (HRegion handle). /* "Region"（HRegion 句柄） */
/// </summary>
public sealed class RegionDescriptor : TypeDescriptorBase
{
    public RegionDescriptor() : base("Region", VisionObjectDescriptor.Instance) { }
    public static RegionDescriptor Instance { get; } = new();
}

/// <summary>
/// "XLD" (HXLD handle). /* "XLD"（HXLD 句柄） */
/// </summary>
public sealed class XldDescriptor : TypeDescriptorBase
{
    public XldDescriptor() : base("XLD", VisionObjectDescriptor.Instance) { }
    public static XldDescriptor Instance { get; } = new();
}

/// <summary>
/// Numeric root: Integer and Real share this parent; Integer→Real implicit promotion. /* 数值根：整数与实数共享父类型;整数可隐式提升为实数 */
/// </summary>
public sealed class NumberDescriptor : TypeDescriptorBase
{
    public NumberDescriptor() : base("Number", null) { }
    public static NumberDescriptor Instance { get; } = new();
}

public sealed class IntegerDescriptor : TypeDescriptorBase
{
    public IntegerDescriptor() : base("Integer", NumberDescriptor.Instance) { }
    public static IntegerDescriptor Instance { get; } = new();
}

public sealed class RealDescriptor : TypeDescriptorBase
{
    public RealDescriptor() : base("Real", NumberDescriptor.Instance) { }
    public static RealDescriptor Instance { get; } = new();
}

public sealed class StringDescriptor : TypeDescriptorBase
{
    public StringDescriptor() : base("String", null) { }
    public static StringDescriptor Instance { get; } = new();
}

public sealed class BoolDescriptor : TypeDescriptorBase
{
    public BoolDescriptor() : base("Bool", null) { }
    public static BoolDescriptor Instance { get; } = new();
}

public sealed class PointDescriptor : TypeDescriptorBase
{
    public PointDescriptor() : base("Point", null) { }
    public static PointDescriptor Instance { get; } = new();
}

/// <summary>
/// Structured result containing multiple values. /* 含多值的结构化结果 */
/// </summary>
public sealed class ResultDescriptor : TypeDescriptorBase
{
    public ResultDescriptor() : base("Result", null) { }
    public static ResultDescriptor Instance { get; } = new();
}

/// <summary>
/// Registry of standard descriptors; lets users resolve by name. · 标准描述符注册表;可按名称解析
/// </summary>
public static class BuiltinTypes
{
    private static readonly Dictionary<string, ITypeDescriptor> Map = new(StringComparer.Ordinal)
    {
        ["VisionObject"] = VisionObjectDescriptor.Instance,
        ["Image"] = ImageDescriptor.Instance,
        ["Region"] = RegionDescriptor.Instance,
        ["XLD"] = XldDescriptor.Instance,
        ["Number"] = NumberDescriptor.Instance,
        ["Integer"] = IntegerDescriptor.Instance,
        ["Real"] = RealDescriptor.Instance,
        ["String"] = StringDescriptor.Instance,
        ["Bool"] = BoolDescriptor.Instance,
        ["Point"] = PointDescriptor.Instance,
        ["Result"] = ResultDescriptor.Instance,
    };

    /// <summary>
    /// Resolves a built-in descriptor by name; null if unknown. · 按名称解析内置描述符;未知返回 null
    /// </summary>
    public static ITypeDescriptor? Find(string name) =>
        Map.TryGetValue(name ?? string.Empty, out var d) ? d : null;
}