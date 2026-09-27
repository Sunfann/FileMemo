using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SuperNote.App.Interop;

/// <summary>
/// 读取 Windows 资源管理器 / 桌面“当前选中项”的完整路径
/// （需求 3.4.1：全局快捷键对当前选中文件/文件夹添加备注）。
///
/// 桌面（Progman/WorkerW 承载的 SysListView32）不属于 Shell.Application.Windows() 集合，
/// 必须单独处理。本实现提供三级路径，任一可用即返回：
///   1) Shell.Application.FindWindowSW(SWC_DESKTOP) 取桌面 Shell 视图 → Document.SelectedItems()
///      —— 修正了历史 bug：pHWND 为 [out] LONG*，必须用可回写的占位数组元素封送，
///         直接传普通 int 会导致调用失败、桌面永远取不到路径而回退手动输入框。
///   2) Win32 直读桌面 SysListView32（跨进程 LVM_GETNEXTITEM/LVM_GETITEMTEXTW），
///      再把名称经 Shell NameSpace(ssfDESKTOP).ParseName 解析为完整路径。
///   3) 资源管理器窗口枚举（优先前台窗口）。
/// </summary>
public static class ExplorerSelectionService
{
    // ShellWindowClass：SWC_DESKTOP = 8；SWFO_NEEDDISPATCH = 1
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const int SSF_DESKTOP = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>获取当前选中项的路径列表；无选中时返回空列表（由上层回退）。</summary>
    public static IReadOnlyList<string> GetSelectedPaths()
    {
        bool foregroundIsDesktop = IsDesktopForeground();

        if (foregroundIsDesktop)
        {
            var d = GetDesktopViaShell();
            if (d.Count > 0) return d;
            var w = GetDesktopViaListView();
            if (w.Count > 0) return w;
            return GetExplorerSelectedPaths();
        }

        // 前台是资源管理器窗口：优先读它，避免误用桌面上残留的选中状态
        var ex = GetExplorerSelectedPaths();
        if (ex.Count > 0) return ex;

        var ds = GetDesktopViaShell();
        if (ds.Count > 0) return ds;
        return GetDesktopViaListView();
    }

    private static bool IsDesktopForeground()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var sb = new StringBuilder(256);
            GetClassName(fg, sb, sb.Capacity);
            var cls = sb.ToString();
            return cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
        }
        catch { return false; }
    }

    /// <summary>路径 1：Shell.Application.FindWindowSW(SWC_DESKTOP) 读取桌面选中项。</summary>
    private static List<string> GetDesktopViaShell()
    {
        var result = new List<string>();
        object? shell = null;
        object? view = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;

            shell = Activator.CreateInstance(shellType);
            if (shell == null) return result;

            // FindWindowSW(pvarLoc, pvarRoot, swClass, pHWND(out), swfwOptions)
            // pHWND 是 [out] LONG*：必须以“可回写的占位元素”参与晚期绑定。
            // 历史 bug 是直接塞入普通 int 1，导致 byref 封送失败、调用抛异常 → 桌面取不到。
            object hwndHolder = 0;
            object?[] args = { Type.Missing, Type.Missing, SWC_DESKTOP, hwndHolder, SWFO_NEEDDISPATCH };
            view = shellType.InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, shell, args);

            if (view != null)
                result.AddRange(ReadSelected(view));
        }
        catch { /* 桌面 Shell 视图不可用时返回空，由后续路径兜底 */ }
        finally
        {
            Release(view);
            Release(shell);
        }
        return result;
    }

    /// <summary>路径 2：Win32 直读桌面列表视图，再解析为完整路径。</summary>
    private static List<string> GetDesktopViaListView()
    {
        var result = new List<string>();
        try
        {
            var names = NativeMethods.GetDesktopSelectedNames();
            if (names.Count == 0) return result;

            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic? shell = shellType != null ? Activator.CreateInstance(shellType) : null;
            dynamic? desktopNS = null;
            try { if (shell != null) desktopNS = shell.NameSpace(SSF_DESKTOP); } catch { }

            foreach (var name in names)
            {
                string? full = null;
                // 优先让 Shell 解析（可正确处理“此电脑”“回收站”等虚拟项的 Path）
                if (desktopNS != null)
                {
                    try
                    {
                        dynamic? fi = desktopNS.ParseName(name);
                        if (fi != null) full = fi.Path as string;
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(full))
                    full = ResolveDesktopName(name);

                if (!string.IsNullOrEmpty(full)) result.Add(full);
            }
        }
        catch { }
        return result;
    }

    private static string? ResolveDesktopName(string name)
    {
        try
        {
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            foreach (var dir in new[] { userDesktop, publicDesktop })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var p = System.IO.Path.Combine(dir, name);
                if (System.IO.File.Exists(p) || System.IO.Directory.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    /// <summary>路径 3：枚举资源管理器窗口并读取选中项（优先前台窗口）。</summary>
    private static List<string> GetExplorerSelectedPaths()
    {
        var result = new List<string>();
        object? shell = null;
        dynamic? windows = null;
        try
        {
            IntPtr fg = GetForegroundWindow();
            int fgHwnd = fg.ToInt32();

            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;

            shell = Activator.CreateInstance(shellType);
            if (shell == null) return result;

            windows = ((dynamic)shell).Windows();   // ShellWindows 集合
            int count = windows.Count;

            List<string>? fallback = null;
            for (int i = 0; i < count; i++)
            {
                object? win = null;
                try
                {
                    win = windows.Item(i);

                    // 仅处理资源管理器窗口（排除 IE / 其他承载 ShellWindows 的进程）
                    string? fullName = null;
                    try { fullName = ((dynamic)win).FullName as string; } catch { }
                    if (fullName == null ||
                        !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int hwnd = 0;
                    try { hwnd = (int)((dynamic)win).HWND; } catch { }
                    bool isForeground = hwnd == fgHwnd;

                    var paths = ReadSelected(win);
                    if (paths.Count == 0) continue;

                    if (isForeground) { result = paths; break; }
                    fallback ??= paths;
                }
                catch { /* 单个窗口读取失败，跳过 */ }
                finally { Release(win); }
            }

            if (result.Count == 0 && fallback != null) result = fallback;
        }
        catch { }
        finally
        {
            Release(windows);
            Release(shell);
        }
        return result;
    }

    private static List<string> ReadSelected(object win)
    {
        var paths = new List<string>();
        object? document = null;
        object? selected = null;
        try
        {
            document = ((dynamic)win).Document;
            selected = ((dynamic)document).SelectedItems();
            int n = ((dynamic)selected).Count;
            for (int j = 0; j < n; j++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)selected).Item(j);
                    string? p = ((dynamic)item).Path as string;
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
                finally { Release(item); }
            }
        }
        catch { }
        finally
        {
            Release(selected);
            Release(document);
        }
        return paths;
    }

    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }
}
