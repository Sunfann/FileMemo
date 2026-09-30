using FileMemo.App.Data;
using FileMemo.App.Models;
using FileMemo.App.Services;

namespace FileMemo.App.Views;

/// <summary>文件备注创建/获取的统一入口，负责指纹采集与初建。</summary>
public static class AnnotationHelper
{
    /// <summary>
    /// 取得（或创建）某个路径对应的 FileRef。
    /// 查找顺序：
    ///   1) 按路径精确命中 → 直接复用（并补齐缺失的 Annotation）；
    ///   2) 路径未命中时，用多重指纹（File ID / 哈希 / 卷序列号）尝试找回旧 FileRef——
    ///      这样即使文件/文件夹已被移动或重命名、用户再次呼出备注弹窗，也能复用原有备注，
    ///      而不是新建一份重复的"孤儿备注"。
    ///   3) 仍未命中才真正新建。
    /// </summary>
    public static FileRef CreateOrGet(Repository repo, string path)
    {
        var existing = repo.GetFileRefByPath(path);
        if (existing != null)
        {
            if (repo.GetAnnotation(existing.Id) == null)
                repo.UpsertAnnotation(new Annotation { FileRefId = existing.Id });
            EnsureWatched(path);
            return existing;
        }

        var fp = new FingerprintService(repo);

        // 路径未命中：先用指纹找回（处理"移动/重命名后再次备注"的场景）。
        // Resolve 命中时会自动把 FileRef 的 Path 更新为新路径并记录时间线。
        // 保守模式：这里是在为"当前不存在的路径记录"建备注，必须避免把全新文件
        // 误绑到内容恰好相同的旧备注上，因此仅接受强信号（同卷 File ID / 同名同内容）。
        bool targetExists = System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
        if (targetExists)
        {
            try
            {
                var (matched, _) = fp.Resolve(path, conservative: true);
                if (matched != null)
                {
                    if (repo.GetAnnotation(matched.Id) == null)
                        repo.UpsertAnnotation(new Annotation { FileRefId = matched.Id });
                    EnsureWatched(path);
                    return matched;
                }
            }
            catch { /* 指纹解析失败不阻断新建 */ }
        }

        var fr = fp.BuildFileRef(path);
        repo.UpsertFileRef(fr);
        fp.SaveFingerprints(fr);
        repo.UpsertAnnotation(new Annotation { FileRefId = fr.Id });
        repo.AddTimeline("file", fr.Id, "annotated", "新增文件备注：" + path);
        EnsureWatched(path);
        return fr;
    }

    /// <summary>让文件监听服务开始跟踪该路径所在目录，保证后续移动/重命名能实时同步。</summary>
    private static void EnsureWatched(string path)
    {
        try { App.Instance?.Watcher?.EnsureWatchForPath(path); } catch { }
    }
}
