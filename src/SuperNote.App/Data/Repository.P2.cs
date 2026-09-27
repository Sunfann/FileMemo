using System.Text.Json;
using SuperNote.App.Models;

namespace SuperNote.App.Data;

/// <summary>
/// P2 仓库扩展：图片向量、插件、协作会话的读写。
/// 复用 Repository.cs 中的参数/映射辅助（AddParam / S / Str / Num / D）。
/// </summary>
public sealed partial class Repository
{
    // ========================= 图片向量 =========================
    public void UpsertImageVector(ImageVector v)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO image_vector(id,file_ref_id,image_path,dim,vector,created_at)
            VALUES($id,$fr,$path,$dim,$vec,$ca)
            ON CONFLICT(id) DO UPDATE SET
              file_ref_id=$fr, image_path=$path, dim=$dim, vector=$vec, created_at=$ca;
            """;
        AddParam(cmd, "$id", v.Id);
        AddParam(cmd, "$fr", v.FileRefId);
        AddParam(cmd, "$path", v.ImagePath);
        AddParam(cmd, "$dim", v.Dim);
        AddParam(cmd, "$vec", v.ToStore());
        AddParam(cmd, "$ca", S(v.CreatedAt));
        cmd.ExecuteNonQuery();
    }

    public ImageVector? GetImageVectorByPath(string imagePath)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM image_vector WHERE image_path=$p LIMIT 1;";
        AddParam(cmd, "$p", imagePath);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapVector(r) : null;
    }

    public List<ImageVector> GetAllImageVectors()
    {
        var list = new List<ImageVector>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM image_vector;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapVector(r));
        return list;
    }

    public void DeleteImageVector(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM image_vector WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    private static ImageVector MapVector(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        int dim = (int)Num(r, "dim");
        return new ImageVector
        {
            Id = Str(r, "id"),
            FileRefId = Str(r, "file_ref_id"),
            ImagePath = Str(r, "image_path"),
            Dim = dim,
            Values = ImageVector.FromStore(Str(r, "vector"), dim),
            CreatedAt = D(Str(r, "created_at"))
        };
    }

    // ========================= 插件 =========================
    public void UpsertPlugin(PluginInfo p)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO plugin(id,name,version,author,description,assembly_path,type_name,state,enabled,error)
            VALUES($id,$n,$v,$a,$d,$ap,$tn,$st,$en,$er)
            ON CONFLICT(id) DO UPDATE SET
              name=$n, version=$v, author=$a, description=$d, assembly_path=$ap,
              type_name=$tn, state=$st, enabled=$en, error=$er;
            """;
        AddParam(cmd, "$id", p.Id);
        AddParam(cmd, "$n", p.Name);
        AddParam(cmd, "$v", p.Version);
        AddParam(cmd, "$a", p.Author);
        AddParam(cmd, "$d", p.Description);
        AddParam(cmd, "$ap", p.AssemblyPath);
        AddParam(cmd, "$tn", p.TypeName);
        AddParam(cmd, "$st", (int)p.State);
        AddParam(cmd, "$en", p.Enabled ? 1 : 0);
        AddParam(cmd, "$er", p.Error);
        cmd.ExecuteNonQuery();
    }

    public List<PluginInfo> GetPlugins()
    {
        var list = new List<PluginInfo>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM plugin ORDER BY name;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PluginInfo
            {
                Id = Str(r, "id"),
                Name = Str(r, "name"),
                Version = Str(r, "version"),
                Author = Str(r, "author"),
                Description = Str(r, "description"),
                AssemblyPath = Str(r, "assembly_path"),
                TypeName = Str(r, "type_name"),
                State = (PluginState)Num(r, "state"),
                Enabled = Num(r, "enabled") == 1,
                Error = Str(r, "error")
            });
        }
        return list;
    }

    public void DeletePlugin(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM plugin WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    // ========================= 协作会话 =========================
    public void UpsertCollabSession(CollaborationSession s)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collab_session(id,name,workspace_id,provider,owner,created_at,updated_at,members_json,sync_state)
            VALUES($id,$n,$w,$p,$o,$ca,$ua,$m,$ss)
            ON CONFLICT(id) DO UPDATE SET
              name=$n, workspace_id=$w, provider=$p, owner=$o,
              updated_at=$ua, members_json=$m, sync_state=$ss;
            """;
        AddParam(cmd, "$id", s.Id);
        AddParam(cmd, "$n", s.Name);
        AddParam(cmd, "$w", s.WorkspaceId);
        AddParam(cmd, "$p", s.Provider);
        AddParam(cmd, "$o", s.Owner);
        AddParam(cmd, "$ca", S(s.CreatedAt));
        AddParam(cmd, "$ua", S(s.UpdatedAt));
        AddParam(cmd, "$m", JsonSerializer.Serialize(s.Members));
        AddParam(cmd, "$ss", s.SyncState);
        cmd.ExecuteNonQuery();
    }

    public List<CollaborationSession> GetCollabSessions()
    {
        var list = new List<CollaborationSession>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM collab_session ORDER BY updated_at DESC;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            List<CollaborationMember> members;
            try { members = JsonSerializer.Deserialize<List<CollaborationMember>>(Str(r, "members_json")) ?? new(); }
            catch { members = new(); }

            list.Add(new CollaborationSession
            {
                Id = Str(r, "id"),
                Name = Str(r, "name"),
                WorkspaceId = Str(r, "workspace_id"),
                Provider = Str(r, "provider"),
                Owner = Str(r, "owner"),
                CreatedAt = D(Str(r, "created_at")),
                UpdatedAt = D(Str(r, "updated_at")),
                Members = members,
                SyncState = Str(r, "sync_state")
            });
        }
        return list;
    }

    public void DeleteCollabSession(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM collab_session WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }
}
