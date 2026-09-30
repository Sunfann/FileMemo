using System.IO;
using System.Security.Cryptography;
using FileMemo.App.Data;
using FileMemo.App.Interop;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 多重指纹追踪（需求 3.5）：
/// 主指纹 = 卷 GUID + NTFS File ID；辅助 = 路径/大小/时间/父目录/扩展名/哈希。
/// 同卷移动/重命名通过 File ID 命中；跨卷通过哈希 + 大小 + 时间 + 名称相似度给出候选。
/// </summary>
public sealed class FingerprintService
{
    private readonly Repository _repo;

    public FingerprintService(Repository repo) => _repo = repo;

    public FileRef BuildFileRef(string path)
    {
        var fi = new FileInfo(path);
        var isDir = Directory.Exists(path);

        var fr = new FileRef
        {
            Path = path,
            IsDir = isDir,
            Ext = isDir ? "" : fi.Extension.ToLowerInvariant(),
            Size = isDir ? null : fi.Length,
            MTime = fi.LastWriteTimeUtc,
            CTime = fi.CreationTimeUtc,
        };

        // 网络盘 / NAS 标记（P1）：影响 sidecar 策略与追踪方式
        fr.IsNetwork = SidecarService.IsRemovableOrNetwork(path);

        // 主指纹
        var id = NativeMethods.GetNtfsIdentity(path);
        if (id != null)
        {
            fr.FileId = id.Value.fileId;
            fr.VolumeGuid = id.Value.volumeGuid;
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                var pid = NativeMethods.GetNtfsIdentity(parent);
                if (pid != null) fr.ParentFileId = pid.Value.fileId;
            }
        }

        // 辅助指纹：卷序列号（非 NTFS / 网络盘 / NAS 也能取到，P1 需求 3.5.3）
        fr.VolumeSerial = NativeMethods.TryGetVolumeSerial(path);

        // 快速哈希（前 1MB）
        if (!isDir && fi.Length > 0)
        {
            fr.QuickHash = QuickHash(path);
            if (fi.Length <= 8 * 1024 * 1024)
                fr.FullHash = FullHash(path);
        }
        return fr;
    }

    public void SaveFingerprints(FileRef fr)
    {
        var list = new List<Fingerprint>();
        void Add(FingerprintType t, string? v, double c)
        {
            if (!string.IsNullOrEmpty(v)) list.Add(new Fingerprint { FileRefId = fr.Id, Type = t, Value = v, Confidence = c });
        }

        if (fr.VolumeGuid != null) Add(FingerprintType.VolumeGuid, fr.VolumeGuid, 0.9);
        if (fr.FileId != null) Add(FingerprintType.NtfsFileId, fr.FileId.Value.ToString(), 1.0);
        if (fr.VolumeSerial != null) Add(FingerprintType.VolumeSerial, fr.VolumeSerial.Value.ToString(), 0.7);
        if (fr.Usn != null) Add(FingerprintType.Usn, fr.Usn.Value.ToString(), 0.85);
        if (fr.ParentFileId != null) Add(FingerprintType.ParentFileId, fr.ParentFileId.Value.ToString(), 0.4);
        if (fr.CTime != null) Add(FingerprintType.CTime, fr.CTime.Value.ToString("o"), 0.5);
        Add(FingerprintType.Path, fr.Path, 0.6);
        Add(FingerprintType.Size, fr.Size?.ToString(), 0.5);
        Add(FingerprintType.MTime, fr.MTime?.ToString("o"), 0.5);
        Add(FingerprintType.Extension, fr.Ext, 0.3);
        Add(FingerprintType.QuickHash, fr.QuickHash, 0.8);
        Add(FingerprintType.FullHash, fr.FullHash, 0.95);

        _repo.SaveFingerprints(fr.Id, list);
    }

    /// <summary>
    /// 给定"新路径"，尝试找回已有备注的 FileRef（处理重命名 / 移动 / 跨卷）。
    /// 返回值：命中的 FileRef（已更新路径）或 null（需要新建 / 用户确认）。
    /// </summary>
    /// <param name="conservative">
    /// 保守模式：调用方是"为新文件建备注"而非"监听到的移动事件"时置 true。
    /// 此时仅接受强信号（同卷 File ID 命中，或跨卷哈希命中且文件名一致），
    /// 避免把一份全新文件的备注误绑到内容恰好相同的旧文件上。
    /// </param>
    public (FileRef? matched, List<(FileRef candidate, double score)> candidates) Resolve(string newPath, bool conservative = false)
    {
        var fresh = BuildFileRef(newPath);
        var name = System.IO.Path.GetFileName(newPath);

        // 1) 主指纹命中：同卷重命名 / 移动（最强信号，两种模式都接受）
        if (fresh.FileId != null)
        {
            var hits = _repo.FindByFileId(fresh.VolumeGuid, fresh.FileId);
            if (hits.Count > 0)
            {
                var existing = hits[0];
                existing.Path = newPath;
                existing.Size = fresh.Size;
                existing.MTime = fresh.MTime;
                existing.CTime = fresh.CTime;
                _repo.UpsertFileRef(existing);
                _repo.AddTimeline("file", existing.Id, "moved", "同卷重命名/移动，File ID 命中：" + newPath);
                return (existing, new());
            }
        }

        // 2) 辅助指纹：完全哈希命中（跨卷复制的强信号）
        if (fresh.FullHash != null)
        {
            var byHash = _repo.FindByHash(fresh.FullHash, null);
            if (byHash.Count == 1)
            {
                var existing = byHash[0];
                // 保守模式下要求文件名也必须一致：内容相同但名字不同的极可能是另一份文件/一次复制
                bool nameOk = !conservative ||
                    string.Equals(System.IO.Path.GetFileName(existing.Path), name, StringComparison.OrdinalIgnoreCase);
                if (nameOk)
                {
                    existing.Path = newPath;
                    existing.VolumeGuid = fresh.VolumeGuid;
                    existing.FileId = fresh.FileId;
                    existing.Size = fresh.Size;
                    existing.MTime = fresh.MTime;
                    _repo.UpsertFileRef(existing);
                    _repo.AddTimeline("file", existing.Id, "moved", "跨卷移动，完整哈希命中：" + newPath);
                    return (existing, new());
                }
            }
        }

        // 3) 网络盘 / NAS：卷序列号 + 路径 + 哈希兜底（需求 3.5.3）
        //    安全性要求：仅凭"卷上只有一个备注"就认定命中会误伤全新文件，
        //    故必须同时满足同目录名或文件大小一致，才允许按卷序列号迁移。
        if (fresh.VolumeSerial != null)
        {
            var byVol = _repo.FindByVolumeSerial(fresh.VolumeSerial.Value);
            if (byVol.Count == 1)
            {
                var existing = byVol[0];
                bool sameName = string.Equals(System.IO.Path.GetFileName(existing.Path), name, StringComparison.OrdinalIgnoreCase);
                bool sameSize = existing.Size != null && fresh.Size != null && existing.Size == fresh.Size;
                bool sameDirKind = existing.IsDir == fresh.IsDir;
                bool acceptable = conservative ? sameDirKind && sameName : sameDirKind && (sameName || sameSize);
                if (acceptable)
                {
                    existing.Path = newPath;
                    existing.VolumeGuid = fresh.VolumeGuid;
                    existing.FileId = fresh.FileId;
                    existing.IsNetwork = fresh.IsNetwork;
                    existing.Size = fresh.Size;
                    existing.MTime = fresh.MTime;
                    _repo.UpsertFileRef(existing);
                    _repo.AddTimeline("file", existing.Id, "moved", "网络盘/NAS 卷序列号命中：" + newPath);
                    return (existing, new());
                }
            }
        }

        // 4) 弱信号候选（需用户确认，需求 3.5.5 冲突处理）
        var candidates = _repo.RecommendMigrationCandidates(name, fresh.Size, fresh.MTime);
        return (null, candidates);
    }

    /// <summary>
    /// 为一条「原路径可能已失效」的备注找回文件的当前真实路径。
    /// 用于弥补应用未运行期间（或监听漏事件时）发生的移动/重命名——
    /// 此时库中记录仍指向旧路径，直接「打开/定位」会失败。
    /// 依次尝试：原路径仍有效 → 同卷 NTFS File ID 反查 → 完整哈希在库内唯一命中。
    /// 找不到返回 null（调用方据此提示「文件已被移动或删除」）。
    /// </summary>
    public string? TryLocate(FileRef fr)
    {
        if (fr == null) return null;
        try
        {
            // 0) 原路径依旧有效：无需迁移
            if (File.Exists(fr.Path) || Directory.Exists(fr.Path)) return fr.Path;

            // 1) 主指纹：同卷 File ID → 打开句柄反查真实路径。
            //    File ID 在同一卷内唯一，且随文件移动/重命名保持不变，是最可靠的补救信号。
            if (fr.FileId != null)
            {
                var root = System.IO.Path.GetPathRoot(fr.Path);
                if (!string.IsNullOrEmpty(root))
                {
                    var h = NativeMethods.OpenByFileId(root, fr.FileId.Value);
                    if (h != NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        try
                        {
                            var gotId = NativeMethods.GetFileIdFromHandle(h);
                            var found = NativeMethods.GetPathFromHandle(h);
                            if (gotId != null && gotId.Value == fr.FileId.Value && !string.IsNullOrEmpty(found))
                            {
                                // 双重校验：卷 GUID 一致才采信，避免盘符被复用后误指到别的卷
                                if (string.IsNullOrEmpty(fr.VolumeGuid) ||
                                    string.Equals(NativeMethods.TryGetVolumeGuid(found), fr.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                                    return found;
                            }
                        }
                        finally { NativeMethods.CloseHandle(h); }
                    }
                }
            }

            // 2) 辅助指纹：内容哈希在库内唯一命中（跨卷移动兜底）
            if (!string.IsNullOrEmpty(fr.FullHash))
            {
                string? only = null;
                int count = 0;
                foreach (var c in _repo.FindByHash(fr.FullHash, null))
                {
                    if (c.Id == fr.Id) continue;
                    if (!File.Exists(c.Path) && !Directory.Exists(c.Path)) continue;
                    only = c.Path;
                    if (++count > 1) break;
                }
                if (count == 1) return only;
            }
        }
        catch { }
        return null;
    }

    private static string QuickHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[1024 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n))).ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static string FullHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }
}
