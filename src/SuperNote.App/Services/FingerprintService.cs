using System.IO;
using System.Security.Cryptography;
using SuperNote.App.Data;
using SuperNote.App.Interop;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

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
    public (FileRef? matched, List<(FileRef candidate, double score)> candidates) Resolve(string newPath)
    {
        var fresh = BuildFileRef(newPath);

        // 1) 主指纹命中：同卷重命名 / 移动
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
        var name = System.IO.Path.GetFileName(newPath);
        if (fresh.FullHash != null)
        {
            var byHash = _repo.FindByHash(fresh.FullHash, null);
            if (byHash.Count == 1)
            {
                var existing = byHash[0];
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

        // 3) 网络盘 / NAS：卷序列号 + 路径 + 哈希兜底（需求 3.5.3）
        if (fresh.VolumeSerial != null)
        {
            var byVol = _repo.FindByVolumeSerial(fresh.VolumeSerial.Value);
            if (byVol.Count == 1)
            {
                var existing = byVol[0];
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

        // 4) 弱信号候选（需用户确认，需求 3.5.5 冲突处理）
        var candidates = _repo.RecommendMigrationCandidates(name, fresh.Size, fresh.MTime);
        return (null, candidates);
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
