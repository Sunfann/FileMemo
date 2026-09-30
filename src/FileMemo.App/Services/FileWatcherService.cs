using System.IO;
using FileMemo.App.Data;
using FileMemo.App.Interop;
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

    /// <summary>后台监听发现文件路径变化（移动/重命名/删除/校正）后触发，供 UI 刷新列表与文件树。</summary>
    public event Action? DataChanged;

    public FileWatcherService(Repository repo) : this(repo, SettingsService.Load()) { }

    public FileWatcherService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
        _fingerprints = new FingerprintService(repo);
    }

    public void Start()
    {
        // 启动校正：弥补应用未运行期间发生的移动/重命名（监听器离线期间无法捕获事件），
        // 否则这些备注的路径会一直是旧值，「打开/定位」将指向不存在的文件。
        try { ReconcileAll(); } catch { }

        // USN Journal 实时订阅（P1）：会自行按卷枚举，失败自动降级
        try
        {
            _usn = new UsnJournalService(_repo, _settings);
            try { _usn.Changed += _ => RaiseDataChanged(); } catch { }
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

        RaiseDataChanged();
    }

    /// <summary>
    /// 确保某个目录被监听。除应用启动时批量注册外，新增备注时也应调用，
    /// 否则"启动之后才添加备注的文件"永远不进监听范围，移动/重命名将无法同步。
    /// </summary>
    public void EnsureWatchForPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var dir = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) WatchDir(dir);
        }
        catch { }
    }

    /// <summary>
    /// 校正单条备注的文件路径：当库中记录的路径已不存在、而文件实际被移动到别处时，
    /// 借助指纹（同卷 File ID / 内容哈希）找回真实路径并回写，保证「文件移动后仍能打开/定位」。
    /// 返回：路径被更新则为新路径，否则为 null。
    /// </summary>
    public string? Reconcile(FileRef fr)
    {
        try
        {
            if (fr == null) return null;
            // 路径仍有效则无需处理
            if (File.Exists(fr.Path) || Directory.Exists(fr.Path)) return null;

            var located = _fingerprints.TryLocate(fr);
            if (string.IsNullOrEmpty(located) ||
                string.Equals(located, fr.Path, StringComparison.OrdinalIgnoreCase))
                return null;

            var old = fr.Path;
            fr.Path = located;
            fr.Offline = false;
            fr.LastSeen = DateTime.Now;
            _repo.UpsertFileRef(fr);

            // 若是文件夹：内部已备注的子项路径也一并跟随
            if (fr.IsDir)
            {
                int n = _repo.RebindPathPrefix(old, located);
                if (n > 0)
                    _repo.AddTimeline("file", fr.Id, "moved",
                        $"路径核对：文件夹已移动，已同步 {n} 个子项备注路径：{old} → {located}");
            }

            _repo.AddTimeline("file", fr.Id, "moved",
                $"路径核对：备注路径自动跟随到 {located}（原 {old}）");

            // 新位置所在目录也要纳入监听，避免后续移动再次漏同步
            EnsureWatchForPath(located);
            return located;
        }
        catch { return null; }
    }

    /// <summary>
    /// 启动时对所有「路径已失效」的备注做一次全量校正。
    /// 用于弥补应用未运行期间发生的移动/重命名（此期间监听器收不到任何事件）。
    /// 返回被成功校正的条目数。
    /// </summary>
    public int ReconcileAll()
    {
        int fixedCount = 0;
        try
        {
            foreach (var ann in _repo.GetAllAnnotations())
            {
                var fr = _repo.GetFileRefById(ann.FileRefId);
                if (fr == null) continue;
                if (Reconcile(fr) != null) fixedCount++;
            }
        }
        catch { }
        if (fixedCount > 0) RaiseDataChanged();
        return fixedCount;
    }

    /// <summary>
    /// 目标目录出现新条目：若它其实是某个"已备注文件"被移动过来的（同卷 File ID 命中），
    /// 则让备注跟随到新路径。用于 FileSystemWatcher 把"跨目录移动"拆成 Deleted+Created 的场景。
    /// 仅采用 File ID 这一强信号，避免把"复制出来的同内容副本"误绑到原备注上。
    /// </summary>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (_repo.GetFileRefByPath(e.FullPath) != null) return;   // 该路径已有精确备注
            var id = NativeMethods.GetNtfsIdentity(e.FullPath);
            if (id == null) return;
            var fr = _repo.FindByFileId(id.Value.volumeGuid, id.Value.fileId).FirstOrDefault();
            if (fr == null) return;
            RelocateToNewPath(fr, e.FullPath, "文件移动到位（Created 命中 File ID）");
        }
        catch { }
    }

    /// <summary>把备注的文件路径迁移到新位置：更新路径与在线状态、跟随子项、写时间线、纳入监听并通知 UI。</summary>
    private void RelocateToNewPath(FileRef fr, string newPath, string reason)
    {
        if (fr == null || string.IsNullOrWhiteSpace(newPath)) return;
        if (string.Equals(fr.Path, newPath, StringComparison.OrdinalIgnoreCase)) return;

        var old = fr.Path;
        fr.Path = newPath;
        fr.Offline = false;
        fr.LastSeen = DateTime.Now;
        _repo.UpsertFileRef(fr);

        // 文件夹移动：内部已备注的子项路径一并跟随
        if (fr.IsDir)
        {
            int n = _repo.RebindPathPrefix(old, newPath);
            if (n > 0)
                _repo.AddTimeline("file", fr.Id, "moved",
                    $"文件夹移动，已同步 {n} 个子项备注路径：{old} → {newPath}");
        }

        _repo.AddTimeline("file", fr.Id, "moved", $"{reason}：{old} → {newPath}");
        EnsureWatchForPath(newPath);
        RaiseDataChanged();
    }

    /// <summary>后台线程发现路径变化后，通知 UI 线程刷新列表与文件树（UI 未就绪时安全忽略）。</summary>
    private void RaiseDataChanged()
    {
        try
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(new Action(() => { try { DataChanged?.Invoke(); } catch { } }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { }
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
                // 递归监听：文件夹内部的文件被移动/重命名时也要能捕获（原先 false 会漏掉整棵子树）
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            w.Renamed += OnRenamed;
            w.Deleted += OnDeleted;
            w.Created += OnCreated;
            w.Changed += OnChanged;
            _watchers.Add(w);
        }
        catch { /* 网络盘/权限问题，忽略 */ }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            // 1) 借助指纹找回被移动/重命名对象对应的 FileRef（同卷走 File ID，跨卷走哈希/卷序列号）。
            //    Resolve 命中时通常已把 FileRef.Path 更新为新路径。
            var (matched, _) = _fingerprints.Resolve(e.FullPath);

            // 2) 指纹未命中时再按旧路径兜底查一次（针对无法取到 File ID 的非 NTFS / 网络盘）
            var fr = matched ?? _repo.GetFileRefByPath(e.OldFullPath) ?? _repo.GetFileRefByPath(e.FullPath);
            if (fr == null)
            {
                // 被移动/重命名的对象本身没有备注，但它可能是"装着已备注子项的文件夹"，
                // 仍需把子项备注路径跟着搬过去。用事件自带的旧/新路径直接做前缀改写。
                int moved = _repo.RebindPathPrefix(e.OldFullPath, e.FullPath);
                if (moved > 0)
                {
                    _repo.AddTimeline("file", e.FullPath, "moved",
                        $"文件夹重命名/移动，已同步 {moved} 个子项备注路径：{e.OldFullPath} → {e.FullPath}");
                    RaiseDataChanged();
                }
                return;
            }

            if (!string.Equals(fr.Path, e.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                fr.Path = e.FullPath;
                fr.Offline = false;
                fr.LastSeen = DateTime.Now;
                _repo.UpsertFileRef(fr);
            }

            // 3) 若是文件夹被重命名/移动：内部已备注的子项也必须一起跟随。
            //    注意必须以事件自带 OldFullPath 为基准做前缀改写——因为 fr.Path 可能已被
            //    Resolve 提前更新为新路径，用它当"旧前缀"会改不动任何子项。
            if (fr.IsDir)
            {
                int n = _repo.RebindPathPrefix(e.OldFullPath, e.FullPath);
                _repo.AddTimeline("file", fr.Id, "moved",
                    $"文件夹重命名/移动：{e.OldFullPath} → {e.FullPath}" + (n > 0 ? $"，已同步 {n} 个子项备注" : ""));
            }
            else
            {
                var ann = _repo.GetAnnotation(fr.Id);
                if (ann != null)
                    _repo.AddTimeline("file", fr.Id, "renamed",
                        $"重命名：{e.OldFullPath} → {e.FullPath}，备注已自动同步");
            }

            RaiseDataChanged();
        }
        catch { }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            var fr = _repo.GetFileRefByPath(e.FullPath);
            if (fr == null) return;

            // 先判断"文件只是被移动到别处"：跨目录移动在 FileSystemWatcher 下表现为
            // 源目录 Deleted + 目标目录 Created，若不在此处尝试找回，就会把移动误判为删除，
            // 把备注标记成孤儿（正是"文件移动后备注丢失、无法跟踪"的根因之一）。
            if (Reconcile(fr) != null)
            {
                RaiseDataChanged();   // 备注已跟随到新路径：通知 UI 刷新列表与文件树
                return;
            }

            fr.Offline = true;
            _repo.UpsertFileRef(fr);
            _repo.AddTimeline("file", fr.Id, "deleted", "文件缺失，备注保留为孤儿备注：" + e.FullPath);
            RaiseDataChanged();

            // 文件夹被删除：其下子项一并标记离线（备注保留）
            if (fr.IsDir)
            {
                foreach (var sub in _repo.GetFileRefsUnder(fr.Path))
                {
                    sub.Offline = true;
                    _repo.UpsertFileRef(sub);
                }
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
                RaiseDataChanged();
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
