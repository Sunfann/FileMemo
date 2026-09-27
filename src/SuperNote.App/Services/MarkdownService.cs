using Markdig;
using SuperNote.App.Data;
using System.Text.RegularExpressions;

namespace SuperNote.App.Services;

/// <summary>
/// Markdown 渲染（需求 3.1 富文本 + Markdown 双模式）。
/// 同时负责解析 [[双向链接]] 语法并写入 record_link（需求 3.9）。
/// </summary>
public static class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePipeTables()
        .UseTaskLists()
        .Build();

    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        return Markdown.ToHtml(markdown, Pipeline);
    }

    private static readonly Regex LinkRegex = new(@"\[\[(.+?)\]\]", RegexOptions.Compiled);

    public static List<string> ExtractLinkTargets(string markdown)
    {
        var list = new List<string>();
        foreach (Match m in LinkRegex.Matches(markdown ?? ""))
            list.Add(m.Groups[1].Value.Trim());
        return list;
    }

    /// <summary>保存便签时同步反向链接索引。</summary>
    public static void SyncBacklinks(Repository repo, string fromRecordId, string markdown)
    {
        foreach (var target in ExtractLinkTargets(markdown))
        {
            // 以标题文本作为链接目标键（MVP 简化：按 title 匹配）
            var id = repo.GetNotes().FirstOrDefault(n =>
                string.Equals(n.Title, target, StringComparison.OrdinalIgnoreCase))?.Id;
            if (id != null) repo.AddLink(fromRecordId, id, "wiki");
        }
    }
}
