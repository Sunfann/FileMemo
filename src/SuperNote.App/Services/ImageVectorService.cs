using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SuperNote.App.Data;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 图片向量搜索（需求 3.7，P2）——纯本地特征向量，不引入任何 AI 模型或云服务。
///
/// 特征 = 8x8 灰度（64 维）
///      + 4x4x4 颜色直方图（64 维）
///      + 4x4 边缘密度网格（16 维）
///      + 全局统计：平均亮度 / 对比度 / 宽高比 / 平均饱和度（4 维）
/// 合计 148 维；相似度使用余弦相似度。
/// </summary>
public sealed class ImageVectorService
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;

    private static readonly string[] ImageExt =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public ImageVectorService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    public static bool IsImage(string path)
    {
        try { return ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant()); }
        catch { return false; }
    }

    /// <summary>计算图像特征向量（148 维）；失败返回 null。</summary>
    public float[]? ComputeFeatures(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;
            using var bmp = new Bitmap(imagePath);

            var feats = new List<float>(148);

            // ---- 8x8 灰度 ----
            using (var small = new Bitmap(8, 8))
            {
                using (var gr = Graphics.FromImage(small))
                {
                    gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    gr.DrawImage(bmp, 0, 0, 8, 8);
                }

                double sum = 0, sum2 = 0;
                var gray = new float[64];
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        var c = small.GetPixel(x, y);
                        float g = (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 255f;
                        gray[y * 8 + x] = g;
                        feats.Add(g);
                        sum += g; sum2 += g * g;
                    }
                }

                // ---- 4x4x4 颜色直方图 ----
                var hist = new float[64];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        var c = small.GetPixel(x, y);
                        int r = Math.Min(3, c.R * 4 / 256);
                        int g = Math.Min(3, c.G * 4 / 256);
                        int bl = Math.Min(3, c.B * 4 / 256);
                        hist[r * 16 + g * 4 + bl] += 1f / 64f;
                    }
                feats.AddRange(hist);

                // ---- 4x4 边缘密度 ----
                var edges = new float[16];
                for (int by = 0; by < 4; by++)
                    for (int bx = 0; bx < 4; bx++)
                    {
                        float e = 0;
                        for (int y = 0; y < 2; y++)
                            for (int x = 0; x < 2; x++)
                            {
                                int ix = bx * 2 + x, iy = by * 2 + y;
                                int nx = Math.Min(7, ix + 1), ny = Math.Min(7, iy + 1);
                                e += Math.Abs(gray[iy * 8 + ix] - gray[iy * 8 + nx]);
                                e += Math.Abs(gray[iy * 8 + ix] - gray[ny * 8 + ix]);
                            }
                        edges[by * 4 + bx] = Math.Min(1f, e / 4f);
                    }
                feats.AddRange(edges);

                // ---- 全局统计 ----
                double mean = sum / 64.0;
                double std = Math.Sqrt(Math.Max(0, sum2 / 64.0 - mean * mean));
                feats.Add((float)mean);
                feats.Add((float)std);
                feats.Add((float)bmp.Width / Math.Max(1, bmp.Height));
                feats.Add(AverageSaturation(bmp));
            }

            return feats.ToArray();
        }
        catch { return null; }
    }

    private static float AverageSaturation(Bitmap bmp)
    {
        try
        {
            using var small = new Bitmap(bmp, new Size(16, 16));
            double s = 0;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    var c = small.GetPixel(x, y);
                    int max = Math.Max(c.R, Math.Max(c.G, c.B));
                    int min = Math.Min(c.R, Math.Min(c.G, c.B));
                    s += max == 0 ? 0 : (max - min) / (double)max;
                }
            return (float)(s / 256.0);
        }
        catch { return 0f; }
    }

    /// <summary>把某文件的图像特征建索引（仅图片；已存在则刷新）。</summary>
    public bool IndexFile(FileRef fr, string? imagePathOverride = null)
    {
        try
        {
            var path = imagePathOverride ?? fr.Path;
            if (string.IsNullOrEmpty(path) || !IsImage(path)) return false;

            var feats = ComputeFeatures(path);
            if (feats == null) return false;

            var existing = _repo.GetImageVectorByPath(path);
            _repo.UpsertImageVector(new ImageVector
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                FileRefId = fr.Id,
                ImagePath = path,
                Dim = feats.Length,
                Values = feats
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>为所有图片备注 / 剪贴板图片重建向量索引，返回成功数量。</summary>
    public int ReindexAll()
    {
        int n = 0;
        try
        {
            foreach (var ann in _repo.GetAllAnnotations())
            {
                var fr = _repo.GetFileRefById(ann.FileRefId);
                if (fr == null || fr.IsDir) continue;
                if (IndexFile(fr)) n++;
            }

            foreach (var clip in _repo.GetClips())
            {
                if (clip.Kind != ClipKind.Image || string.IsNullOrEmpty(clip.ImagePath)) continue;
                if (!File.Exists(clip.ImagePath)) continue;
                var feats = ComputeFeatures(clip.ImagePath);
                if (feats == null) continue;
                var existing = _repo.GetImageVectorByPath(clip.ImagePath);
                _repo.UpsertImageVector(new ImageVector
                {
                    Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                    FileRefId = "",
                    ImagePath = clip.ImagePath,
                    Dim = feats.Length,
                    Values = feats
                });
                n++;
            }
        }
        catch { }
        return n;
    }

    /// <summary>以一张查询图找最相似的已索引图片（余弦相似度）。</summary>
    public List<VectorMatch> FindSimilar(string queryImagePath, int top = 10)
    {
        var result = new List<VectorMatch>();
        try
        {
            var q = ComputeFeatures(queryImagePath);
            if (q == null) return result;

            foreach (var v in _repo.GetAllImageVectors())
            {
                if (string.Equals(v.ImagePath, queryImagePath, StringComparison.OrdinalIgnoreCase)) continue;
                double score = Cosine(q, v.Values);
                result.Add(new VectorMatch { FileRefId = v.FileRefId, ImagePath = v.ImagePath, Score = score });
            }
            result.Sort((a, b) => b.Score.CompareTo(a.Score));
        }
        catch { }
        return result.Take(top).ToList();
    }

    /// <summary>以已索引图片 id 为查询找相似图（便于 UI 里对某张备注图「查找相似」）。</summary>
    public List<VectorMatch> FindSimilarByPath(string imagePath, int top = 10) => FindSimilar(imagePath, top);

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
