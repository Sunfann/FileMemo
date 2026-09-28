namespace FileMemo.App.Models;

/// <summary>
/// P1 新增实体：sidecar 伴生文件、版本链、跨卷迁移候选、OCR 结果。
/// 原有 NoteRecord/Clip/TaskItem/FileRef/Annotation/Fingerprint/TimelineEntry/RecordLink
/// 保持不变，仅做字段级扩展（见 Entities.cs）。
/// </summary>

/// <summary>sidecar 伴生文件记录（需求 3.12，P1）：移动盘 / NAS 场景下的权威副本。</summary>
public sealed class SidecarRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileRefId { get; set; } = "";
    public string Path { get; set; } = "";
    public SidecarFormat Format { get; set; } = SidecarFormat.Json;
    public DateTime LastSyncedAt { get; set; } = DateTime.Now;
    public string PolicyFlags { get; set; } = "";
}

/// <summary>版本链（需求 3.4.5，P1）：同一逻辑文件的不同版本之间的关系。</summary>
public sealed class VersionLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromFileRefId { get; set; } = "";
    public string ToFileRefId { get; set; } = "";
    public int Version { get; set; }
    /// <summary>变更原因 / 差异说明。</summary>
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>跨卷迁移候选（需求 3.5.3 / 3.5.5，P1）：携带匹配度与命中理由供用户确认。</summary>
public sealed class MigrationCandidate
{
    public FileRef Candidate { get; set; } = new();
    public double Score { get; set; }
    public string MatchReason { get; set; } = "";

    public string ScoreLabel => $"{Score:P0}";
}

/// <summary>OCR 结果（需求 3.2 / 3.7，P1）。</summary>
public sealed class OcrResult
{
    public bool Success { get; set; }
    public string Text { get; set; } = "";
    public string Language { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>USN Journal 捕获到的单条文件变化（需求 3.5.3，P1）。</summary>
public sealed class UsnChange
{
    public string VolumeRoot { get; set; } = "";
    public string VolumeGuid { get; set; } = "";
    public long FileReferenceNumber { get; set; }
    public long ParentFileReferenceNumber { get; set; }
    public string FileName { get; set; } = "";
    public long Usn { get; set; }
    public uint Reason { get; set; }
    public string? ResolvedPath { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

    public bool IsRename => (Reason & (NativeMethodsUsn.RenameOldName | NativeMethodsUsn.RenameNewName)) != 0;
    public bool IsCreate => (Reason & NativeMethodsUsn.FileCreate) != 0;
    public bool IsDelete => (Reason & NativeMethodsUsn.FileDelete) != 0;
    public bool IsDataChange => (Reason & (NativeMethodsUsn.DataOverwrite | NativeMethodsUsn.DataExtend | NativeMethodsUsn.DataTruncation)) != 0;
}

/// <summary>USN_REASON_* 常量镜像（避免服务层直接依赖 Interop 常量命名）。</summary>
public static class NativeMethodsUsn
{
    public const uint DataOverwrite = 0x00000001;
    public const uint DataExtend = 0x00000002;
    public const uint DataTruncation = 0x00000004;
    public const uint FileCreate = 0x00000100;
    public const uint FileDelete = 0x00000200;
    public const uint RenameOldName = 0x00001000;
    public const uint RenameNewName = 0x00002000;
}
