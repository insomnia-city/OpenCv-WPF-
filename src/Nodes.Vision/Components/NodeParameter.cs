namespace HalconWorkflow.Nodes.Vision.Components;

/// <summary>
/// Declares a node parameter on a strongly-typed view-model property (§4.4/§6.2).
/// Drives the reflected property panel; units and ranges are advisory metadata.
/// / 在强类型 VM 属性上声明一个节点参数（§4.4/§6.2）。驱动反射属性面板;单位与范围是元数据约束
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NodeParameterAttribute : Attribute
{
    public string Name { get; }
    public string Group { get; }

    /// <summary>Declared min; null when the attribute used the NaN sentinel. / 声明最小值;NaN 哨兵视为无</summary>
    public double? Min => double.IsNaN(MinRaw) ? null : MinRaw;

    /// <summary>Declared max; null when the attribute used the NaN sentinel. / 声明最大值;NaN 哨兵视为无</summary>
    public double? Max => double.IsNaN(MaxRaw) ? null : MaxRaw;

    public string? Unit { get; }
    public string? Description { get; }

    // Attributes require constant args, so emptiness is expressed via NaN. · 特性参数须为常量,空缺以 NaN 表达
    internal double MinRaw { get; }
    internal double MaxRaw { get; }

    public NodeParameterAttribute(string name, string group = "General", double min = double.NaN, double max = double.NaN,
        string? unit = null, string? description = null)
    {
        Name = name;
        Group = group;
        MinRaw = min;
        MaxRaw = max;
        Unit = unit;
        Description = description;
    }
}

/// <summary>UI-facing kind for a parameter row. / 参数行的 UI 种类</summary>
public enum ParameterKind
{
    Integer,
    Real,
    String,
    Boolean,
    Selection,
}

/// <summary>
/// Reflected parameter row: metadata + live value box. / 反射出的参数行：元数据 + 活值盒子
/// </summary>
public sealed class ParameterMetadata
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "General";
    public ParameterKind Kind { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Unit { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public object? Value { get; init; }
    internal System.Reflection.PropertyInfo? Source { get; init; }
}