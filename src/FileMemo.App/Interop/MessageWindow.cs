using System.Windows.Interop;

namespace FileMemo.App.Interop;

/// <summary>
/// 隐藏的 WPF 消息窗口：用于承载 RegisterHotKey / AddClipboardFormatListener 的
/// WM_HOTKEY / WM_CLIPBOARDUPDATE 消息。
/// 相比 WinForms 窗口，HwndSource 由 WPF 创建，HwndSource.FromHwnd 可正常返回实例，
/// 钩子可靠；窗口不显示，仅用于接收窗口消息。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public IntPtr Handle => _source.Handle;

    /// <summary>消息回调：返回 true 表示已处理。参数 (msg, wParam, lParam)。</summary>
    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("SuperNote.MessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,          // 顶层但不显示
            ExtendedWindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var d = Message;
        if (d != null)
        {
            foreach (Func<int, IntPtr, IntPtr, bool> single in d.GetInvocationList())
            {
                if (single(msg, wParam, lParam))
                {
                    handled = true;
                    break;
                }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
