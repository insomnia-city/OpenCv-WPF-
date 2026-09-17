using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Motion.Nodes;

namespace HalconWorkflow.Nodes.Motion;

/// <summary>
/// Creates motion nodes from contracts: motion.home/moveAbs/moveRel/line/waitInPos/dout.
/// / 根据契约创建运动节点
/// </summary>
public sealed class MotionNodeFactory : INodeFactory
{
    public INode? Create(NodeContract contract, string id) => contract switch
    {
        { Namespace: "motion.home", Version: 1 } => new MotionHomeNode(id),
        { Namespace: "motion.moveAbs", Version: 1 } => new MotionMoveAbsNode(id),
        { Namespace: "motion.moveRel", Version: 1 } => new MotionMoveRelNode(id),
        { Namespace: "motion.line", Version: 1 } => new MotionLineNode(id),
        { Namespace: "motion.waitInPos", Version: 1 } => new MotionWaitInPosNode(id),
        { Namespace: "motion.dout", Version: 1 } => new MotionDoutNode(id),
        _ => null,
    };
}
