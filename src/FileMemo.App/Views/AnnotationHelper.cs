using FileMemo.App.Data;
using FileMemo.App.Models;
using FileMemo.App.Services;

namespace FileMemo.App.Views;

/// <summary>文件备注创建/获取的统一入口，负责指纹采集与初建。</summary>
public static class AnnotationHelper
{
    public static FileRef CreateOrGet(Repository repo, string path)
    {
        var existing = repo.GetFileRefByPath(path);
        if (existing != null)
        {
            if (repo.GetAnnotation(existing.Id) == null)
                repo.UpsertAnnotation(new Annotation { FileRefId = existing.Id });
            return existing;
        }

        var fp = new FingerprintService(repo);
        var fr = fp.BuildFileRef(path);
        repo.UpsertFileRef(fr);
        fp.SaveFingerprints(fr);
        repo.UpsertAnnotation(new Annotation { FileRefId = fr.Id });
        repo.AddTimeline("file", fr.Id, "annotated", "新增文件备注：" + path);
        return fr;
    }
}
