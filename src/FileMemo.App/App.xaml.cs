using System.IO;
using System.Text;
using System.Windows;
using FileMemo.App.Data;
using FileMemo.App.Services;
using FileMemo.App.ViewModels;
using FileMemo.App.Views;

namespace FileMemo.App;

/// <summary>
/// 应用入口。负责初始化数据库、系统服务（剪贴板监听 / 全局热键 / 文件指纹），
/// 并创建主窗口、托盘图标；同时集中注册全局快捷键（不依赖主窗口是否显示）。
///
/// 稳定性约定：每个子系统初始化都独立 try/catch 隔离，任一子系统失败都不得
/// 阻塞主界面显示；同时把异常写入 %APPDATA%\SuperNote\crash.log 便于定位。
/// </summary>
public partial class App : Application
{
    public static App Instance => (App)Current;

    private System.Windows.Forms.NotifyIcon? _tray;
    private ClipboardMonitorService? _clipboard;
    private FileWatcherService? _watcher;
    private bool _hotkeysRegistered;

    // 桌面悬浮球（可个性化）+ 便签浮窗注册表（供「收纳 / 释放 / 最小化」批量操作）
    private FloatingBallWindow? _ball;

    // 主窗口引用 + 「真正退出」标志：主窗口点关闭默认只隐藏到托盘，
    // 托盘 / 悬浮球仍可唤回；只有走「退出」时才真正结束进程。
    private MainWindow? _main;
    private bool _shuttingDown;
    public bool IsShuttingDown => _shuttingDown;

    public Database Db { get; private set; } = null!;
    public Repository Repo { get; private set; } = null!;
    public SettingsService Settings { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public EverythingSearchService Everything { get; private set; } = null!;

    /// <summary>文件变化监听（USN Journal + FileSystemWatcher）。
    /// 外部（如新增备注时）可调用 EnsureWatchForPath 动态加入监听目录。</summary>
    public FileWatcherService? Watcher => _watcher;

    // ---- 悬浮球系统（配置 + 窗口注册表）----
    public BallConfigService BallConfig { get; private set; } = null!;
    public WindowRegistry BallWindows { get; private set; } = null!;

    // ---- P1 服务（需求 3.4 ~ 3.12）----
    public SidecarService Sidecar { get; private set; } = null!;
    public OcrService Ocr { get; private set; } = null!;
    public VersionService Versions { get; private set; } = null!;
    public LinkService Links { get; private set; } = null!;
    public CloudSyncService CloudSync { get; private set; } = null!;
    public CopyInheritService CopyInherit { get; private set; } = null!;

    // ---- P2 服务（需求 7.P2）----
    public GraphService Graph { get; private set; } = null!;
    public ImageVectorService ImageVectors { get; private set; } = null!;
    public CollaborationService Collaboration { get; private set; } = null!;
    public PluginHost Plugins { get; private set; } = null!;

    // ---- 待办提醒服务 ----
    public ReminderService Reminders { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // P2：后台服务模式（FileMemo.exe --service）——不创建任何窗口，仅后台追踪
        if (BackgroundServiceHost.IsServiceMode(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = BackgroundServiceHost.Run(e.Args);
            Environment.Exit(code);
            return;
        }

        // 全局异常兜底：UI 线程异常记录并吞掉，避免一个未处理异常直接杀进程
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        // ---- 数据层：关键子系统，失败可用内存兜底，但必须不影响主界面显示 ----
        try
        {
            Settings = SettingsService.Load();
        }
        catch (Exception ex)
        {
            LogCrash("Settings.Load", ex);
            try { Settings = new SettingsService(); } catch { /* 极端兜底 */ }
        }

        // ---- 外观主题：必须早于任何窗口创建，让窗口初次显示就是正确配色 ----
        try
        {
            ThemeManager.Initialize(Settings.Theme);
        }
        catch (Exception ex)
        {
            // 主题初始化失败 → 保持 Theme.Initial.xaml 里合并的默认主题(Mocha)，不影响功能
            LogCrash("Theme.Initialize", ex);
        }

        try
        {
            Db = new Database(Settings.DatabasePath);
            Db.Migrate();
            Repo = new Repository(Db);
        }
        catch (Exception ex)
        {
            LogCrash("Database", ex);
        }

        // ---- 服务层：逐项隔离，任何一项失败都不阻断其余功能与主界面 ----
        try { Everything = new EverythingSearchService(Settings.EverythingPath); }
        catch (Exception ex) { LogCrash("Everything", ex); }

        try { BallConfig = new BallConfigService(); }
        catch (Exception ex) { LogCrash("BallConfig", ex); }

        try { BallWindows = new WindowRegistry(); }
        catch (Exception ex) { LogCrash("WindowRegistry", ex); }

        try { Hotkeys = new HotkeyService(); }
        catch (Exception ex) { LogCrash("Hotkeys", ex); }

        try
        {
            _clipboard = new ClipboardMonitorService(Repo, Settings);
            if (Settings.ClipboardEnabled) _clipboard.Start();
        }
        catch (Exception ex) { LogCrash("Clipboard", ex); }

        try
        {
            // 文件监听：内部已接入 USN Journal 实时订阅（P1），并保留 FileSystemWatcher 兜底
            _watcher = new FileWatcherService(Repo, Settings);
            _watcher.Start();
        }
        catch (Exception ex) { LogCrash("Watcher", ex); }

        // ---- P1 服务：同样逐项隔离，任一失败不影响主界面 ----
        try { Sidecar = new SidecarService(Repo, Settings); }
        catch (Exception ex) { LogCrash("Sidecar", ex); }

        try { Ocr = new OcrService(Settings); }
        catch (Exception ex) { LogCrash("Ocr", ex); }

        try { Versions = new VersionService(Repo); }
        catch (Exception ex) { LogCrash("Versions", ex); }

        try { Links = new LinkService(Repo); }
        catch (Exception ex) { LogCrash("Links", ex); }

        try { CopyInherit = new CopyInheritService(Repo, Settings); }
        catch (Exception ex) { LogCrash("CopyInherit", ex); }

        try
        {
            if (Db != null) CloudSync = new CloudSyncService(Db, Settings);
        }
        catch (Exception ex) { LogCrash("CloudSync", ex); }

        // ---- P2 服务：同样逐项隔离 ----
        try { Graph = new GraphService(Repo); }
        catch (Exception ex) { LogCrash("Graph", ex); }

        try { ImageVectors = new ImageVectorService(Repo, Settings); }
        catch (Exception ex) { LogCrash("ImageVectors", ex); }

        try { Collaboration = new CollaborationService(Repo, Settings); }
        catch (Exception ex) { LogCrash("Collaboration", ex); }

        try
        {
            if (Settings.PluginsEnabled)
            {
                Plugins = new PluginHost(Repo, Settings, m => LogCrash("Plugin", new Exception(m)));
                Plugins.LoadAll();
            }
        }
        catch (Exception ex) { LogCrash("Plugins", ex); }

        // ---- 待办提醒：到点弹出托盘气泡 + 提醒窗口 ----
        try
        {
            Reminders = new ReminderService(Repo, Settings);
            Reminders.TaskDue += t => Dispatcher.Invoke(() => ShowTaskReminder(t));
            Reminders.Start();
        }
        catch (Exception ex) { LogCrash("Reminders", ex); }

        try { BuildTray(); }
        catch (Exception ex) { LogCrash("Tray", ex); }

        try { BuildFloatingBall(); }
        catch (Exception ex) { LogCrash("FloatingBall", ex); }

        try { RegisterGlobalHotkeys(); }
        catch (Exception ex) { LogCrash("Hotkeys.Register", ex); }

        // ---- 主窗口：始终尝试显示；仅当主窗口本身都建不出来时才退出 ----
        try
        {
            EnsureMainWindow();
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow", ex);
            MessageBox.Show("主窗口启动失败：" + ex.Message +
                            "\n\n详细信息已写入：" + CrashLogPath(),
                            "文笺 FileMemo - 启动失败");
            Shutdown(1);
            return;
        }

        try
        {
            if (Settings.FirstRun)
            {
                Settings.FirstRun = false;
                Settings.Save();
            }
        }
        catch (Exception ex) { LogCrash("FirstRun", ex); }

        try { HandleCommandLine(e.Args); }
        catch (Exception ex) { LogCrash("CommandLine", ex); }
    }

    private void RegisterGlobalHotkeys()
    {
        if (_hotkeysRegistered) return;
        _hotkeysRegistered = true;
        if (Hotkeys == null) return;

        var s = Settings;
        // 对资源管理器/桌面选中文件呼出备注弹窗（新增核心入口）
        Hotkeys.Register(s.HotkeyAddAnnotation, () => Dispatcher.Invoke(SafeShowAnnotationPopup));
        Hotkeys.Register(s.HotkeyQuickNote, () => Dispatcher.Invoke(SafeShowQuickNote));
        Hotkeys.Register(s.HotkeyClipboard, () => Dispatcher.Invoke(SafeShowClipboardPanel));
    }

    private void HandleCommandLine(string[] args)
    {
        // --annotate <path>：资源管理器右键菜单直接呼出备注弹窗
        int ai = Array.IndexOf(args, "--annotate");
        if (ai >= 0)
        {
            var path = ai + 1 < args.Length ? args[ai + 1] : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                ShowAnnotationPopup(new[] { path });
                return;
            }
        }
        if (args.Contains("--quick")) ShowQuickNote();
        if (args.Contains("--clip")) ShowClipboardPanel();
    }

    /// <summary>
    /// 快捷键呼出：对资源管理器/桌面当前选中项直接备注 / 查看已有备注。
    /// 无选中项时依次回退：剪贴板中的文件列表 → 手动输入路径对话框。
    /// </summary>
    private void SafeShowAnnotationPopup()
    {
        try { ShowAnnotationPopup(); }
        catch (Exception ex) { LogCrash("ShowAnnotationPopup", ex); }
    }

    public void ShowAnnotationPopup()
    {
        var paths = Interop.ExplorerSelectionService.GetSelectedPaths();

        if (paths.Count == 0)
        {
            // 回退 1：剪贴板里的文件拖放列表
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                    paths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
            }
            catch { }
        }

        if (paths.Count == 0)
        {
            // 回退 2：手动输入路径
            var dlg = new AddAnnotationDialog();
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FilePath))
                ShowAnnotationPopup(new[] { dlg.FilePath });
            return;
        }

        ShowAnnotationPopup(paths);
    }

    public void ShowAnnotationPopup(IReadOnlyList<string> paths)
        => RegisterNote(new FloatingNoteWindow(paths));

    private void SafeShowQuickNote()
    {
        try { ShowQuickNote(); } catch (Exception ex) { LogCrash("QuickNote", ex); }
    }

    private void SafeShowClipboardPanel()
    {
        try { ShowClipboardPanel(); } catch (Exception ex) { LogCrash("ClipboardPanel", ex); }
    }

    /// <summary>
    /// 加载应用图标（托盘/通知区域使用）：优先读取本进程可执行文件内嵌的图标，
    /// 即 csproj 中 &lt;ApplicationIcon&gt; 指定的 Assets\appicon.ico（已去掉蓝色底纹的“文件夹”图标）。
    /// 失败时回退到系统默认应用图标，避免托盘图标显示为通用图标或空白。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico is not null) return ico;
            }
        }
        catch { }
        try
        {
            var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(loc) && System.IO.File.Exists(loc))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(loc);
                if (ico is not null) return ico;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void BuildTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "文笺 FileMemo"
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowMainWindow();
        }));
        menu.Items.Add("对选中文件备注", null, (_, _) => Dispatcher.Invoke(SafeShowAnnotationPopup));
        menu.Items.Add("快速便签", null, (_, _) => Dispatcher.Invoke(SafeShowQuickNote));
        menu.Items.Add("剪贴板", null, (_, _) => Dispatcher.Invoke(SafeShowClipboardPanel));
        menu.Items.Add("桌面速览", null, (_, _) => Dispatcher.Invoke(ShowWidget));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Shutdown));
        _tray.ContextMenuStrip = menu;
        // 单击（左键）托盘图标即打开主页面；右键仍弹出上下文菜单。
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.Invoke(ShowMainWindow);
        };
    }

    public void ShowQuickNote() => RegisterNote(new QuickNoteWindow());    /// <summary>快速待办：悬浮图标「待办」单击呼出的轻量录入框（QuickTaskWindow）。</summary>
    public void ShowQuickTask() => RegisterNote(new QuickTaskWindow());
    public void ShowClipboardPanel() => new ClipboardPanelWindow().Show();
    public void ShowWidget() => RegisterNote(new WidgetWindow());

    /// <summary>待办到点：托盘气泡提示 + 弹出提醒窗口（可稍后提醒 / 标记完成）。</summary>
    private void ShowTaskReminder(Models.TaskItem t)
    {
        try
        {
            _tray?.ShowBalloonTip(5000, "待办提醒",
                string.IsNullOrWhiteSpace(t.Title) ? "(无标题待办)" : t.Title,
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }

        try
        {
            var w = new Views.ReminderWindow(t) { Topmost = true };
            w.Show();
            w.Activate();
        }
        catch (Exception ex) { LogCrash("ShowTaskReminder", ex); }
    }

    /// <summary>打开 / 关闭主页：主窗口可见时关闭（或按「最小化到托盘」设置隐藏），否则唤出。供悬浮图标单击使用。</summary>
    public void ToggleMainWindow()
    {
        var win = _main;
        if (win != null && win.IsVisible)
        {
            // OnClosing 中若启用「最小化到托盘」会转为 Hide；否则真正关闭，_main 由 Closed 事件清空。
            try { win.Close(); } catch { }
            return;
        }
        EnsureMainWindow();
    }

    /// <summary>
    /// 创建（必要时重建）主窗口并显示 / 激活。
    /// 窗口被真正关闭后也能重新唤回，供托盘、悬浮球、全局快捷键统一调用。
    /// </summary>
    public MainWindow EnsureMainWindow()
    {
        if (_main == null)
        {
            _main = new MainWindow();
            MainWindow = _main;
            _main.Closed += (_, _) => _main = null;
        }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        return _main;
    }

    public void ShowMainWindow() => EnsureMainWindow();

    /// <summary>打开主窗口并切换到指定导航分区（便签 / 待办 / 文件树备注等），供悬浮球图标直接打开对应页。</summary>
    public void ShowMainWindow(NavSection section) => EnsureMainWindow().NavigateTo(section);

    /// <summary>登记便签类浮窗，便于统一收纳 / 释放 / 最小化；窗口关闭时自动移除。</summary>
    private T RegisterNote<T>(T w) where T : Window
    {
        BallWindows.Register(w);
        w.Show();
        return w;
    }

    // ---------------- 悬浮球 + 批量便签操作 ----------------
    public void BuildFloatingBall()
    {
        if (_ball != null) return;
        if (!BallConfig.Current.Enabled) return;
        _ball = new FloatingBallWindow();
        _ball.Closed += (_, _) => _ball = null;
        _ball.Show();
    }

    public void CloseFloatingBall()
    {
        try { _ball?.Close(); } catch { }
        _ball = null;
    }

    /// <summary>设置项变更后重建悬浮球，使个数 / 颜色 / 尺寸等即时生效。</summary>
    public void RebuildFloatingBall()
    {
        try
        {
            BallConfig.Save();
            CloseFloatingBall();
            BuildFloatingBall();
        }
        catch (Exception ex) { LogCrash("RebuildFloatingBall", ex); }
    }

    /// <summary>
    /// 悬浮球屏幕中心（DIP），用于便签收纳动画的收拢方向。
    /// 主球未创建时按新规格回退到「屏幕右上角」的默认位置（right:28, top:30，球径 46）。
    /// </summary>
    public Point BallCenterScreen()
    {
        // 悬浮球窗口画布为 240×240，球心在画布中心（Center=120），并叠加「贴边滑出」位移。
        if (_ball != null) return _ball.BallCenterDip;

        double center = Views.FloatingBallWindow.Center;
        var wa = SystemParameters.WorkArea;
        // 与 FloatingBallWindow.RestorePosition 的默认落点保持一致：贴屏幕右上角的「小凸起」。
        return new Point(wa.Right - 14, wa.Top + 40 + center);
    }

    /// <summary>一键收纳全部便签（向悬浮球收拢并隐藏）。</summary>
    public void CollapseAllNotes() => BallWindows.CollapseAll(BallCenterScreen());

    /// <summary>释放全部便签（反向动画重新显示）。</summary>
    public void ReleaseAllNotes() => BallWindows.ReleaseAll();

    /// <summary>最小化所有便签。</summary>
    public void MinimizeAllNotes() => BallWindows.MinimizeAll();

    /// <summary>收纳 / 释放切换（小红点长按）。</summary>
    public void ToggleCollapseNotes()
    {
        if (BallWindows.AnyVisible()) CollapseAllNotes();
        else ReleaseAllNotes();
    }

    /// <summary>供退出特效使用：关闭托盘并结束应用。</summary>
    public new void Shutdown()
    {
        _shuttingDown = true;
        try { _tray?.Dispose(); } catch { }
        Shutdown(0);
    }

    // ---------------- 崩溃日志 ----------------
    private static string CrashLogPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperNote");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "crash.log");
        }
        catch { return "crash.log"; }
    }

    private static void LogCrash(string stage, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] stage={stage}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(CrashLogPath(), sb.ToString(), Encoding.UTF8);
        }
        catch { /* 记录失败不得再抛异常 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _clipboard?.Dispose(); } catch { }
        try { Reminders?.Dispose(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        try { Hotkeys?.Dispose(); } catch { }
        try { _ball?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { Plugins?.Shutdown(); } catch { }
        try { Db?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
