using System.Diagnostics;
using System.IO;
using System.Text;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 与 Everything 联合搜索（需求 3.7）：
/// 本地固定盘优先调用 Everything 命令行 es.exe；网络盘/NAS 交给 Repository 的内置轻量索引。
/// 未安装 / 未运行 Everything 时自动降级。
/// </summary>
public sealed class EverythingSearchService
{
    private readonly string? _esPath;

    public bool Available => _esPath != null;

    public EverythingSearchService(string configuredPath)
    {
        _esPath = ResolveEs(configuredPath);
    }

    private static string? ResolveEs(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // 常见安装位置探测
        var candidates = new[]
        {
            @"C:\Program Files\Everything\es.exe",
            @"C:\Program Files (x86)\Everything\es.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "es.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // PATH 中探测
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "es.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>调用 es.exe 做文件名搜索。失败返回空列表，由调用方降级到内置索引。</summary>
    public List<string> SearchFiles(string query, int limit = 200)
    {
        var results = new List<string>();
        if (_esPath == null) return results;
        try
        {
            var psi = new ProcessStartInfo(_esPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // -n 限制数量，-p 输出完整路径
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(limit.ToString());
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(query);

            using var proc = Process.Start(psi);
            if (proc == null) return results;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim();
                if (p.Length > 0) results.Add(p);
                if (results.Count >= limit) break;
            }
        }
        catch { /* Everything 未运行或异常，降级 */ }
        return results;
    }
}
