using System.IO;
using SuperNote.App.Data;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 版本关系（需求 3.4.5，P1）。
///
/// 手动为主 + 自动推荐：
///   - 用户手动把两个文件标记为「同一逻辑文件的不同版本」→ 建立版本链；
///   - 系统按同目录 / 相似文件名 / 相似大小 / 修改时间接近 推荐候选，用户确认后入链。
/// </summary>
public sealed class VersionService
{
    private readonly Repository _repo;

    public VersionService(Repository repo) => _repo = repo;

    /// <summary>自动推荐候选（复用迁移候选的相似度算法，排除自身）。</summary>
    public List<MigrationCandidate> SuggestCandidates(FileRef target, int max = 8)
    {
        var result = new List<MigrationCandidate>();
        try
        {
            var name = target.Name;
            foreach (var (cand, score) in _repo.RecommendMigrationCandidates(name, target.Size, target.MTime))
            {
                if (cand.Id == target.Id) continue;

                var reason = new List<string>();
                if (string.Equals(Path.GetDirectoryName(cand.Path), Path.GetDirectoryName(target.Path), StringComparison.OrdinalIgnoreCase))
                    reason.Add("同目录");
                if (string.Equals(Path.GetExtension(cand.Path), Path.GetExtension(target.Path), StringComparison.OrdinalIgnoreCase))
                    reason.Add("同扩展名");
                if (target.Size != null && cand.Size != null)
                {
                    double diff = Math.Abs(target.Size.Value - cand.Size.Value) / (double)Math.Max(1, target.Size.Value);
                    if (diff <= 0.1) reason.Add("大小相近");
                }
                if (target.MTime != null && cand.MTime != null &&
                    Math.Abs((target.MTime.Value - cand.MTime.Value).TotalDays) <= 7) reason.Add("时间接近");

                result.Add(new MigrationCandidate
                {
                    Candidate = cand,
                    Score = score,
                    MatchReason = string.Join(" · ", reason)
                });
            }
        }
        catch { }

        return result.OrderByDescending(r => r.Score).Take(max).ToList();
    }

    /// <summary>建立/追加版本关系；version 为递增序号，note 记录变更原因。</summary>
    public VersionLink Link(FileRef from, FileRef to, string note = "", int version = 0)
    {
        if (version <= 0)
        {
            var existing = _repo.GetVersionLinks(from.Id);
            version = existing.Count == 0 ? 1 : existing.Max(v => v.Version) + 1;
        }

        var link = new VersionLink
        {
            FromFileRefId = from.Id,
            ToFileRefId = to.Id,
            Version = version,
            Note = note
        };
        _repo.AddVersionLink(link);

        // 同步写入 annotation.version_chain（逗号分隔）
        TryAppendVersionChain(from.Id, to.Id);
        TryAppendVersionChain(to.Id, from.Id);

        _repo.AddTimeline("file", from.Id, "version-change", $"建立版本关系 v{version}：{to.Name} {note}".Trim());
        return link;
    }

    public void Unlink(string versionLinkId)
    {
        try { _repo.DeleteVersionLink(versionLinkId); } catch { }
    }

    /// <summary>取某文件所在版本链（含自身与所有关联版本）。</summary>
    public List<FileRef> GetVersionChain(FileRef fr)
    {
        var result = new List<FileRef> { fr };
        var seen = new HashSet<string> { fr.Id };
        try
        {
            var queue = new Queue<string>();
            queue.Enqueue(fr.Id);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var v in _repo.GetVersionLinks(id))
                {
                    var otherId = v.FromFileRefId == id ? v.ToFileRefId : v.FromFileRefId;
                    if (seen.Add(otherId))
                    {
                        var other = _repo.GetFileRefById(otherId);
                        if (other != null) { result.Add(other); queue.Enqueue(otherId); }
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private void TryAppendVersionChain(string fileRefId, string otherId)
    {
        try
        {
            var ann = _repo.GetAnnotation(fileRefId);
            if (ann == null) return;
            var parts = (ann.VersionChain ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(otherId)) parts.Add(otherId);
            ann.VersionChain = string.Join(",", parts);
            ann.UpdatedAt = DateTime.Now;
            _repo.UpsertAnnotation(ann);
        }
        catch { }
    }
}
