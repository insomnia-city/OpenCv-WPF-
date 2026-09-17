using System.Reflection;

namespace HalconWorkflow.Abstractions.Parameters;

/// <summary>
/// Reads <see cref="NodeParameterAttribute"/>-decorated properties off a strongly
/// typed parameter object and applies typed writes §4.4. Numeric kinds promote
/// across int/double; reads never mutate the instance.
/// / 从带 NodeParameterAttribute 的强类型参数对象上读取元数据并应用类型化写入（§4.4）。
///   数值种类在 int/double 之间提升;读取绝不改动实例。
/// </summary>
public static class ParameterReflection
{
    /// <summary>
    /// Projects decorated properties into parameter rows (name/group/range/kind). · 将带特性的属性投影为参数行
    /// </summary>
    public static IReadOnlyList<ParameterMetadata> Summarize(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var list = new List<ParameterMetadata>();
        foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<NodeParameterAttribute>();
            if (attr is null) continue;
            var kind = InferKind(prop.PropertyType);
            object? value = prop.GetValue(instance);
            if (kind == ParameterKind.Integer) value = Convert.ToInt64(value ?? 0L);
            if (kind == ParameterKind.Real) value = Convert.ToDouble(value ?? 0.0);
            list.Add(new ParameterMetadata
            {
                Name = attr.Name,
                Group = attr.Group,
                Kind = kind,
                Min = attr.Min,
                Max = attr.Max,
                Unit = attr.Unit,
                Description = attr.Description,
                Options = kind == ParameterKind.Selection ? GetOptions(prop) : null,
                Value = value,
                Source = prop,
            });
        }
        return list;
    }

    /// <summary>
    /// Applies a typed value to a parameter; returns the previous value for undo. 
    /// Throws when the value is outside the declared range or the type does not coerce.
    /// / 将类型化值写入参数;返回旧值供撤销。越界或类型不可转义即抛异常。
    /// </summary>
    public static (object? Old, object? New) Apply(object instance, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var prop = Find(instance, name)
            ?? throw new ArgumentException($"no decorated parameter named '{name}' on {instance.GetType().Name}");
        var meta = Summarize(instance).First(m => m.Name == name);

        object? parsed = Coerce(value, prop.PropertyType, meta);
        object? old = prop.GetValue(instance);
        if (Equals(old, parsed)) return (old, old);
        if (meta.Kind == ParameterKind.Integer)
        {
            long v = Convert.ToInt64(parsed);
            if (meta.Min is double mn && v < mn) throw new ArgumentOutOfRangeException(nameof(value), $"{name} below min {mn}");
            if (meta.Max is double mx && v > mx) throw new ArgumentOutOfRangeException(nameof(value), $"{name} above max {mx}");
        }
        else if (meta.Kind == ParameterKind.Real)
        {
            double v = Convert.ToDouble(parsed);
            if (meta.Min is double mn && v < mn) throw new ArgumentOutOfRangeException(nameof(value), $"{name} below min {mn}");
            if (meta.Max is double mx && v > mx) throw new ArgumentOutOfRangeException(nameof(value), $"{name} above max {mx}");
        }
        if (meta.Kind == ParameterKind.Selection && meta.Options is not null && !meta.Options.Contains(Convert.ToString(parsed)))
            throw new ArgumentException($"'{parsed}' is not a valid option for '{name}'");

        prop.SetValue(instance, parsed);
        return (old, parsed);
    }

    private static PropertyInfo? Find(object instance, string name)
    {
        foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<NodeParameterAttribute>() is { } a && a.Name == name)
                return prop;
        }
        return null;
    }

    private static IReadOnlyList<string>? GetOptions(PropertyInfo prop)
        => prop.PropertyType.IsEnum
            ? Enum.GetNames(prop.PropertyType)
            : null;

    private static ParameterKind InferKind(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(bool)) return ParameterKind.Boolean;
        if (t == typeof(string) || t == typeof(byte[])) return ParameterKind.String;
        if (t.IsEnum) return ParameterKind.Selection;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return ParameterKind.Real;
        return ParameterKind.Integer;
    }

    private static object? Coerce(object value, Type target, ParameterMetadata meta)
    {
        if (value is null) return null;
        target = Nullable.GetUnderlyingType(target) ?? target;
        if (target.IsInstanceOfType(value)) return value;
        if (target == typeof(string)) return Convert.ToString(value);
        if (target == typeof(bool)) return Convert.ToBoolean(value);
        if (target == typeof(double)) return Convert.ToDouble(value);
        if (target == typeof(float)) return Convert.ToSingle(value);
        if (target == typeof(decimal)) return Convert.ToDecimal(value);
        if (target == typeof(byte)) return Convert.ToByte(value);
        if (target == typeof(short)) return Convert.ToInt16(value);
        if (target == typeof(int)) return Convert.ToInt32(value);
        if (target == typeof(long)) return Convert.ToInt64(value);
        if (target.IsEnum)
        {
            string s = Convert.ToString(value) ?? "";
            return Enum.Parse(target, s, ignoreCase: true);
        }
        throw new InvalidCastException($"cannot coerce {value.GetType().Name} to {target.Name}");
    }
}