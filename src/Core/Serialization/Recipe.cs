using System.Text.Json;
using System.Text.Json.Nodes;

namespace HalconWorkflow.Core.Serialization;

/// <summary>
/// Recipe persisted separately from topology (§5.8): params keyed by recipe id. 
/// 配方与拓扑分开存储(§5.8)：参数按配方ID组织
/// </summary>
public sealed class Recipe
{
    public const string Schema = "vision.workflow/recipe";
    public const int Version = 1;

    /// <summary>
    /// Params by recipe id, stored as arbitrary JSON objects. /* 按配方ID存储的参数，为任意 JSON 对象 */
    /// </summary>
    public Dictionary<string, JsonObject> Params { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads a typed value from a recipe. Returns null when missing or type mismatch. 
    /// 从配方读取类型化值;缺失或类型不匹配返回 null
    /// </summary>
    public T? Get<T>(string recipeId, string param)
    {
        if (!Params.TryGetValue(recipeId, out var obj) || obj[param] is not JsonNode n) return default;
        try { return n.GetValue<T>(); }
        catch { return default; }
    }

    /// <summary>
    /// Gets the raw JSON node for a param (for migration tools). · 取某参数的原始 JSON 节点(供迁移工具)
    /// </summary>
    public JsonObject? GetRaw(string recipeId) => Params.TryGetValue(recipeId, out var o) ? o : null;

    public static string Serialize(Recipe recipe)
    {
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["version"] = Version,
            ["params"] = new JsonObject()
        };
        var p = (JsonObject)root["params"]!;
        foreach (var (k, v) in recipe.Params)
            p[k] = v.DeepClone();
        return root.ToJsonString(GraphJsonSerializer.Options);
    }

    public static Recipe Deserialize(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? throw new FormatException("Recipe JSON is empty.");

        if ((string?)root["schema"] != Schema)
            throw new FormatException($"Unsupported recipe schema '{root["schema"]}'.");

        var recipe = new Recipe();
        if (root["params"] is JsonObject p)
            foreach (var (k, v) in p)
                recipe.Params[k] = v?.DeepClone() as JsonObject ?? new JsonObject();
        return recipe;
    }
}