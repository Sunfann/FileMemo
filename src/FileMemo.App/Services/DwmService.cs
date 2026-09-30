using System;
using System.Runtime.InteropServices;

namespace FileMemo.App.Services;

/// <summary>
/// Desktop Window Manager 互操作：让原生标题栏跟随深色主题，并在 Win11 上启用 Mica 背景。
///
/// 设计原则：**绝不抛异常**。所有调用失败（老系统不支持该属性、窗口已销毁等）
/// 都静默降级，由调用方用主题纯色兜底。
/// </summary>
internal static class DwmService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int size);

    // ---- 属性 ID（随 Windows 版本引入，老系统调用会返回非 0，忽略即可）----
    /// <summary>沉浸式深色模式：让原生标题栏 / 系统菜单变深（Win10 1809+）。</summary>
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>窗口圆角偏好（Win11）。</summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>系统背景材质类型（Win11 22H2+）。</summary>
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // ---- 取值 ----
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_AUTO = 0;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt

    /// <summary>Win11 起始构建号（22000）。</summary>
    public static bool IsWindows11OrGreater
    {
        get
        {
            try { return Environment.OSVersion.Version.Build >= 22000; }
            catch { return false; }
        }
    }

    /// <summary>Win11 22H2+（22621）才支持 DWMWA_SYSTEMBACKDROP_TYPE。</summary>
    public static bool SupportsSystemBackdrop
    {
        get
        {
            try { return Environment.OSVersion.Version.Build >= 22621; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 让原生标题栏跟随深色主题。所有窗口都可以安全调用（包括有系统边框的普通窗口）。
    /// </summary>
    public static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, v);
    }

    /// <summary>
    /// 给主窗口整体套用外观：深色标题栏 + Win11 圆角 + Mica 背景。
    /// 返回是否成功启用了系统背景材质（调用方据此决定窗口底色用透明还是主题纯色）。
    /// </summary>
    public static bool ApplyBackdrop(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 1) 标题栏深色（Win10 1809+ 支持，最基础的一项，尽量成功）
        SetImmersiveDarkMode(hwnd, dark);

        // 2) 系统背景材质（仅 Win11 22H2+）
        if (!SupportsSystemBackdrop) return false;

        // 圆角：Win11 交给系统处理，避免自绘圆角与系统阴影打架
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // Mica：仅在深色/浅色都能取得较好效果；失败则回退 Acrylic，再失败则放弃
        if (TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW)) return true;
        if (TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)) return true;

        return false;
    }

    /// <summary>关闭系统背景材质（回到不透明纯色）。</summary>
    public static void ClearBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (SupportsSystemBackdrop)
            TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DEFAULT);
    }

    /// <summary>
    /// 给「保留系统标题栏」的普通窗口套用深色标题栏 + Win11 圆角。
    /// 供 GraphWindow / ClipboardPanelWindow / AddAnnotationDialog 等复用，
    /// 避免每个窗口各写一遍 P/Invoke。null 窗口安全。
    /// </summary>
    public static void ApplyShellTheme(System.Windows.Window? window)
    {
        if (window is null) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            SetImmersiveDarkMode(hwnd, ThemeManager.IsDark);
            if (IsWindows11OrGreater)
                TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        }
        catch { /* 外观优化失败不影响窗口功能 */ }
    }

    /// <summary>设置调用，返回是否成功（hr == 0）。任何异常都不外抛。</summary>
    private static bool TrySet(IntPtr hwnd, int attr, int value)
    {
        try
        {
            int v = value;
            return DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }
}
