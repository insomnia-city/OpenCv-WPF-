using System.Globalization;
using System.Text.Json.Nodes;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Serialization;

namespace HalconWorkflow.Abstractions.Parameters;

/// <summary>
/// Bridges reflected node parameters and the separately-persisted recipe (§5.8):
/// snapshots every <see cref="IParameterized"/> node's decorated properties into a
/// <see cref="Recipe"/> and applies a loaded recipe back onto a graph. Keys are the
/// node's recipe id (falling back to the instance id for legacy files). Reads/writes
/// are tolerant: a single bad value never aborts a load. /
/// 在反射节点参数与独立持久化的配方(§5.8)之间搭桥：把每个 IParameterized 节点的
/// 特性参数快照进 Recipe，并把载入的配方写回图。键为节点配方ID(旧文件回退实例ID)。
/// 读写均容错：单个坏值不会中断载入。
/// </summary>
public static class RecipeBinder
{
    /// <summary>
    /// Projects a parameter object into a JSON object keyed by <see cref="NodeParameterAttribute"/> name. 
    /// / 将参数对象投影为以特性名索引的 JSON 对象
    /// </summary>
    public static JsonObject Snapshot(object parameterObject)
    {
        ArgumentNullException.ThrowIfNull(parameterObject);
        var obj = new JsonObject();
        foreach (var meta in ParameterReflection.Summarize(parameterObject))
            obj[meta.Name] = ToNode(meta.Value);
        return obj;
    }

    /// <summary>
    /// Applies a JSON object onto a parameter object; unknown names and uncoercible
    /// values are skipped. Returns true when at least one parameter was written. 
    /// / 将 JSON 对象写回参数对象;未知名称与不可转义值跳过。至少写入一项返回 true
    /// </summary>
    public static bool Apply(object parameterObject, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(parameterObject);
        ArgumentNullException.ThrowIfNull(values);
        var byName = ParameterReflection.Summarize(parameterObject)
            .ToDictionary(m => m.Name, StringComparer.Ordinal);
        var applied = false;
        foreach (var (name, node) in values)
        {
            if (node is null || !byName.TryGetValue(name, out var meta)) continue;
            try
            {
                object value = meta.Kind switch
                {
                    ParameterKind.Integer => node.GetValue<long>(),
                    ParameterKind.Real => node.GetValue<double>(),
                    ParameterKind.Boolean => node.GetValue<bool>(),
                    _ => node.GetValue<string>()
                };
                ParameterReflection.Apply(parameterObject, name, value);
                applied = true;
            }
            catch
            {
                // Tolerate a stale or hand-edited recipe value. · 容忍陈旧或手改的配方值
            }
        }
        return applied;
    }

    /// <summary>
    /// Rewrites <paramref name="recipe"/> from the graph: parameterized nodes are
    /// snapshotted, suspended nodes keep any previously stored entry (so re-enabling a
    /// plugin preserves their params), and entries for deleted nodes are pruned. 
    /// / 依据图重写配方：参数化节点被快照,挂起节点保留旧条目(重启用插件后参数不丢),
    ///   已删除节点的条目被清理
    /// </summary>
    public static void Capture(GraphModel graph, Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(recipe);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gn in graph.Nodes.Values)
        {
            var id = string.IsNullOrEmpty(gn.RecipeId) ? gn.Id : gn.RecipeId;
            gn.RecipeId = id;
            referenced.Add(id);
            if (gn.Node is IParameterized p)
                recipe.Params[id] = Snapshot(p.ParameterObject);
        }
        foreach (var key in recipe.Params.Keys.Where(k => !referenced.Contains(k)).ToList())
            recipe.Params.Remove(key);
    }

    /// <summary>
    /// Applies the loaded recipe onto graph nodes. Returns the number of parameterized
    /// nodes that received values. Missing entries leave defaults untouched. 
    /// / 将载入的配方写回图节点。返回收到值的参数化节点数;缺失条目保持默认值
    /// </summary>
    public static int Restore(GraphModel graph, Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(recipe);
        var applied = 0;
        foreach (var gn in graph.Nodes.Values)
        {
            var id = string.IsNullOrEmpty(gn.RecipeId) ? gn.Id : gn.RecipeId;
            gn.RecipeId = id;
            if (gn.Node is not IParameterized p) continue;
            if (!recipe.Params.TryGetValue(id, out var values)) continue;
            if (Apply(p.ParameterObject, values)) applied++;
        }
        return applied;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n,
        bool b => JsonValue.Create(b),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        Enum e => JsonValue.Create(e.ToString()),
        string s => JsonValue.Create(s),
        int i => JsonValue.Create((long)i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        decimal m => JsonValue.Create((double)m),
        IFormattable fm => JsonValue.Create(fm.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString())
    };
}
