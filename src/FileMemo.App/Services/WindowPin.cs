using System.Windows;

namespace FileMemo.App.Services;

/// <summary>展开面板打开的窗口种类。</summary>
public enum PinKind { QuickNote, QuickTask, Clipboard, FileNote }

/// <summary>
/// 展开面板四个入口窗口的「窗口固定」辅助：
/// 固定后窗口置顶（Topmost）显示，且不参与主球的批量收纳 / 最小化 / 释放；状态持久化到设置。
/// </summary>
public static class WindowPin
{
    /// <summary>窗口加载时按已保存的固定状态应用（固定则置顶）。</summary>
    public static void ApplyOnLoad(Window w, PinKind kind)
    {
        try
        {
            if (Get(kind)) App.Instance.BallWindows.SetPinned(w, true);
        }
        catch { }
    }

    /// <summary>切换固定状态。</summary>
    public static void Toggle(Window w, PinKind kind) => Apply(w, kind, !IsPinned(w));

    public static bool IsPinned(Window w)
    {
        try { return App.Instance.BallWindows.IsPinned(w); }
        catch { return false; }
    }

    /// <summary>设置固定状态：更新注册表 + 置顶 + 持久化。</summary>
    public static void Apply(Window w, PinKind kind, bool pinned)
    {
        try
        {
            App.Instance.BallWindows.SetPinned(w, pinned);
            Set(kind, pinned);
            App.Instance.Settings.Save();
        }
        catch { }
    }

    private static bool Get(PinKind k) => k switch
    {
        PinKind.QuickNote => App.Instance.Settings.PinQuickNote,
        PinKind.QuickTask => App.Instance.Settings.PinQuickTask,
        PinKind.Clipboard => App.Instance.Settings.PinClipboard,
        _ => App.Instance.Settings.PinFileNote,
    };

    private static void Set(PinKind k, bool v)
    {
        switch (k)
        {
            case PinKind.QuickNote: App.Instance.Settings.PinQuickNote = v; break;
            case PinKind.QuickTask: App.Instance.Settings.PinQuickTask = v; break;
            case PinKind.Clipboard: App.Instance.Settings.PinClipboard = v; break;
            default: App.Instance.Settings.PinFileNote = v; break;
        }
    }
}
