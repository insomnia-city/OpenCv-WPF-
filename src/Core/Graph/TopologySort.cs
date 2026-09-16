using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Graph;

/// <summary>
/// Graph algorithms used by validation and scheduling. /* 校验与调度共用的图算法 */
/// </summary>
internal static class GraphAlgorithms
{
    /// <summary>
    /// Detects cycles in a directed graph given as adjacency list. /* 检测有向图（邻接表形式）中的环 */
    /// </summary>
    public static bool HasCycle(Dictionary<string, List<string>> adj)
    {
        var state = new Dictionary<string, byte>(StringComparer.Ordinal); // 0=unvisited 1=visiting 2=done /* 0未访问 1访问中 2完成 */
        foreach (var id in adj.Keys)
        {
            if (DFS(id)) return true;
        }
        return false;

        bool DFS(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s == 1) return true;
                return false;
            }
            state[id] = 1;
            foreach (var next in adj[id])
                if (DFS(next)) return true;
            state[id] = 2;
            return false;
        }
    }
}

/// <summary>
/// Kahn's algorithm producing a topological order over exec links. /* Kahn 算法，基于控制流连线求拓扑序 */
/// </summary>
internal static class TopologySort
{
    /// <summary>
    /// Sorts nodes by exec-link ordering; nodes not reachable by exec edges are appended
    /// in insertion order. Any cycle is reported via <paramref name="cycleNodes"/>. /* 按控制流连线排序;不参与执行的节点按插入序追加;成环节点写入 cycleNodes */
    /// </summary>
    public static List<GraphNode> Sort(
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphLink> links,
        List<string> cycleNodes)
    {
        var all = nodes.ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++) index[all[i].Id] = i;

        var inDegree = new int[all.Count];
        var adj = new List<int>[all.Count];
        for (var i = 0; i < all.Count; i++) adj[i] = [];

        foreach (var l in links)
        {
            if (l.From.Kind != PortKind.Exec) continue;
            if (!index.TryGetValue(l.From.Owner.Id, out var u)) continue;
            if (!index.TryGetValue(l.To.Owner.Id, out var v)) continue;
            adj[u].Add(v);
            inDegree[v]++;
        }

        var queue = new Queue<int>();
        for (var i = 0; i < all.Count; i++)
            if (inDegree[i] == 0) queue.Enqueue(i);

        var result = new List<GraphNode>(all.Count);
        var visited = new HashSet<int>();
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            if (!visited.Add(u)) continue;
            result.Add(all[u]);
            foreach (var v in adj[u])
                if (--inDegree[v] == 0) queue.Enqueue(v);
        }

        if (visited.Count < all.Count)
        {
            // Nodes with non-zero in-degree inside a cycle. · 环内(入度非零)节点
            for (var i = 0; i < all.Count; i++)
                if (!visited.Contains(i)) cycleNodes.Add(all[i].Id);
            // Fall back: insert the rest at the end so callers can still work. /* 兜底：其余节点追加到尾部以便调用方继续工作 */
            for (var i = 0; i < all.Count; i++)
                if (!visited.Contains(i)) result.Add(all[i]);
        }

        return result;
    }
}