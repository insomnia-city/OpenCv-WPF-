namespace HalconWorkflow.Core.Model;

/// <summary>
/// Contract identity of a node: namespace + version.
/// Nodes are serialized by contract name, never by CLR type name. /* 节点契约身份：命名空间 + 版本号;节点按契约名序列化，绝不使用类名 */
/// </summary>
public sealed record NodeContract(string Namespace, int Version)
{
    /// <summary>
    /// Renders "ns:version", e.g. "vision.threshold:1". /* 渲染为 "ns:version"，如 "vision.threshold:1" */
    /// </summary>
    public override string ToString() => $"{Namespace}:{Version}";

    /// <summary>
    /// Checks semantic compatibility: same namespace and same major version. /* 语义兼容：同名且 major 版本一致 */
    /// </summary>
    public bool IsCompatibleWith(NodeContract other) =>
        string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) && Version == other.Version;

    /// <summary>
    /// Parses a "ns:version" string into a contract. Throws FormatException on bad input. /* 将 "ns:version" 字符串解析为契约;非法输入抛 FormatException */
    /// </summary>
    public static NodeContract Parse(string text)
    {
        var idx = text.LastIndexOf(':');
        if (idx <= 0 || idx == text.Length - 1)
            throw new FormatException($"Invalid node contract format: {text}");
        var ns = text[..idx];
        if (!int.TryParse(text[(idx + 1)..], out var version))
            throw new FormatException($"Invalid contract version in: {text}");
        return new NodeContract(ns, version);
    }
}