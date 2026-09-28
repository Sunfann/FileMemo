namespace FileMemo.App.Models;

/// <summary>
/// P2 新增实体（需求文档 7.P2）：
///   - 关系图谱 / 知识网络：GraphNode / GraphEdge / GraphData
///   - 图片向量搜索：ImageVector / VectorMatch
///   - 团队协作预留：CollaborationSession / CollaborationMember
///   - 插件系统：PluginInfo
/// </summary>

// ============================ 关系图谱 ============================
public enum GraphNodeKind
{
    Note,
    File,
    Folder,
    Task,
    Clip
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public GraphNodeKind Kind { get; set; } = GraphNodeKind.Note;
    /// <summary>底层对象 id（记录 id / FileRef id）。</summary>
    public string RefId { get; set; } = "";
    public string Tags { get; set; } = "";
    public string State { get; set; } = "";
    /// <summary>度（连边数量），用于视图大小映射。</summary>
    public int Degree { get; set; }
    /// <summary>布局坐标（由视图写入）。</summary>
    public double X { get; set; }
    public double Y { get; set; }

    public string KindLabel => Kind switch
    {
        GraphNodeKind.Note => "便签",
        GraphNodeKind.File => "文件",
        GraphNodeKind.Folder => "文件夹",
        GraphNodeKind.Task => "待办",
        GraphNodeKind.Clip => "剪贴板",
        _ => "对象"
    };
}

public sealed class GraphEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    /// <summary>ref / wiki / version / related / contains / task。</summary>
    public string Type { get; set; } = "ref";
    public string Label { get; set; } = "";
}

public sealed class GraphData
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();

    public Dictionary<string, GraphNode> ById()
    {
        var map = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in Nodes) map[n.Id] = n;
        return map;
    }
}

// ========================= 图片向量搜索 =========================
/// <summary>
/// 本地图像特征向量（纯离线，不引入任何 AI 模型 / 云端）。
/// 特征 = 8x8 灰度（感知哈希风格，64 维）+ 4x4x4 颜色直方图（64 维）+
///        边缘密度网格（4x4，16 维）+ 全局统计（4 维）= 148 维。
/// 相似度使用余弦相似度。
/// </summary>
public sealed class ImageVector
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileRefId { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public int Dim { get; set; }
    /// <summary>特征值（与 Dim 等长）。</summary>
    public float[] Values { get; set; } = Array.Empty<float>();
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string ToStore() => string.Join(',', Values.Select(v => v.ToString("0.####")));

    public static float[] FromStore(string s, int dim)
    {
        var arr = new float[dim];
        if (string.IsNullOrWhiteSpace(s)) return arr;
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < arr.Length && i < parts.Length; i++)
        {
            if (float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
                arr[i] = f;
        }
        return arr;
    }
}

public sealed class VectorMatch
{
    public string FileRefId { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public double Score { get; set; }
    public string Label => $"{Score:P0}";
}

// ========================= 团队协作预留 =========================
public enum CollaborationRole
{
    Owner,
    Editor,
    Viewer
}

public sealed class CollaborationMember
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public CollaborationRole Role { get; set; } = CollaborationRole.Viewer;
}

/// <summary>
/// 协作会话（P2 预留）：仅保存工作区元数据与成员，真正的同步由
/// <c>ICollaborationProvider</c> 实现决定；默认仅本地（LocalOnly）。
/// </summary>
public sealed class CollaborationSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string Provider { get; set; } = "local";
    public string Owner { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<CollaborationMember> Members { get; set; } = new();
    /// <summary>最近同步状态描述（供 UI 展示）。</summary>
    public string SyncState { get; set; } = "未同步";

    public string MemberLabel => Members.Count == 0 ? "仅本人" : $"{Members.Count} 名成员";
}

// ========================= 插件系统 =========================
public enum PluginState
{
    Loaded,
    Disabled,
    Failed
}

public sealed class PluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string TypeName { get; set; } = "";
    public PluginState State { get; set; } = PluginState.Disabled;
    public bool Enabled { get; set; }
    public string Error { get; set; } = "";

    public string StateLabel => State switch
    {
        PluginState.Loaded => "已加载",
        PluginState.Disabled => "已禁用",
        PluginState.Failed => "加载失败",
        _ => "未知"
    };
}
