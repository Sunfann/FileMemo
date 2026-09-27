using System.IO;
using SuperNote.App.Data;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 副本继承策略（需求 3.4.6，P1）。
///
/// 复制 / 粘贴文件时决定是否把源文件的备注继承给副本：
///   每次询问 / 总是继承 / 总是不继承 / 仅同目录继承 / 仅检测到 sidecar 时继承。
/// </summary>
public sealed class CopyInheritService
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;

    public CopyInheritService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    /// <summary>继承决策结果。</summary>
    public sealed class Decision
    {
        public bool Inherit { get; set; }
        public bool NeedsUserConfirm { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>根据当前策略，判断新副本是否应继承源文件备注。</summary>
    public Decision Decide(string sourcePath, string destPath)
    {
        var d = new Decision();
        try
        {
            var src = _repo.GetFileRefByPath(sourcePath);
            if (src == null)
            {
                d.Reason = "源文件无备注";
                return d;   // 无备注可继承
            }

            switch (_settings.InheritDefault)
            {
                case InheritPolicy.Always:
                    d.Inherit = true;
                    d.Reason = "全局策略：总是继承";
                    break;

                case InheritPolicy.Never:
                    d.Inherit = false;
                    d.Reason = "全局策略：总是不继承";
                    break;

                case InheritPolicy.SameDirOnly:
                    d.Inherit = string.Equals(
                        Path.GetDirectoryName(sourcePath),
                        Path.GetDirectoryName(destPath),
                        StringComparison.OrdinalIgnoreCase);
                    d.Reason = d.Inherit ? "同目录，继承备注" : "非同目录，不继承";
                    break;

                case InheritPolicy.SidecarOnly:
                    var sidecar = src.Path + _settings.SidecarSuffix;
                    d.Inherit = File.Exists(sidecar + ".json") || File.Exists(sidecar + ".yaml");
                    d.Reason = d.Inherit ? "检测到 sidecar，继承备注" : "未检测到 sidecar";
                    break;

                case InheritPolicy.Ask:
                default:
                    d.NeedsUserConfirm = true;
                    d.Reason = "默认询问是否复制备注";
                    break;
            }
        }
        catch (Exception ex) { d.Reason = "决策异常：" + ex.Message; }
        return d;
    }

    /// <summary>用户确认继承后，把源备注复制到目标文件（含指纹重建）。</summary>
    public void Inherit(string sourcePath, string destPath)
    {
        try
        {
            var src = _repo.GetFileRefByPath(sourcePath);
            if (src == null) return;
            var srcAnn = _repo.GetAnnotation(src.Id);
            if (srcAnn == null) return;

            var fp = new FingerprintService(_repo);
            var dest = _repo.GetFileRefByPath(destPath) ?? _repo.UpsertFileRef(fp.BuildFileRef(destPath));

            var copy = new Annotation
            {
                FileRefId = dest.Id,
                Content = srcAnn.Content,
                ImagesJson = srcAnn.ImagesJson,
                Tags = srcAnn.Tags,
                State = srcAnn.State,
                Intent = srcAnn.Intent,
                Source = srcAnn.Source,
                Owner = srcAnn.Owner,
                DueAt = srcAnn.DueAt,
                VersionChain = srcAnn.VersionChain,
            };
            _repo.UpsertAnnotation(copy);
            _repo.AddTimeline("file", dest.Id, "inherit", $"从副本继承备注：{sourcePath} → {destPath}");
        }
        catch { }
    }
}
