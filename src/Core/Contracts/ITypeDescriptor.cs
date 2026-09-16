namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// Type compatibility descriptor for data ports. /* 数据端口的类型兼容描述 */
/// </summary>
public interface ITypeDescriptor
{
    /// <summary>
    /// Type name, e.g. "Image" / "Number" / "Result". /* 类型名，如 "Image" / "Number" / "Result" */
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Compatibility check: subclass-to-parent allowed (Image → VisionObject). /* 兼容判定：子类可赋父类 */
    /// </summary>
    bool IsAssignableTo(ITypeDescriptor target);

    /// <summary>
    /// Full assignability path for diagnostics. /* 完整可赋性路径（用于诊断） */
    /// </summary>
    string AssignabilityPath { get; }
}