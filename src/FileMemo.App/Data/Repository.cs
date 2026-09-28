using System.Globalization;
using Microsoft.Data.Sqlite;
using FileMemo.App.Models;

namespace FileMemo.App.Data;

/// <summary>
/// 数据访问仓库。统一对象模型 CRUD + FTS5 全文检索 + 时间线 + 双向链接。
/// 所有日期以 ISO-8601 文本存储，便于排序与跨平台。
/// </summary>
public sealed partial class Repository
{
    private readonly Database _db;
    private readonly bool _hasFts;

    public Repository(Database db)
    {
        _db = db;
        _hasFts = ProbeFts();
    }

    private bool ProbeFts()
    {
        try
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='record_fts';";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }

    private static string S(DateTime? d) => d?.ToString("o", CultureInfo.InvariantCulture) ?? "";
    private static string S(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
    private static DateTime D(string? s) =>
        string.IsNullOrEmpty(s) ? default : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTime? DN(string? s) => string.IsNullOrEmpty(s) ? null : D(s);

    private static void AddParam(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    // ===================================================================
    //  便签 / 统一记录
    // ===================================================================
    public List<NoteRecord> GetNotes(string? search = null, string? tag = null)
    {
        var list = new List<NoteRecord>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // 关键词命中：优先 FTS5，降级 LIKE
            var ids = SearchNoteIds(c, search!);
            if (ids.Count == 0) return list;
            var inClause = string.Join(",", ids.Select((_, i) => "$id" + i));
            for (int i = 0; i < ids.Count; i++) AddParam(cmd, "$id" + i, ids[i]);
            cmd.CommandText = $"SELECT * FROM note_record WHERE kind={(int)RecordKind.Note} AND id IN ({inClause}) ORDER BY pinned DESC, updated_at DESC";
        }
        else
        {
            cmd.CommandText = $"SELECT * FROM note_record WHERE kind={(int)RecordKind.Note} ORDER BY pinned DESC, updated_at DESC";
        }
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = MapNote(r);
            if (tag != null && !n.Tags.Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(n);
        }
        return list;
    }

    private List<string> SearchNoteIds(SqliteConnection c, string q)
    {
        var result = new List<string>();
        try
        {
            using var cmd = c.CreateCommand();
            if (_hasFts)
            {
                cmd.CommandText = "SELECT record_id FROM record_fts WHERE record_fts MATCH $q";
                AddParam(cmd, "$q", BuildFtsQuery(q));
            }
            else
            {
                cmd.CommandText = "SELECT id FROM note_record WHERE title LIKE $q OR content_md LIKE $q OR tags LIKE $q";
                AddParam(cmd, "$q", "%" + q + "%");
            }
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }
        catch { /* FTS 语法异常时忽略，返回空由调用方降级 */ }
        return result;
    }

    private static string BuildFtsQuery(string raw)
    {
        // 将用户输入拆成安全的前缀词，避免 FTS5 语法注入
        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Replace("\"", ""))
            .Where(p => p.Length > 0)
            .Select(p => $"\"{p}\"*");
        return string.Join(" AND ", parts);
    }

    public void UpsertNote(NoteRecord n)
    {
        n.UpdatedAt = DateTime.Now;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_record(id,kind,title,content_md,content_html,color,tags,pinned,created_at,updated_at,file_ref_id)
            VALUES($id,$kind,$title,$md,$html,$color,$tags,$pin,$ca,$ua,$fr)
            ON CONFLICT(id) DO UPDATE SET
              title=$title, content_md=$md, content_html=$html, color=$color, tags=$tags,
              pinned=$pin, updated_at=$ua, file_ref_id=$fr;
            """;
        AddParam(cmd, "$id", n.Id);
        AddParam(cmd, "$kind", (int)n.Kind);
        AddParam(cmd, "$title", n.Title);
        AddParam(cmd, "$md", n.ContentMd);
        AddParam(cmd, "$html", n.ContentHtml);
        AddParam(cmd, "$color", n.Color);
        AddParam(cmd, "$tags", n.Tags);
        AddParam(cmd, "$pin", n.Pinned ? 1 : 0);
        AddParam(cmd, "$ca", S(n.CreatedAt));
        AddParam(cmd, "$ua", S(n.UpdatedAt));
        AddParam(cmd, "$fr", n.FileRefId);
        cmd.ExecuteNonQuery();
        IndexFts(c, n.Id, n.Title, n.ContentMd, n.Tags);
    }

    private void IndexFts(SqliteConnection c, string id, string title, string content, string tags)
    {
        if (!_hasFts) return;
        try
        {
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM record_fts WHERE record_id=$id;";
            AddParam(del, "$id", id);
            del.ExecuteNonQuery();
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO record_fts(record_id,title,content,tags) VALUES($id,$t,$c,$g);";
            AddParam(ins, "$id", id);
            AddParam(ins, "$t", title);
            AddParam(ins, "$c", content);
            AddParam(ins, "$g", tags);
            ins.ExecuteNonQuery();
        }
        catch { /* FTS 索引失败不影响主库 */ }
    }

    public void DeleteNote(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM note_record WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
        if (_hasFts)
        {
            using var f = c.CreateCommand();
            f.CommandText = "DELETE FROM record_fts WHERE record_id=$id;";
            AddParam(f, "$id", id);
            f.ExecuteNonQuery();
        }
    }

    private static NoteRecord MapNote(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Kind = (RecordKind)r.GetInt32(r.GetOrdinal("kind")),
        Title = Str(r, "title"),
        ContentMd = Str(r, "content_md"),
        ContentHtml = Str(r, "content_html"),
        Color = Str(r, "color"),
        Tags = Str(r, "tags"),
        Pinned = Num(r, "pinned") == 1,
        CreatedAt = D(Str(r, "created_at")),
        UpdatedAt = D(Str(r, "updated_at")),
        FileRefId = NullableStr(r, "file_ref_id")
    };

    // ===================================================================
    //  剪贴板
    // ===================================================================
    public List<Clip> GetClips(string? search = null, int limit = 500)
    {
        var list = new List<Clip>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var where = string.IsNullOrWhiteSpace(search) ? "" : "WHERE content LIKE $q OR source_app LIKE $q OR ocr_text LIKE $q";
        cmd.CommandText = $"SELECT * FROM clip {where} ORDER BY pinned DESC, created_at DESC LIMIT $lim";
        if (!string.IsNullOrWhiteSpace(search)) AddParam(cmd, "$q", "%" + search + "%");
        AddParam(cmd, "$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Clip
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Kind = (ClipKind)r.GetInt32(r.GetOrdinal("kind")),
                Content = Str(r, "content"),
                ImagePath = NullableStr(r, "image_path"),
                SourceApp = Str(r, "source_app"),
                CreatedAt = D(Str(r, "created_at")),
                Pinned = Num(r, "pinned") == 1,
                ExpireAt = DN(Str(r, "expire_at")),
                ContentHash = Str(r, "content_hash"),
                OcrText = Str(r, "ocr_text")
            });
        }
        return list;
    }

    public void InsertClip(Clip clip)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clip(id,kind,content,image_path,source_app,created_at,pinned,expire_at,content_hash,ocr_text)
            VALUES($id,$kind,$content,$img,$app,$ca,$pin,$exp,$hash,$ocr);
            """;
        AddParam(cmd, "$id", clip.Id);
        AddParam(cmd, "$kind", (int)clip.Kind);
        AddParam(cmd, "$content", clip.Content);
        AddParam(cmd, "$img", clip.ImagePath);
        AddParam(cmd, "$app", clip.SourceApp);
        AddParam(cmd, "$ca", S(clip.CreatedAt));
        AddParam(cmd, "$pin", clip.Pinned ? 1 : 0);
        AddParam(cmd, "$exp", S(clip.ExpireAt));
        AddParam(cmd, "$hash", clip.ContentHash);
        AddParam(cmd, "$ocr", clip.OcrText);
        cmd.ExecuteNonQuery();
    }

    public bool ClipHashExists(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM clip WHERE content_hash=$h;";
        AddParam(cmd, "$h", hash);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void SetClipPinned(string id, bool pinned)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE clip SET pinned=$p WHERE id=$id;";
        AddParam(cmd, "$p", pinned ? 1 : 0);
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteClip(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM clip WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TrimClips(int keep)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM clip WHERE pinned=0 AND id NOT IN (
              SELECT id FROM clip WHERE pinned=0 ORDER BY created_at DESC LIMIT $keep
            );
            """;
        AddParam(cmd, "$keep", keep);
        cmd.ExecuteNonQuery();
    }

    // ===================================================================
    //  待办
    // ===================================================================
    public List<TaskItem> GetTasks(string? fileRefId = null)
    {
        var list = new List<TaskItem>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var where = fileRefId != null ? "WHERE file_ref_id=$fr" : "";
        cmd.CommandText = $"SELECT * FROM task {where} ORDER BY done ASC, " +
                          "CASE WHEN due_at='' THEN 1 ELSE 0 END, due_at ASC, created_at DESC";
        if (fileRefId != null) AddParam(cmd, "$fr", fileRefId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TaskItem
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Title = Str(r, "title"),
                Description = Str(r, "description"),
                State = (TaskState)Num(r, "state"),
                Done = Num(r, "done") == 1,
                Priority = (Priority)Num(r, "priority"),
                DueAt = DN(Str(r, "due_at")),
                RepeatRule = Str(r, "repeat_rule"),
                Progress = (int)Num(r, "progress"),
                Owner = Str(r, "owner"),
                Tags = Str(r, "tags"),
                FileRefId = NullableStr(r, "file_ref_id"),
                LinkedRecordId = NullableStr(r, "linked_record_id"),
                CreatedAt = D(Str(r, "created_at"))
            });
        }
        return list;
    }

    public void UpsertTask(TaskItem t)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task(id,title,description,state,done,priority,due_at,repeat_rule,progress,owner,tags,file_ref_id,linked_record_id,created_at)
            VALUES($id,$title,$desc,$state,$done,$pri,$due,$rep,$prog,$owner,$tags,$fr,$lr,$ca)
            ON CONFLICT(id) DO UPDATE SET
              title=$title, description=$desc, state=$state, done=$done, priority=$pri,
              due_at=$due, repeat_rule=$rep, progress=$prog, owner=$owner, tags=$tags,
              file_ref_id=$fr, linked_record_id=$lr;
            """;
        AddParam(cmd, "$id", t.Id);
        AddParam(cmd, "$title", t.Title);
        AddParam(cmd, "$desc", t.Description);
        AddParam(cmd, "$state", (int)t.State);
        AddParam(cmd, "$done", t.Done ? 1 : 0);
        AddParam(cmd, "$pri", (int)t.Priority);
        AddParam(cmd, "$due", S(t.DueAt));
        AddParam(cmd, "$rep", t.RepeatRule);
        AddParam(cmd, "$prog", t.Progress);
        AddParam(cmd, "$owner", t.Owner);
        AddParam(cmd, "$tags", t.Tags);
        AddParam(cmd, "$fr", t.FileRefId);
        AddParam(cmd, "$lr", t.LinkedRecordId);
        AddParam(cmd, "$ca", S(t.CreatedAt));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTask(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM task WHERE id=$id;";
        AddParam(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    // ===================================================================
    //  文件引用 + 备注 + 指纹
    // ===================================================================
    /// <summary>
    /// 按路径查文件引用。使用去除首尾空白 + 大小写不敏感匹配，
    /// 避免资源管理器/Shell 返回的盘符或路径大小写差异导致"已有备注查不到"。
    /// </summary>
    public FileRef? GetFileRefByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE TRIM(path) = $p COLLATE NOCASE LIMIT 1;";
        AddParam(cmd, "$p", p);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapFileRef(r) : null;
    }

    public FileRef? GetFileRefById(string id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE id=$id LIMIT 1;";
        AddParam(cmd, "$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapFileRef(r) : null;
    }

    public List<FileRef> FindByFileId(string? volumeGuid, long? fileId)
    {
        var list = new List<FileRef>();
        if (fileId == null) return list;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE file_id=$fid AND (volume_guid=$vg OR $vg IS NULL);";
        AddParam(cmd, "$fid", fileId);
        AddParam(cmd, "$vg", volumeGuid);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }

    public List<FileRef> FindByHash(string? fullHash, string? quickHash)
    {
        var list = new List<FileRef>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM file_ref
            WHERE ($fh <> '' AND full_hash=$fh) OR ($qh <> '' AND quick_hash=$qh);
            """;
        AddParam(cmd, "$fh", fullHash ?? "");
        AddParam(cmd, "$qh", quickHash ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }

    /// <summary>按名称相似度 + 大小 + 时间接近度给出跨卷迁移候选（需求 3.5.3）。</summary>
    public List<(FileRef candidate, double score)> RecommendMigrationCandidates(string name, long? size, DateTime? mtime)
    {
        var result = new List<(FileRef, double)>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE is_dir=0;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var fr = MapFileRef(r);
            double score = 0;
            var candName = System.IO.Path.GetFileName(fr.Path);
            score += Similarity(candName, name) * 0.5;
            if (size != null && fr.Size != null && size > 0)
                score += (1.0 - Math.Min(1.0, Math.Abs(fr.Size.Value - size.Value) / (double)size.Value)) * 0.25;
            if (mtime != null && fr.MTime != null)
            {
                var days = Math.Abs((fr.MTime.Value - mtime.Value).TotalDays);
                score += Math.Max(0, 1.0 - days / 7.0) * 0.25;
            }
            if (score >= 0.5) result.Add((fr, score));
        }
        return result.OrderByDescending(x => x.Item2).Take(10).ToList();
    }

    private static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        if (a == b) return 1;
        // 简化的 Dice 系数（二元组）
        var sa = Bigrams(a); var sb = Bigrams(b);
        if (sa.Count == 0 || sb.Count == 0) return 0;
        int inter = sa.Intersect(sb).Count();
        return 2.0 * inter / (sa.Count + sb.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        for (int i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
        if (set.Count == 0) set.Add(s);
        return set;
    }

    public FileRef UpsertFileRef(FileRef fr)
    {
        fr.LastSeen = DateTime.Now;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_ref(id,volume_guid,file_id,usn,path,size,mtime,ctime,quick_hash,full_hash,parent_file_id,ext,is_dir,offline,last_seen,volume_serial,is_network)
            VALUES($id,$vg,$fid,$usn,$path,$size,$mtime,$ctime,$qh,$fh,$pfid,$ext,$isdir,$off,$ls,$vs,$net)
            ON CONFLICT(id) DO UPDATE SET
              volume_guid=$vg, file_id=$fid, usn=$usn, path=$path, size=$size, mtime=$mtime, ctime=$ctime,
              quick_hash=$qh, full_hash=$fh, parent_file_id=$pfid, ext=$ext, is_dir=$isdir, offline=$off, last_seen=$ls,
              volume_serial=$vs, is_network=$net;
            """;
        AddParam(cmd, "$id", fr.Id);
        AddParam(cmd, "$vg", fr.VolumeGuid);
        AddParam(cmd, "$fid", fr.FileId);
        AddParam(cmd, "$usn", fr.Usn);
        AddParam(cmd, "$path", fr.Path);
        AddParam(cmd, "$size", fr.Size);
        AddParam(cmd, "$mtime", S(fr.MTime));
        AddParam(cmd, "$ctime", S(fr.CTime));
        AddParam(cmd, "$qh", fr.QuickHash);
        AddParam(cmd, "$fh", fr.FullHash);
        AddParam(cmd, "$pfid", fr.ParentFileId);
        AddParam(cmd, "$ext", fr.Ext);
        AddParam(cmd, "$isdir", fr.IsDir ? 1 : 0);
        AddParam(cmd, "$off", fr.Offline ? 1 : 0);
        AddParam(cmd, "$ls", S(fr.LastSeen));
        AddParam(cmd, "$vs", fr.VolumeSerial);
        AddParam(cmd, "$net", fr.IsNetwork ? 1 : 0);
        cmd.ExecuteNonQuery();
        return fr;
    }

    public Annotation? GetAnnotation(string fileRefId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM annotation WHERE file_ref_id=$fr LIMIT 1;";
        AddParam(cmd, "$fr", fileRefId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapAnnotation(r) : null;
    }

    public List<Annotation> GetAllAnnotations()
    {
        var list = new List<Annotation>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM annotation ORDER BY updated_at DESC;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapAnnotation(r));
        return list;
    }

    public void UpsertAnnotation(Annotation a)
    {
        a.UpdatedAt = DateTime.Now;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO annotation(id,file_ref_id,content,images_json,tags,state,intent,source,owner,due_at,created_at,updated_at,version_chain,ocr_text)
            VALUES($id,$fr,$content,$imgs,$tags,$state,$intent,$src,$owner,$due,$ca,$ua,$vc,$ocr)
            ON CONFLICT(id) DO UPDATE SET
              content=$content, images_json=$imgs, tags=$tags, state=$state, intent=$intent,
              source=$src, owner=$owner, due_at=$due, updated_at=$ua, version_chain=$vc, ocr_text=$ocr;
            """;
        AddParam(cmd, "$id", a.Id);
        AddParam(cmd, "$fr", a.FileRefId);
        AddParam(cmd, "$content", a.Content);
        AddParam(cmd, "$imgs", a.ImagesJson);
        AddParam(cmd, "$tags", a.Tags);
        AddParam(cmd, "$state", (int)a.State);
        AddParam(cmd, "$intent", a.Intent);
        AddParam(cmd, "$src", a.Source);
        AddParam(cmd, "$owner", a.Owner);
        AddParam(cmd, "$due", S(a.DueAt));
        AddParam(cmd, "$ca", S(a.CreatedAt));
        AddParam(cmd, "$ua", S(a.UpdatedAt));
        AddParam(cmd, "$vc", a.VersionChain);
        AddParam(cmd, "$ocr", a.OcrText);
        cmd.ExecuteNonQuery();
    }

    public void SaveFingerprints(string fileRefId, IEnumerable<Fingerprint> fps)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM fingerprint WHERE file_ref_id=$fr;";
            AddParam(del, "$fr", fileRefId);
            del.ExecuteNonQuery();
        }
        foreach (var fp in fps)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO fingerprint(id,file_ref_id,type,value,confidence) VALUES($id,$fr,$t,$v,$c);";
            AddParam(cmd, "$id", fp.Id);
            AddParam(cmd, "$fr", fileRefId);
            AddParam(cmd, "$t", (int)fp.Type);
            AddParam(cmd, "$v", fp.Value);
            AddParam(cmd, "$c", fp.Confidence);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<Fingerprint> GetFingerprints(string fileRefId)
    {
        var list = new List<Fingerprint>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM fingerprint WHERE file_ref_id=$fr;";
        AddParam(cmd, "$fr", fileRefId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Fingerprint
            {
                Id = r.GetString(r.GetOrdinal("id")),
                FileRefId = r.GetString(r.GetOrdinal("file_ref_id")),
                Type = (FingerprintType)r.GetInt32(r.GetOrdinal("type")),
                Value = Str(r, "value"),
                Confidence = r.GetDouble(r.GetOrdinal("confidence"))
            });
        return list;
    }

    /// <summary>文件名搜索（内置轻量索引，用于无 Everything 时的降级）。</summary>
    public List<FileRef> SearchFileRefs(string term, int limit = 200)
    {
        var list = new List<FileRef>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_ref WHERE path LIKE $q ORDER BY last_seen DESC LIMIT $lim;";
        AddParam(cmd, "$q", "%" + term + "%");
        AddParam(cmd, "$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapFileRef(r));
        return list;
    }

    // ===================================================================
    //  时间线 + 链接
    // ===================================================================
    public void AddTimeline(string objType, string objId, string action, string detail)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO timeline(id,object_type,object_id,action,detail,created_at) VALUES($id,$t,$o,$a,$d,$ca);";
        AddParam(cmd, "$id", Guid.NewGuid().ToString("N"));
        AddParam(cmd, "$t", objType);
        AddParam(cmd, "$o", objId);
        AddParam(cmd, "$a", action);
        AddParam(cmd, "$d", detail);
        AddParam(cmd, "$ca", S(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    public List<TimelineEntry> GetTimeline(string objId, int limit = 100)
    {
        var list = new List<TimelineEntry>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM timeline WHERE object_id=$o ORDER BY created_at DESC LIMIT $lim;";
        AddParam(cmd, "$o", objId);
        AddParam(cmd, "$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TimelineEntry
            {
                Id = r.GetString(r.GetOrdinal("id")),
                ObjectType = Str(r, "object_type"),
                ObjectId = Str(r, "object_id"),
                Action = Str(r, "action"),
                Detail = Str(r, "detail"),
                CreatedAt = D(Str(r, "created_at"))
            });
        return list;
    }

    public void AddLink(string from, string to, string type)
    {
        if (from == to) return;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO record_link(id,from_record_id,to_record_id,link_type) VALUES($id,$f,$t,$lt);";
        AddParam(cmd, "$id", Guid.NewGuid().ToString("N"));
        AddParam(cmd, "$f", from);
        AddParam(cmd, "$t", to);
        AddParam(cmd, "$lt", type);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetBacklinks(string recordId)
    {
        var list = new List<string>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT from_record_id FROM record_link WHERE to_record_id=$id;";
        AddParam(cmd, "$id", recordId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ===================================================================
    //  映射辅助
    // ===================================================================
    private static string Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : r.GetValue(i).ToString() ?? "";
    }

    private static string? NullableStr(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    }

    private static long Num(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
    }

    private static FileRef MapFileRef(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        VolumeGuid = NullableStr(r, "volume_guid"),
        FileId = r.IsDBNull(r.GetOrdinal("file_id")) ? null : r.GetInt64(r.GetOrdinal("file_id")),
        Usn = r.IsDBNull(r.GetOrdinal("usn")) ? null : r.GetInt64(r.GetOrdinal("usn")),
        Path = Str(r, "path"),
        Size = r.IsDBNull(r.GetOrdinal("size")) ? null : r.GetInt64(r.GetOrdinal("size")),
        MTime = DN(Str(r, "mtime")),
        CTime = DN(Str(r, "ctime")),
        QuickHash = NullableStr(r, "quick_hash"),
        FullHash = NullableStr(r, "full_hash"),
        ParentFileId = r.IsDBNull(r.GetOrdinal("parent_file_id")) ? null : r.GetInt64(r.GetOrdinal("parent_file_id")),
        VolumeSerial = r.IsDBNull(r.GetOrdinal("volume_serial")) ? null : (uint)r.GetInt64(r.GetOrdinal("volume_serial")),
        IsNetwork = Num(r, "is_network") == 1,
        Ext = Str(r, "ext"),
        IsDir = Num(r, "is_dir") == 1,
        Offline = Num(r, "offline") == 1,
        LastSeen = D(Str(r, "last_seen"))
    };

    private static Annotation MapAnnotation(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        FileRefId = Str(r, "file_ref_id"),
        Content = Str(r, "content"),
        ImagesJson = Str(r, "images_json"),
        Tags = Str(r, "tags"),
        State = (AnnotationState)Num(r, "state"),
        Intent = Str(r, "intent"),
        Source = Str(r, "source"),
        Owner = Str(r, "owner"),
        DueAt = DN(Str(r, "due_at")),
        CreatedAt = D(Str(r, "created_at")),
        UpdatedAt = D(Str(r, "updated_at")),
        VersionChain = Str(r, "version_chain"),
        OcrText = Str(r, "ocr_text")
    };
}
