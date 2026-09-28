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

            change.ResolvedPath = ResolvePath(change, volGuid);
            if (change.ResolvedPath != null)
                _frnToPath[(volGuid, change.FileReferenceNumber)] = change.ResolvedPath;

            try { Changed?.Invoke(change); } catch { }
            ApplyToRepository(change, volGuid);

            offset += recordLength;
        }
    }

    private string? ResolvePath(UsnChange c, string volGuid)
    {
        if (c.ParentFileReferenceNumber == 0 || string.IsNullOrEmpty(c.FileName)) return null;

        if (_frnToPath.TryGetValue((volGuid, c.ParentFileReferenceNumber), out var parentPath))
        {
            try { return Path.Combine(parentPath, c.FileName); } catch { return null; }
        }
        return null;
    }

    /// <summary>把 USN 变化落到备注绑定：重命名 / 移动命中 FileId 时自动同步路径。</summary>
    private void ApplyToRepository(UsnChange c, string volGuid)
    {
        try
        {
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
                }
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
