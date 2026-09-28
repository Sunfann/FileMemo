using System.IO;
using FileMemo.App.Data;

namespace FileMemo.App.Services;

/// <summary>
/// 后台服务（需求 3.15 / 7.P2「后台服务」）。
///
/// 以 <c>FileMemo.exe --service</c> 方式启动时进入无界面模式：
/// 只做大规模文件追踪（USN Journal）、网络盘 / NAS 监控与开机索引，
/// 不创建任何 WPF 窗口。配合 scripts/install-service.ps1 注册为 Windows 服务。
/// </summary>
public static class BackgroundServiceHost
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperNote", "service.log");

    /// <summary>命令行是否请求后台服务模式。</summary>
    public static bool IsServiceMode(string[] args) =>
        args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

    /// <summary>后台服务主循环；阻塞直到收到停止信号。返回退出码。</summary>
    public static int Run(string[] args)
    {
        FileWatcherService? watcher = null;
        try
        {
            var settings = SettingsService.Load();
            // 后台服务下强制开启监听相关能力，并默认开启全盘/网络盘追踪（可被 settings 覆盖）
            var db = new Database(settings.DatabasePath);
            db.Migrate();
            var repo = new Repository(db);

            Log("后台服务启动：目录 " + settings.DatabasePath);

            watcher = new FileWatcherService(repo, settings);
            watcher.Start();       // 内部 USN Journal + FileSystemWatcher 兜底
            Log("文件追踪已启动（USN Journal + FileSystemWatcher）");

            // 首次启动按需做一次向量索引（若已开启图片向量）
            try
            {
                if (settings.ImageVectorEnabled)
                {
                    var n = new ImageVectorService(repo, settings).ReindexAll();
                    Log($"图片向量索引完成，共 {n} 张");
                }
            }
            catch (Exception ex) { Log("向量索引失败：" + ex.Message); }

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();

            Log("进入待机；按 Ctrl+C 停止。");
            stop.Wait();
            Log("收到停止信号，正在退出。");
            return 0;
        }
        catch (Exception ex)
        {
            Log("后台服务异常：" + ex);
            return 1;
        }
        finally
        {
            try { watcher?.Dispose(); } catch { }
        }
    }

    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
        try { Console.WriteLine(line); } catch { }
    }
}
