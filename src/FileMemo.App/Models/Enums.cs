namespace FileMemo.App.Models;

/// <summary>统一对象模型：便签、剪贴板、待办、文件备注都是"记录"。</summary>
public enum RecordKind
{
    Note,
    Clip,
    Task,
    FileAnnotation
}

public enum TaskState
{
    NotStarted,
    InProgress,
    Waiting,
    Done,
    Cancelled
}

public enum ClipKind
{
    Text,
    Html,
    Rtf,
    Image,
    FileDrop
}

/// <summary>备注状态（可自定义，默认模板见 docs）。</summary>
public enum AnnotationState
{
    Draft,
    InProgress,
    Reviewing,
    Done,
    Archived,
    Discarded
}

public enum FingerprintType
{
    VolumeGuid,
    NtfsFileId,
    /// <summary>卷序列号：非 NTFS / 网络盘 / NAS 场景的辅助标识（P1）。</summary>
    VolumeSerial,
    Usn,
    Path,
    Size,
    MTime,
    CTime,
    ParentFileId,
    Extension,
    QuickHash,
    FullHash
}

/// <summary>记录之间链接的语义类型（需求 3.9）。</summary>
public enum LinkType
{
    /// <summary>普通引用。</summary>
    Ref,
    /// <summary>[[双向链接]]。</summary>
    Wiki,
    /// <summary>版本关系（同一逻辑文件的不同版本）。</summary>
    Version,
    /// <summary>相关/关联。</summary>
    Related
}

/// <summary>sidecar 伴生文件格式（需求 3.12）。</summary>
public enum SidecarFormat
{
    Json,
    Yaml
}

/// <summary>sidecar 存放策略。</summary>
public enum SidecarPlacement
{
    /// <summary>与目标文件同目录（默认隐藏）。</summary>
    SameDirectory,
    /// <summary>集中到统一目录。</summary>
    CentralDirectory
}

/// <summary>复制文件时的备注继承策略（需求 3.4.6）。</summary>
public enum InheritPolicy
{
    /// <summary>默认询问"是否复制备注"。</summary>
    Ask,
    /// <summary>总是继承。</summary>
    Always,
    /// <summary>总是不继承。</summary>
    Never,
    /// <summary>仅同目录继承。</summary>
    SameDirOnly,
    /// <summary>仅检测到 sidecar 时继承。</summary>
    SidecarOnly
}

/// <summary>云同步提供方（需求 3.12，P1 预留）。</summary>
public enum CloudProvider
{
    None,
    WebDav,
    OneDrive
}

public enum Priority
{
    Low,
    Normal,
    High,
    Urgent
}

/// <summary>时间线事件分类（需求 3.11，可配置）。</summary>
public static class TimelineCategories
{
    public const string AnnotationEdit = "annotation.edit";
    public const string FileRename = "file.rename";
    public const string FileMove = "file.move";
    public const string FileModify = "file.modify";
    public const string FileDelete = "file.delete";
    public const string HashChange = "file.hash";
    public const string TaskChange = "task.change";
    public const string NoteChange = "note.change";
    public const string VersionChange = "version.change";
    public const string LinkChange = "link.change";
    public const string SystemEvent = "system";

    /// <summary>默认仅记录备注编辑 + 文件关键变化（需求 3.11 默认项）。</summary>
    public static readonly string[] DefaultEnabled =
    {
        AnnotationEdit, FileRename, FileMove, FileModify, HashChange, FileDelete
    };

    /// <summary>用户可开启的完整事件流。</summary>
    public static readonly string[] All =
    {
        AnnotationEdit, FileRename, FileMove, FileModify, FileDelete, HashChange,
        TaskChange, NoteChange, VersionChange, LinkChange, SystemEvent
    };
}

public static class StateDefaults
{
    public static readonly string[] States = { "草稿", "进行中", "待审核", "已完成", "已归档", "废弃" };
    public static readonly string[] Tags = { "重要", "待跟进", "财务", "合同", "项目", "个人", "临时" };

    public static string Display(AnnotationState s) => s switch
    {
        AnnotationState.Draft => "草稿",
        AnnotationState.InProgress => "进行中",
        AnnotationState.Reviewing => "待审核",
        AnnotationState.Done => "已完成",
        AnnotationState.Archived => "已归档",
        AnnotationState.Discarded => "废弃",
        _ => "草稿"
    };

    public static string Display(TaskState s) => s switch
    {
        TaskState.NotStarted => "未开始",
        TaskState.InProgress => "进行中",
        TaskState.Waiting => "等待",
        TaskState.Done => "已完成",
        TaskState.Cancelled => "已取消",
        _ => "未开始"
    };

    public static string Display(InheritPolicy p) => p switch
    {
        InheritPolicy.Ask => "每次询问",
        InheritPolicy.Always => "总是继承",
        InheritPolicy.Never => "总是不继承",
        InheritPolicy.SameDirOnly => "仅同目录继承",
        InheritPolicy.SidecarOnly => "仅 sidecar 继承",
        _ => "每次询问"
    };
}
