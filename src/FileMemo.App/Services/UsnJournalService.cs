using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FileMemo.App.Data;
using FileMemo.App.Interop;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// USN Journal 实时订阅（需求 3.5.3，P1）。
///
/// 相比 FileSystemWatcher，USN Journal 能捕获：
///   - 高频重命名 / 移动（含同卷父目录变化）
///   - 文件创建 / 删除 / 数据覆盖
///   - 事件带有 FileReferenceNumber，可直接与主指纹（NTFS File ID）对齐
///
/// 实现：对每个被追踪卷开一个后台线程，循环 FSCTL_READ_USN_JOURNAL，
/// 解析 USN_RECORD_V2，用「父目录 FRN → 路径」映射重建新路径，
/// 命中已有 FileRef 时自动同步备注绑定。权限不足 / 非 NTFS 卷自动跳过（降级为 FileSystemWatcher）。
/// </summary>
public sealed class UsnJournalService : IDisposable
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;
    private readonly FingerprintService _fingerprints;

    private readonly List<Thread> _threads = new();
    private readonly List<IntPtr> _volumes = new();
    private readonly ConcurrentDictionary<string, long> _lastUsn = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string vol, long frn), string> _frnToPath = new();
    private volatile bool _running;

    /// <summary>每捕获一条变化触发一次（已尽力解析出路径）。</summary>
    public event Action<UsnChange>? Changed;

    public UsnJournalService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
        _fingerprints = new FingerprintService(repo);
    }

    /// <summary>启动订阅。返回成功订阅的卷数量（0 表示全部降级）。</summary>
    public int Start()
    {
        if (_running) return _volumes.Count;
        _running = true;

        SeedFrnMap();

        var roots = CollectVolumeRoots();
        SeedVolumeRoots(roots);
        int started = 0;
        foreach (var root in roots)
        {
            var jd = NativeMethods.QueryUsnJournal(root);
            if (jd == null) continue;                 // 非 NTFS / 无权限：跳过并降级
            if (!_settings.WatchUsnJournal) continue;

            IntPtr h = NativeMethods.CreateFile(root, NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) continue;

            // 从当前 NextUsn 起读，避免重放历史；同时保留卷 GUID 便于和 FileRef 对齐
            _lastUsn[root] = jd.Value.NextUsn;
            string volGuid = NativeMethods.TryGetVolumeGuid(root) ?? root;
            _volumes.Add(h);

            var t = new Thread(() => PollLoop(h, root, volGuid)) { IsBackground = true, Name = "USN-" + root };
            _threads.Add(t);
            t.Start();
            started++;
        }
        return started;
    }

    /// <summary>用已有 FileRef 预填「FileId → Path」「父目录 FRN」映射，便于重命名时重建路径。</summary>
    private void SeedFrnMap()
    {
        try
        {
            foreach (var fr in _repo.GetAllFileRefs())
            {
                if (fr.FileId is null) continue;
                string vol = fr.VolumeGuid ?? "";
                _frnToPath[(vol, fr.FileId.Value)] = fr.Path;
            }
        }
        catch { }
    }

    /// <summary>
    /// 预填每个被追踪卷的「根目录 FRN → 卷根路径」映射。
    /// NTFS 根目录的 FileReferenceNumber 固定为 5，先把这一层补上，
    /// 才能解析出位于卷根下的第一层重命名/移动事件（无需每次走 OpenFileById）。
    /// </summary>
    private void SeedVolumeRoots(IEnumerable<string> roots)
    {
        const long NtfsRootFrn = 5;
        foreach (var root in roots)
        {
            try
            {
                var volGuid = NativeMethods.TryGetVolumeGuid(root) ?? root;
                // 卷根必须保留末尾反斜杠（"C:\"）。若裁成 "C:"，Path.Combine 会得到
                // "C:file.txt" 这种"盘符相对路径"，拼接结果错误。
                var normalized = root.EndsWith("\\") || root.EndsWith("/") ? root : root + "\\";
                _frnToPath[(volGuid, NtfsRootFrn)] = normalized;
                _frnToPath[(root, NtfsRootFrn)] = normalized;
            }
            catch { }
        }
    }

    private List<string> CollectVolumeRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root)) roots.Add(root);
            }
            catch { }
        }

        try
        {
            foreach (var fr in _repo.GetAllFileRefs()) AddFromPath(fr.Path);
        }
        catch { }

        foreach (var r in _settings.TrackedRoots) AddFromPath(r);
        if (_settings.FullDiskTracking)
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName);
        }
        return roots.ToList();
    }

    private void PollLoop(IntPtr hVolume, string root, string volGuid)
    {
        const int bufferSize = 64 * 1024;
        IntPtr outBuf = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (_running)
            {
                long start = _lastUsn.TryGetValue(root, out var u) ? u : 0;
                var req = new NativeMethods.READ_USN_JOURNAL_DATA
                {
                    StartUsn = start,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = 0
                };

                if (!NativeMethods.DeviceIoControl(hVolume, NativeMethods.FSCTL_READ_USN_JOURNAL,
                        ref req, (uint)Marshal.SizeOf<NativeMethods.READ_USN_JOURNAL_DATA>(),
                        outBuf, bufferSize, out uint bytes, IntPtr.Zero))
                {
                    Thread.Sleep(1500);   // 卷不可用 / 被卸载：退避重试
                    continue;
                }
                if (bytes <= 8) { Thread.Sleep(400); continue; }

                long nextUsn = Marshal.ReadInt64(outBuf);
                ParseBuffer(outBuf, bytes, root, volGuid);
                _lastUsn[root] = nextUsn;

                if (bytes < bufferSize) Thread.Sleep(300);
            }
        }
        catch { /* 后台线程异常：静默降级 */ }
        finally { Marshal.FreeHGlobal(outBuf); }
    }

    private void ParseBuffer(IntPtr buffer, uint bytes, string root, string volGuid)
    {
        int offset = 8;                       // 跳过开头的 USN（next）
        while (offset < bytes)
        {
            int recordLength = Marshal.ReadInt32(buffer, offset);
            if (recordLength <= 0 || offset + recordLength > bytes) break;

            var rec = Marshal.PtrToStructure<NativeMethods.USN_RECORD_V2>(buffer + offset);

            // V2 名称：偏移相对记录起点，UTF-16
            string name = "";
            try
            {
                int nameOffset = offset + rec.FileNameOffset;
                if (rec.FileNameLength > 0)
                    name = Marshal.PtrToStringUni(buffer + nameOffset, rec.FileNameLength / 2) ?? "";
            }
            catch { }

            var change = new UsnChange
            {
                VolumeRoot = root,
                VolumeGuid = volGuid,
                FileReferenceNumber = (long)rec.FileReferenceNumber,
                ParentFileReferenceNumber = (long)rec.ParentFileReferenceNumber,
                FileName = name,
                Usn = rec.Usn,
                Reason = rec.Reason,
                TimeUtc = DateTime.FromFileTimeUtc(rec.TimeStamp)
            };

            // 先记录缓存中的旧路径，再解析新路径（重命名时旧路径是推算子项迁移的关键信息）
            if (_frnToPath.TryGetValue((volGuid, change.FileReferenceNumber), out var prev))
                change.PreviousPath = prev;

            change.ResolvedPath = ResolvePath(change, volGuid);

            // 只在"新名 / 新建"记录上更新 FRN→路径 缓存。
            // 重命名会连抛旧名与新名两条记录，若把旧名记录也写进缓存，
            // 可能让后续子项解析到已失效的旧目录名（USN 不保证两条记录的先后顺序）。
            if (change.ResolvedPath != null && !change.IsRenameOld)
                _frnToPath[(volGuid, change.FileReferenceNumber)] = change.ResolvedPath;

            try { Changed?.Invoke(change); } catch { }
            ApplyToRepository(change, volGuid);

            offset += recordLength;
        }
    }

    private string? ResolvePath(UsnChange c, string volGuid)
    {
        if (c.ParentFileReferenceNumber == 0 || string.IsNullOrEmpty(c.FileName)) return null;

        // 1) 父目录 FRN 已在缓存中：直接拼接（最快路径）
        if (_frnToPath.TryGetValue((volGuid, c.ParentFileReferenceNumber), out var parentPath)
            && !string.IsNullOrEmpty(parentPath))
        {
            try { return System.IO.Path.Combine(parentPath, c.FileName); } catch { return null; }
        }

        // 2) 缓存未命中：用 OpenFileById 打开父目录句柄，反查其真实路径，并回填缓存。
        //    这一步是"备注跟随文件移动"能否成立的关键——普通目录从未被登记进缓存，
        //    若不兜底则几乎所有移动/重命名的父目录都解析失败。
        var resolvedParent = ResolveDirectoryPath(volGuid, c.ParentFileReferenceNumber, c.VolumeRoot);
        if (resolvedParent == null) return null;

        _frnToPath[(volGuid, c.ParentFileReferenceNumber)] = resolvedParent;
        try { return System.IO.Path.Combine(resolvedParent, c.FileName); } catch { return null; }
    }

    /// <summary>用卷根 + 父目录 FRN 打开父目录句柄并取真实路径；顺带回填缓存。</summary>
    private string? ResolveDirectoryPath(string volGuid, long parentFrn, string volRoot)
    {
        try
        {
            IntPtr h = NativeMethods.OpenByFileId(volRoot, parentFrn);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return null;
            try
            {
                // 防御性校验：句柄的 File ID 应与请求的 FRN 一致（序号复用/并发改名时可能不一致）
                var gotId = NativeMethods.GetFileIdFromHandle(h);
                if (gotId != null && gotId.Value != parentFrn) return null;

                var path = NativeMethods.GetPathFromHandle(h);
                if (!string.IsNullOrEmpty(path))
                {
                    _frnToPath[(volGuid, parentFrn)] = path;
                    return path;
                }
                return null;
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { return null; }
    }

    /// <summary>
    /// 处理"没有备注的文件夹被移动/重命名"的情况：文件夹自身不在 file_ref 中，
    /// 但内部可能挂着已备注的子项。借助 FRN 缓存里的旧路径 + 新解析出的路径，
    /// 把子项备注路径一并迁移过去。
    /// </summary>
    private void TryRebindUnannotatedDir(string volGuid, UsnChange c)
    {
        try
        {
            // 旧路径可能来自缓存（PreviousPath）；再退一步，用"新路径 = 旧父路径 + 文件名"的
            // 逆运算无从下手，故此处只能依赖缓存。缓存缺失时放弃（下次事件或 FSW 会补上）。
            var oldDir = c.PreviousPath;
            if (string.IsNullOrEmpty(oldDir)) return;

            // 只有当旧路径确实像个目录前缀、且与目标不同才处理
            if (string.Equals(oldDir.TrimEnd('\\', '/'),
                              c.ResolvedPath!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return;

            // 快速判存在性：只有当库中真的存在该旧目录下的子项时才需要改写
            var subs = _repo.GetFileRefsUnder(oldDir);
            if (subs.Count == 0) return;

            int n = _repo.RebindPathPrefix(oldDir, c.ResolvedPath!);
            if (n > 0)
                _repo.AddTimeline("file", subs[0].Id, "moved",
                    $"未备注文件夹移动/重命名，已同步 {n} 个子项备注路径：{oldDir} → {c.ResolvedPath}");
        }
        catch { }
    }

    /// <summary>把 USN 变化落到备注绑定：重命名 / 移动命中 FileId 时自动同步路径。</summary>
    private void ApplyToRepository(UsnChange c, string volGuid)
    {
        try
        {
            // 重命名/移动时 USN 会连抛两条记录（旧名 + 新名）。旧名记录解析出的仍是旧路径，
            // 若在此更新会把刚同步好的新路径又改回去，因此只处理"新名"记录。
            if (c.IsRenameOld) return;

            if (c.IsRename || c.IsCreate)
            {
                var fr = _repo.FindByFileId(volGuid, c.FileReferenceNumber).FirstOrDefault();
                if (fr != null && c.ResolvedPath != null && !string.Equals(fr.Path, c.ResolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    string old = fr.Path;
                    fr.Path = c.ResolvedPath;
                    fr.Offline = false;
                    fr.LastSeen = DateTime.Now;
                    _repo.UpsertFileRef(fr);
                    _repo.AddTimeline("file", fr.Id, c.IsRename ? "renamed" : "created",
                        $"USN 实时同步：{old} → {c.ResolvedPath}");

                    // 关键补强：若被移动/重命名的对象是【文件夹】，其内部已备注的子文件/子文件夹
                    // 也必须跟随。FileRef.Id 不变，备注自然保留，只需同步路径前缀。
                    if (fr.IsDir)
                    {
                        int n = _repo.RebindPathPrefix(old, c.ResolvedPath);
                        if (n > 0)
                            _repo.AddTimeline("file", fr.Id, "moved",
                                $"文件夹移动/重命名，已同步 {n} 个子项备注路径：{old} → {c.ResolvedPath}");
                    }
                }
                else if (c.IsRename && c.ResolvedPath != null)
                {
                    // 被移动/重命名的文件夹本身没有备注，但它内部可能有已备注的子项。
                    // 用缓存里的旧路径推算新路径，仍要把子项备注跟着搬过去。
                    TryRebindUnannotatedDir(volGuid, c);
                }
            }
            else if (c.IsDelete)
            {
                var fr = _repo.FindByFileId(volGuid, c.FileReferenceNumber).FirstOrDefault();
                if (fr != null)
                {
                    fr.Offline = true;
                    _repo.UpsertFileRef(fr);
                    _repo.AddTimeline("file", fr.Id, "deleted", "USN 检测到删除，备注保留为孤儿：" + fr.Path);

                    // 文件夹被整体删除：其下子项一并标记离线（备注保留）
                    if (fr.IsDir)
                    {
                        foreach (var sub in _repo.GetFileRefsUnder(fr.Path))
                        {
                            sub.Offline = true;
                            _repo.UpsertFileRef(sub);
                        }
                    }
                }
                _frnToPath.TryRemove((volGuid, c.FileReferenceNumber), out _);
            }
        }
        catch { }
    }

    public void Stop()
    {
        _running = false;
        foreach (var h in _volumes)
        {
            try { NativeMethods.CloseHandle(h); } catch { }
        }
        _volumes.Clear();
        _threads.Clear();
    }

    public void Dispose() => Stop();
}
