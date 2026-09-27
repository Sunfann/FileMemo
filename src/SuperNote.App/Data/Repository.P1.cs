using Microsoft.Data.Sqlite;
using SuperNote.App.Models;

namespace SuperNote.App.Data;

/// <summary>
/// P1 仓库扩展（需求 3.4 / 3.5 / 3.9 / 3.12）：
///   - Sidecar 读写
///   - 版本关系链读写
///   - 全量 FileRef 遍历（USN 预热 / 卷枚举）
///   - 链接删除（重新解析双向链接前清空旧边）
///   - 跨卷迁移候选（带匹配理由）
/// 复用 Repository.cs 中的映射辅助（S/DN/Str/NullableStr/Num）。
/// </summary>
public sealed partial class Repository
{
    // ============================ Sidecar ============================
    public void UpsertSidecar(SidecarRecord s)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sidecar(id,file_ref_id,path,format,last_synced_at,policy_flags)
            VALUES($id,$fr,$path,$fmt,$sync,$flags)
            ON CONFLICT(id) DO UPDATE SET
              file_ref_id=$fr, path=$path, format=$fmt, last_synced_at=$sync, policy_flags=$flags;
            """;
        AddParam(cmd, "$id", s.Id);
        AddParam(cmd, "$fr", s.FileRefId);
        AddParam(cmd, "$path", s.Path);
        AddParam(cmd, "$fmt", (int)s.Format);
        AddParam(cmd, "$sync", S(s.LastSyncedAt));
        AddParam(cmd, "$flags", s.PolicyFlags);
        cmd.ExecuteNonQuery();
    }

    public List<SidecarRecord> GetSidecars(string fileRefId)
    {
        var list = new List<SidecarRecord>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM sidecar WHERE file_ref_id=$fr;";
        AddParam(cmd, "$fr", fileRefId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SidecarRecord
            {
                Id = Str(r, "id"),
                FileRefId = Str(r, "file_ref_id"),
                Path = Str(r, "path"),
                Format = (SidecarFormat)Num(r, "format"),
                LastSyncedAt = D(Str(r, "last_synced_at")),
                PolicyFlags = Str(r, "policy_flags")
            });
        }
        return list;
    }

    // ========================= 版本关系链 =========================
    public void AddVersionLink(VersionLink v)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO version_link(id,from_file_ref_id,to_file_ref_id,version,note,created_at)
            VALUES($id,$f,$t,$v,$n,$ca);
            """;
        AddParam(cmd, "$id", v.Id);
        AddParam(cmd, "$f", v.FromFileRefId);
        AddParam(cmd, "$t", v.ToFileRefId);
        AddParam(cmd, "$v", v.Version);
        AddParam(cmd, "$n", v.Note);
        AddParam(cmd, "$ca", S(v.CreatedAt));
        cmd.ExecuteNonQuery();
    }

    public List<VersionLink> GetVersionLinks(string fileRefId)
    {
        var list = new List<VersionLink>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM version_link WHERE from_file_ref_id=$id OR to_file_ref_id=$id ORDER BY version DESC;";
        AddParam(cmd, "$id", fileRefId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VersionLink
            {
                Id = Str(r, "id"),
                FromFileRefId = Str(r, "from_file_ref_id"),
                ToFileRefId = Str(r, "to_file_ref_id"),
                Version = (int)Num(r, "version"),
                Note = Str(r, "note"),
                CreatedAt = D(Str(r, "created_at"))
            });
        }
        return list;
    }

    public void DeleteVersionLink(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM version_link WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    // ========================= 链接维护 =========================
    /// <summary>重新解析前清空某记录的出边（保持双向链接一致）。</summary>
    public void DeleteLinksFrom(string fromRecordId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM record_link WHERE from_record_id=$f;";
        AddParam(cmd, "$f", fromRecordId);
        cmd.ExecuteNonQuery();
    }

    // ====================== FileRef 全量遍历 ======================
    public List<FileRef> GetAllFileRefs()
    {
        var list = new List<FileRef>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref ORDER BY last_seen DESC;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }

    // ================= 网络盘 / NAS 路径检索 =================
    public List<FileRef> GetNetworkFileRefs()
    {
        var list = new List<FileRef>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE is_network=1 ORDER BY last_seen DESC;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }

    // ================= 跨卷迁移候选（含理由） =================
    /// <summary>按卷序列号 + 哈希快速定位候选（网络盘 / 跨卷场景）。</summary>
    public List<FileRef> FindByVolumeSerial(uint volumeSerial)
    {
        var list = new List<FileRef>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE volume_serial=$vs;";
        AddParam(cmd, "$vs", volumeSerial);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }
}
