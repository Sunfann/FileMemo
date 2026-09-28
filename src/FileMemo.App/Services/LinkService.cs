using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 双向链接 / 反向链接（需求 3.9，P1）。
///
/// 支持 [[目标标题]] 语法：保存记录时解析出目标，建立 record_link；
/// 打开某条记录时可查询「哪些记录引用了它」——即反向链接面板的数据源。
/// </summary>
public sealed class LinkService
{
    private readonly Repository _repo;

    public LinkService(Repository repo) => _repo = repo;

    /// <summary>解析并同步正向链接（保存便签 / 备注内容后调用）。</summary>
    public int SyncOutgoing(string fromRecordId, string markdownOrText)
    {
        int added = 0;
        try
        {
            _repo.DeleteLinksFrom(fromRecordId);

            foreach (var target in MarkdownService.ExtractLinkTargets(markdownOrText))
            {
                var targetId = ResolveByTitle(target);
                if (targetId == null) continue;
                _repo.AddLink(fromRecordId, targetId, "wiki");
                _repo.AddTimeline("record", fromRecordId, "link-change", $"新增链接 → {target}");
                added++;
            }
        }
        catch { }
        return added;
    }

    /// <summary>解析标题为记录 Id（先精确匹配标题，再匹配文件名）。</summary>
    public string? ResolveByTitle(string title)
    {
        try
        {
            var note = _repo.GetNotes().FirstOrDefault(n =>
                string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));
            if (note != null) return note.Id;

            // 也允许 [[文件名]] 指向文件备注
            var fr = _repo.SearchFileRefs(title, 1).FirstOrDefault();
            if (fr != null) return fr.Id;
        }
        catch { }
        return null;
    }

    /// <summary>反向链接：返回引用了该记录的所有来源记录。</summary>
    public List<NoteRecord> GetBacklinkRecords(string recordId)
    {
        var result = new List<NoteRecord>();
        try
        {
            foreach (var fromId in _repo.GetBacklinks(recordId))
            {
                var n = _repo.GetNotes().FirstOrDefault(x => x.Id == fromId);
                if (n != null) result.Add(n);
            }
        }
        catch { }
        return result;
    }

    /// <summary>反向链接数量（供列表徽标显示）。</summary>
    public int GetBacklinkCount(string recordId)
    {
        try { return _repo.GetBacklinks(recordId).Count; } catch { return 0; }
    }
}
