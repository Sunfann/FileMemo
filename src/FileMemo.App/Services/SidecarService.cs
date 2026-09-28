using System.IO;
using System.Text;
using System.Text.Json;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// sidecar 伴生文件（需求 3.12，P1）。
///
/// 移动盘 / NAS 场景下，DataBase 不一定可达或权威；将备注摘要写入目标附近的
/// sidecar 文件（JSON / YAML），随文件一起移动，作为「权威副本」。
///
/// 可配置：是否启用、隐藏 / 可见、同目录 / 集中目录、命名规则、格式。
/// </summary>
public sealed class SidecarService
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;

    public SidecarService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    /// <summary>判断该路径是否属于移动盘 / 网络盘（需要 sidecar 的场景）。</summary>
    public static bool IsRemovableOrNetwork(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)) continue;
                return d.DriveType is DriveType.Removable or DriveType.Network;
            }
        }
        catch { }
        return false;
    }

    /// <summary>给定目标文件，计算其 sidecar 路径；不可用时返回 null。</summary>
    public string? ResolveSidecarPath(FileRef fr)
    {
        if (string.IsNullOrWhiteSpace(fr.Path)) return null;

        string ext = _settings.SidecarFormat == SidecarFormat.Json ? ".json" : ".yaml";
        string fileName = Path.GetFileName(fr.Path) + _settings.SidecarSuffix + ext;

        if (_settings.SidecarPlacement == SidecarPlacement.CentralDirectory)
        {
            var dir = _settings.ResolvedSidecarDir;
            // 用路径哈希避免同名冲突
            string key = IntegerHash(fr.Path).ToString("x8");
            return Path.Combine(dir, $"{key}_{fileName}");
        }

        var targetDir = fr.IsDir ? fr.Path : Path.GetDirectoryName(fr.Path);
        return string.IsNullOrEmpty(targetDir) ? null : Path.Combine(targetDir, fileName);
    }

    /// <summary>写出 sidecar（备注摘要 + 指纹）。返回是否成功。</summary>
    public bool Write(FileRef fr, Annotation? ann)
    {
        if (!_settings.SidecarEnabled) return false;
        var path = ResolveSidecarPath(fr);
        if (path == null) return false;

        try
        {
            var payload = new SidecarPayload
            {
                Schema = "supernote.sidecar/1",
                FilePath = fr.Path,
                VolumeGuid = fr.VolumeGuid,
                FileId = fr.FileId,
                VolumeSerial = fr.VolumeSerial,
                Size = fr.Size,
                MTime = fr.MTime,
                CTime = fr.CTime,
                FullHash = fr.FullHash,
                QuickHash = fr.QuickHash,
                UpdatedAt = DateTime.Now,
                Content = ann?.Content,
                State = ann?.State.ToString(),
                Tags = ann?.Tags,
                Intent = ann?.Intent,
                Source = ann?.Source,
                Owner = ann?.Owner,
                VersionChain = ann?.VersionChain
            };

            string text = _settings.SidecarFormat == SidecarFormat.Json
                ? JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
                : ToYaml(payload);

            if (_settings.SidecarHidden)
                text = "# SuperNote sidecar — 隐藏属性文件，可安全忽略\n" + text;

            File.WriteAllText(path, text, Encoding.UTF8);
            SetHidden(path, _settings.SidecarHidden);

            _repo.UpsertSidecar(new SidecarRecord
            {
                FileRefId = fr.Id,
                Path = path,
                Format = _settings.SidecarFormat,
                LastSyncedAt = DateTime.Now,
                PolicyFlags = (_settings.SidecarHidden ? "hidden;" : "")
            });
            _repo.AddTimeline("file", fr.Id, "sidecar", "已写出 sidecar：" + path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>尝试从目标文件旁边读取 sidecar（用于移动盘插入后恢复备注）。</summary>
    public Annotation? TryRead(string targetPath, out string? sidecarPath)
    {
        sidecarPath = null;
        try
        {
            var dir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            var candidates = Directory.GetFiles(dir,
                Path.GetFileName(targetPath) + _settings.SidecarSuffix + "*");
            if (candidates.Length == 0) return null;

            sidecarPath = candidates[0];
            string text = File.ReadAllText(sidecarPath, Encoding.UTF8);
            var payload = _settings.SidecarFormat == SidecarFormat.Json
                ? JsonSerializer.Deserialize<SidecarPayload>(text)
                : FromYaml(text);
            if (payload == null) return null;

            return new Annotation
            {
                FileRefId = "",
                Content = payload.Content ?? "",
                Tags = payload.Tags ?? "",
                Intent = payload.Intent ?? "",
                Source = payload.Source ?? "",
                Owner = payload.Owner ?? "",
                VersionChain = payload.VersionChain ?? "",
                State = Enum.TryParse<AnnotationState>(payload.State, out var st) ? st : AnnotationState.Draft,
            };
        }
        catch { return null; }
    }

    private static void SetHidden(string path, bool hidden)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            File.SetAttributes(path, hidden ? attrs | FileAttributes.Hidden : attrs & ~FileAttributes.Hidden);
        }
        catch { }
    }

    private static int IntegerHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    // ---- 极简 YAML（避免额外依赖；仅支持本 payload 的标量字段）----
    private static string ToYaml(SidecarPayload p)
    {
        var sb = new StringBuilder();
        void W(string k, object? v) => sb.Append(k).Append(": ").Append(YamlScalar(v)).Append('\n');
        W("schema", p.Schema);
        W("file_path", p.FilePath);
        W("volume_guid", p.VolumeGuid);
        W("file_id", p.FileId);
        W("volume_serial", p.VolumeSerial);
        W("size", p.Size);
        W("mtime", p.MTime);
        W("ctime", p.CTime);
        W("full_hash", p.FullHash);
        W("quick_hash", p.QuickHash);
        W("updated_at", p.UpdatedAt);
        W("content", p.Content);
        W("state", p.State);
        W("tags", p.Tags);
        W("intent", p.Intent);
        W("source", p.Source);
        W("owner", p.Owner);
        W("version_chain", p.VersionChain);
        return sb.ToString();
    }

    private static SidecarPayload FromYaml(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            map[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"');
        }
        string G(string k) => map.TryGetValue(k, out var v) ? v : "";
        return new SidecarPayload
        {
            Schema = G("schema"),
            FilePath = G("file_path"),
            VolumeGuid = G("volume_guid"),
            Content = G("content"),
            State = G("state"),
            Tags = G("tags"),
            Intent = G("intent"),
            Source = G("source"),
            Owner = G("owner"),
            VersionChain = G("version_chain"),
        };
    }

    private static string YamlScalar(object? v)
    {
        if (v == null) return "null";
        var s = v is DateTime dt ? dt.ToString("o") : v.ToString() ?? "";
        if (s.Contains(':') || s.Contains('#') || s.Contains('\n') || s.Contains('"'))
            return "\"" + s.Replace("\"", "\\\"").Replace("\n", " ") + "\"";
        return s;
    }

    /// <summary>sidecar 文件承载的数据结构。</summary>
    public sealed class SidecarPayload
    {
        public string Schema { get; set; } = "supernote.sidecar/1";
        public string? FilePath { get; set; }
        public string? VolumeGuid { get; set; }
        public long? FileId { get; set; }
        public uint? VolumeSerial { get; set; }
        public long? Size { get; set; }
        public DateTime? MTime { get; set; }
        public DateTime? CTime { get; set; }
        public string? FullHash { get; set; }
        public string? QuickHash { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public string? Content { get; set; }
        public string? State { get; set; }
        public string? Tags { get; set; }
        public string? Intent { get; set; }
        public string? Source { get; set; }
        public string? Owner { get; set; }
        public string? VersionChain { get; set; }
    }
}
