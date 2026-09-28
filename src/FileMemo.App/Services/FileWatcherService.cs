using System.IO;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 文件变化监听（需求 3.5.3）：
///   - 优先级：USN Journal 实时订阅（P1）——捕获同卷重命名/移动/删除/内容变化，
///     事件携带 NTFS File ID，可精确对齐主指纹；
///   - 回退级：FileSystemWatcher 监听已添加备注文件所在目录（网络盘 / 非 NTFS 场景）。
/// 两者可并存：USN 负责本地固定盘，FileSystemWatcher 兜底网络盘。
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;
    private readonly FingerprintService _fingerprints;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _watchedDirs = new(StringComparer.OrdinalIgnoreCase);
    private UsnJournalService? _usn;

    public FileWatcherService(Repository repo) : this(repo, SettingsService.Load()) { }

    public FileWatcherService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
        _fingerprints = new FingerprintService(repo);
    }

    public void Start()
    {
        // USN Journal 实时订阅（P1）：会自行按卷枚举，失败自动降级
        try
        {
            _usn = new UsnJournalService(_repo, _settings);
            _usn.Start();
        }
        catch { _usn = null; }

        // FileSystemWatcher 兜底：监听所有"已添加备注文件"的父目录
        foreach (var ann in _repo.GetAllAnnotations())
        {
            var fr = _repo.GetFileRefById(ann.FileRefId);
            if (fr == null) continue;
            var dir = fr.IsDir ? fr.Path : System.IO.Path.GetDirectoryName(fr.Path);
            if (!string.IsNullOrEmpty(dir)) WatchDir(dir);
        }
    }

    public void WatchDir(string dir)
    {
        if (_watchedDirs.Contains(dir) || !Directory.Exists(dir)) return;
        _watchedDirs.Add(dir);
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            w.Renamed += OnRenamed;
            w.Deleted += OnDeleted;
            w.Changed += OnChanged;
            _watchers.Add(w);
        }
        catch { /* 网络盘/权限问题，忽略 */ }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            var (matched, _) = _fingerprints.Resolve(e.FullPath);
            if (matched != null)
            {
                var ann = _repo.GetAnnotation(matched.Id);
                if (ann != null)
                    _repo.AddTimeline("file", matched.Id, "renamed",
                        $"重命名：{e.OldFullPath} → {e.FullPath}，备注已自动同步");
            }
        }
        catch { }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            var fr = _repo.GetFileRefByPath(e.FullPath);
            if (fr != null)
            {
                fr.Offline = true;
                _repo.UpsertFileRef(fr);
                _repo.AddTimeline("file", fr.Id, "deleted", "文件缺失，备注保留为孤儿备注：" + e.FullPath);
            }
        }
        catch { }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var fr = _repo.GetFileRefByPath(e.FullPath);
            if (fr == null) return;
            var fresh = _fingerprints.BuildFileRef(e.FullPath);
            if (fr.Size != fresh.Size || fr.MTime != fresh.MTime)
            {
                fr.Size = fresh.Size;
                fr.MTime = fresh.MTime;
                if (fresh.FullHash != null && fresh.FullHash != fr.FullHash)
                {
                    _repo.AddTimeline("file", fr.Id, "modified", "内容变化（哈希更新）：" + e.FullPath);
                    fr.FullHash = fresh.FullHash;
                }
                _repo.UpsertFileRef(fr);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        try { _usn?.Dispose(); } catch { }
    }
}
