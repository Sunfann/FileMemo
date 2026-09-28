using System.IO;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 关系图谱 / 知识网络（需求 3.9，P2）。
///
/// 把「便签记录 + 文件备注 + 待办」统一为节点，把
/// record_link（[[双向链接]]）、version_link（版本关系）、
/// 待办挂靠的文件 统一为边，供知识网络视图可视化。
/// </summary>
public sealed class GraphService
{
    private readonly Repository _repo;

    public GraphService(Repository repo) => _repo = repo;

    /// <summary>构建完整图谱（受 maxNodes 保护，避免超大库卡顿）。</summary>
    public GraphData Build(int maxNodes = 400)
    {
        var data = new GraphData();
        // refId（底层对象 id）→ 节点 id
        var noteNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var taskNode = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            // ---- 便签记录 ----
            foreach (var n in _repo.GetNotes().Take(maxNodes))
            {
                var id = "n:" + n.Id;
                data.Nodes.Add(new GraphNode
                {
                    Id = id,
                    RefId = n.Id,
                    Kind = GraphNodeKind.Note,
                    Label = string.IsNullOrWhiteSpace(n.Title) ? "(无标题便签)" : n.Title,
                    Tags = n.Tags
                });
                noteNode[n.Id] = id;
            }

            // ---- 文件 / 文件夹备注 ----
            foreach (var a in _repo.GetAllAnnotations().Take(maxNodes))
            {
                var fr = _repo.GetFileRefById(a.FileRefId);
                if (fr == null) continue;
                var id = "f:" + fr.Id;
                data.Nodes.Add(new GraphNode
                {
                    Id = id,
                    RefId = fr.Id,
                    Kind = fr.IsDir ? GraphNodeKind.Folder : GraphNodeKind.File,
                    Label = SafeName(fr.Path, fr.IsDir),
                    Tags = a.Tags,
                    State = a.StateLabel
                });
                fileNode[fr.Id] = id;
            }

            // ---- 待办 ----
            foreach (var t in _repo.GetTasks().Take(maxNodes))
            {
                var id = "t:" + t.Id;
                data.Nodes.Add(new GraphNode
                {
                    Id = id,
                    RefId = t.Id,
                    Kind = GraphNodeKind.Task,
                    Label = string.IsNullOrWhiteSpace(t.Title) ? "(无标题待办)" : t.Title,
                    Tags = t.Tags,
                    State = t.StateLabel
                });
                taskNode[t.Id] = id;
            }

            // ---- 边：[[双向链接]] / 记录引用 ----
            foreach (var kv in noteNode)
            {
                foreach (var fromId in _repo.GetBacklinks(kv.Key))
                {
                    if (noteNode.TryGetValue(fromId, out var fromNode))
                        data.Edges.Add(new GraphEdge { FromId = fromNode, ToId = kv.Value, Type = "wiki", Label = "引用" });
                }
            }

            // ---- 边：版本关系 ----
            foreach (var kv in fileNode)
            {
                foreach (var v in _repo.GetVersionLinks(kv.Key))
                {
                    var otherId = v.FromFileRefId == kv.Key ? v.ToFileRefId : v.FromFileRefId;
                    if (fileNode.TryGetValue(otherId, out var otherNode))
                        data.Edges.Add(new GraphEdge { FromId = kv.Value, ToId = otherNode, Type = "version", Label = "v" + v.Version });
                }
            }

            // ---- 边：待办挂靠文件 ----
            foreach (var kv in taskNode)
            {
                var tasks = _repo.GetTasks();
                var t = tasks.FirstOrDefault(x => x.Id == kv.Key);
                if (t?.FileRefId != null && fileNode.TryGetValue(t.FileRefId, out var fnode))
                    data.Edges.Add(new GraphEdge { FromId = kv.Value, ToId = fnode, Type = "task", Label = "挂靠" });
            }
        }
        catch { /* 单个数据源异常不应让整图失败 */ }

        // 去重边 + 计算度
        Dedup(data);
        ComputeDegrees(data);
        return data;
    }

    /// <summary>以某节点为中心取邻域（用于「聚焦查看」）。</summary>
    public GraphData Neighborhood(string centerNodeId, int depth = 2)
    {
        var full = Build();
        var keep = new HashSet<string>(StringComparer.Ordinal) { centerNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { centerNodeId };

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in full.Edges)
        {
            if (!adjacency.TryGetValue(e.FromId, out var l1)) adjacency[e.FromId] = l1 = new();
            l1.Add(e.ToId);
            if (!adjacency.TryGetValue(e.ToId, out var l2)) adjacency[e.ToId] = l2 = new();
            l2.Add(e.FromId);
        }

        for (int d = 0; d < depth; d++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in frontier)
                if (adjacency.TryGetValue(id, out var neighbors))
                    foreach (var nb in neighbors)
                        if (keep.Add(nb)) next.Add(nb);
            frontier = next;
        }

        return Subgraph(full, keep);
    }

    private static GraphData Subgraph(GraphData full, HashSet<string> keep)
    {
        var g = new GraphData();
        foreach (var n in full.Nodes)
            if (keep.Contains(n.Id)) g.Nodes.Add(n);
        foreach (var e in full.Edges)
            if (keep.Contains(e.FromId) && keep.Contains(e.ToId)) g.Edges.Add(e);
        ComputeDegrees(g);
        return g;
    }

    private static void Dedup(GraphData g)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<GraphEdge>();
        foreach (var e in g.Edges)
        {
            // 无向去重（version/task 为无向；wiki 保留方向但同对去重）
            var key = string.CompareOrdinal(e.FromId, e.ToId) <= 0
                ? $"{e.FromId}|{e.ToId}|{e.Type}"
                : $"{e.ToId}|{e.FromId}|{e.Type}";
            if (seen.Add(key)) kept.Add(e);
        }
        g.Edges.Clear();
        g.Edges.AddRange(kept);
    }

    private static void ComputeDegrees(GraphData g)
    {
        var deg = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in g.Edges)
        {
            deg[e.FromId] = deg.TryGetValue(e.FromId, out var a) ? a + 1 : 1;
            deg[e.ToId] = deg.TryGetValue(e.ToId, out var b) ? b + 1 : 1;
        }
        foreach (var n in g.Nodes)
            n.Degree = deg.TryGetValue(n.Id, out var d) ? d : 0;
    }

    private static string SafeName(string path, bool isDir)
    {
        try
        {
            var name = isDir ? new DirectoryInfo(path).Name : Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            return name;
        }
        catch { return path; }
    }
}
