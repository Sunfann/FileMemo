using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileMemo.App.Models;

/// <summary>所有实体的轻量通知基类，供 UI 绑定刷新。</summary>
public abstract class ObservableEntity : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>统一记录（便签 / 剪贴板 / 待办 / 文件备注的公共外壳）。</summary>
public sealed class NoteRecord : ObservableEntity
{
    private string _title = "";
    private string _contentMd = "";
    private string _tags = "";
    private bool _pinned;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecordKind Kind { get; set; } = RecordKind.Note;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string ContentMd { get => _contentMd; set => Set(ref _contentMd, value); }
    public string ContentHtml { get; set; } = "";
    public string Color { get; set; } = "";
    public string Tags { get => _tags; set => Set(ref _tags, value); }
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>主对象关联（文件备注 / 待办挂靠的文件引用）。</summary>
    public string? FileRefId { get; set; }
}

/// <summary>剪贴板片段。</summary>
public sealed class Clip : ObservableEntity
{
    private string _content = "";
    private bool _pinned;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; } = ClipKind.Text;
    public string Content { get => _content; set => Set(ref _content, value); }
    public string? ImagePath { get; set; }

    /// <summary>是否为图片片段（用于详情面板切换到图片预览）。</summary>
    public bool IsImage => Kind == ClipKind.Image;
    public string SourceApp { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }
    public DateTime? ExpireAt { get; set; }
    public string ContentHash { get; set; } = "";
    /// <summary>图片 OCR 识别出的文本，参与统一搜索（P1）。</summary>
    public string OcrText { get; set; } = "";

    public string Preview => Kind switch
    {
        ClipKind.Image => "[图片]",
        ClipKind.FileDrop => "[文件] " + Content,
        _ => Content.Length > 120 ? Content[..120].Replace('\n', ' ') : Content.Replace('\n', ' ')
    };
    public string KindLabel => Kind switch
    {
        ClipKind.Text => "文本",
        ClipKind.Html => "HTML",
        ClipKind.Rtf => "富文本",
        ClipKind.Image => "图片",
        ClipKind.FileDrop => "文件",
        _ => "文本"
    };
}

/// <summary>待办事项。</summary>
public sealed class TaskItem : ObservableEntity
{
    private string _title = "";
    private TaskState _state = TaskState.NotStarted;
    private bool _done;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _description = "";
    /// <summary>描述：支持 Markdown 图片语法 ![alt](path)，更改时联动刷新图片预览。</summary>
    public string Description
    {
        get => _description;
        set { if (Set(ref _description, value)) { Raise(nameof(ImagePaths)); Raise(nameof(HasImages)); } }
    }

    /// <summary>从描述中解析出的本地图片路径（存在才返回），供插入图片后的预览。</summary>
    public System.Collections.Generic.List<string> ImagePaths
    {
        get
        {
            var list = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(_description)) return list;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(_description, @"!\[[^\]]*\]\(([^)]+)\)"))
            {
                var p = m.Groups[1].Value.Trim().Replace("file:///", "");
                if (System.IO.File.Exists(p)) list.Add(p);
            }
            return list;
        }
    }

    public bool HasImages => ImagePaths.Count > 0;

    public TaskState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateLabel)); } }
    public bool Done
    {
        get => _done;
        set { if (Set(ref _done, value)) { State = value ? TaskState.Done : TaskState.NotStarted; } }
    }
    public Priority Priority { get; set; } = Priority.Normal;
    public DateTime? DueAt { get; set; }
    public string RepeatRule { get; set; } = "";
    public int Progress { get; set; }
    public string Owner { get; set; } = "";
    public string Tags { get; set; } = "";

    /// <summary>挂靠的文件 / 文件夹引用（文件备注上的待办）。</summary>
    public string? FileRefId { get; set; }
    public string? LinkedRecordId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string StateLabel => Models.StateDefaults.Display(State);
    public string PriorityLabel => Priority switch
    {
        Priority.Low => "低",
        Priority.Normal => "中",
        Priority.High => "高",
        Priority.Urgent => "紧急",
        _ => "中"
    };
    public string DueLabel => DueAt?.ToString("MM-dd HH:mm") ?? "";
}

/// <summary>文件 / 文件夹的逻辑引用，承载多重指纹。</summary>
public sealed class FileRef : ObservableEntity
{
    private string _path = "";
    private string? _volumeGuid;
    private long? _fileId;
    private long? _size;
    private DateTime? _mtime;
    private DateTime? _ctime;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? VolumeGuid { get => _volumeGuid; set => Set(ref _volumeGuid, value); }
    public long? FileId { get => _fileId; set => Set(ref _fileId, value); }
    public long? Usn { get; set; }
    public string Path { get => _path; set => Set(ref _path, value); }
    public long? Size { get => _size; set => Set(ref _size, value); }
    public DateTime? MTime { get => _mtime; set => Set(ref _mtime, value); }
    public DateTime? CTime { get => _ctime; set => Set(ref _ctime, value); }
    public string? QuickHash { get; set; }
    public string? FullHash { get; set; }
    public long? ParentFileId { get; set; }
    /// <summary>卷序列号：非 NTFS / 网络盘 / NAS 场景的辅助指纹（P1）。</summary>
    public uint? VolumeSerial { get; set; }
    /// <summary>是否为网络盘 / NAS（影响 sidecar 与追踪策略，P1）。</summary>
    public bool IsNetwork { get; set; }
    public string Ext { get; set; } = "";
    public bool IsDir { get; set; }
    public bool Offline { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;

    public string Name => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public string SizeLabel => IsDir ? "文件夹" : FormatSize(Size);

    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "";
        double b = bytes.Value;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}

/// <summary>备注本体：文件 / 文件夹上的图文注释、状态、标签、时间线入口。</summary>
public sealed class Annotation : ObservableEntity
{
    private string _content = "";
    private AnnotationState _state = AnnotationState.Draft;
    private string _tags = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileRefId { get; set; } = "";
    public string Content { get => _content; set => Set(ref _content, value); }
    public string ImagesJson { get; set; } = "";    // 图片附件路径数组的 JSON
    public string Tags { get => _tags; set => Set(ref _tags, value); }
    public AnnotationState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateLabel)); } }
    public string Intent { get; set; } = "";        // 为什么保存它
    public string Source { get; set; } = "";        // 从何而来
    public string Owner { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>版本链：同一逻辑文件的不同版本，逗号分隔的 FileRefId。</summary>
    public string VersionChain { get; set; } = "";

    /// <summary>插入图片 OCR 出的文本，参与备注搜索（P1）。</summary>
    public string OcrText { get; set; } = "";

    public string StateLabel => Models.StateDefaults.Display(State);
}

/// <summary>指纹：用于文件移动 / 重命名后的备注自动同步。</summary>
public sealed class Fingerprint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileRefId { get; set; } = "";
    public FingerprintType Type { get; set; }
    public string Value { get; set; } = "";
    public double Confidence { get; set; } = 1.0;
}

/// <summary>时间线事件。</summary>
public sealed class TimelineEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>记录之间的双向链接 [[...]]。</summary>
public sealed class RecordLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromRecordId { get; set; } = "";
    public string ToRecordId { get; set; } = "";
    public string LinkType { get; set; } = "ref";
}
