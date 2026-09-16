using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Nodes.Flow;

/// <summary>
/// Control-flow constructs as first-class graph nodes (§13.1 stage 4, lines 182/592): 
/// branch routing, join, counter, script, delay. Serial engine keeps a single exec chain. 
/// 图内一等公民的流程节点：分支路由/汇聚/计数/脚本/延时(§13.1 阶段4, 第182/592行)
/// </summary>
public static class FlowNodes
{
    /// <summary>
    /// Branch: evaluates a condition and routes one of two data values downstream; exec passes through. 
    /// 分支节点：按条件从两个输入选择一路数据下传;控制流直通
    /// </summary>
    public static INode Branch(string id, double threshold = 0.5)
    {
        var node = new FlowNodeBase(id, new NodeContract("flow.branch", 1),
            execute: (ctx, _) =>
            {
                // Route one of two data inputs by condition; absent condition defaults to true. 
                // 按条件从两路数据输入中选一路转发;条件缺失时默认走真支
                var cond = ctx.GetData("Condition") is null || ValueCoercion.ToBool(ctx.GetData("Condition"));
                object? value = cond ? ctx.GetData("InTrue") : ctx.GetData("InFalse");
                if (value is null) value = ctx.GetData("InTrue") ?? ctx.GetData("InFalse");
                ctx.SetData("Out", value);
                ctx.SetData("Taken", cond);
                return Task.CompletedTask;
            },
            execIn: true, execOut: true,
            ("Condition", BoolDescriptor.Instance, true),
            ("InTrue", VisionObjectDescriptor.Instance, true),
            ("InFalse", VisionObjectDescriptor.Instance, true));
        node.AddDataOut("Out", VisionObjectDescriptor.Instance);
        node.AddDataOut("Taken", BoolDescriptor.Instance);
        return node;
    }

    /// <summary>
    /// Join: requires both data inputs to be present (serial engine guarantees feeding order) and merges them into one Result. 
    /// 汇聚节点：要求两条数据输入齐备(串行引擎保证喂入顺序)并合成为一条 Result
    /// </summary>
    public static INode Join(string id)
    {
        var node = new FlowNodeBase(id, new NodeContract("flow.join", 1),
            execute: (ctx, _) =>
            {
                var a = ctx.GetData("A");
                var b = ctx.GetData("B");
                if (a is null || b is null)
                    throw new InvalidOperationException("Join requires both A and B inputs to be fed.");
                ctx.SetData("Merged", new FlowResult($"A:{a} | B:{b}"));
                return Task.CompletedTask;
            },
            execIn: true, execOut: true,
            ("A", ResultDescriptor.Instance, false),
            ("B", ResultDescriptor.Instance, false));
        node.AddDataOut("Merged", ResultDescriptor.Instance);
        return node;
    }

    /// <summary>
    /// Counter: counts exec pulses within a batch; emits the count as Integer. 
    /// 计数器：在批次内累计控制流脉冲次数;输出 Integer
    /// </summary>
    public static INode Counter(string id)
    {
        var node = new FlowNodeBase(id, new NodeContract("flow.counter", 1),
            execute: (ctx, _) =>
            {
                var n = ValueCoercion.ToInt64(ctx.GetData("Count") ?? 0) + 1;
                ctx.SetData("Count", n);
                return Task.CompletedTask;
            },
            execIn: true, execOut: true);
        node.AddDataOut("Count", IntegerDescriptor.Instance);
        return node;
    }

    /// <summary>
    /// Delay: sleeps for a fixed duration honouring cancellation; exec pass-through. 
    /// 延时节点：固定时长休眠并响应取消;控制流直通
    /// </summary>
    public static INode Delay(string id, int ms = 20)
        => new FlowNodeBase(id, new NodeContract("flow.delay", 1),
            execute: async (_, ct) => await Task.Delay(Math.Max(0, ms), ct),
            execIn: true, execOut: true);

    /// <summary>Duplicate-name guard: the FlowResult record carries a TLS display. · 流程载荷</summary>
    public sealed record FlowResult(string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>
/// Numeric/bool conversion helpers shared by flow nodes. · 流程节点共享的数值/布尔转换
/// </summary>
public static class ValueCoercion
{
    public static double ToDouble(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        long l => l,
        int i => i,
        double d => d,
        float f => f,
        string s => double.TryParse(s, out var dv) ? dv : 0,
        _ => 0
    };

    public static long ToInt64(object? v) => (long)ToDouble(v);

    public static bool ToBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => bool.TryParse(s, out var bv) && bv,
        _ => ToDouble(v) != 0
    };

    public static string ToText(object? v) => v switch
    {
        null => "",
        bool _ or double _ or long _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => v.ToString() ?? ""
    };
}