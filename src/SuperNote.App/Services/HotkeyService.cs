using SuperNote.App.Interop;

namespace SuperNote.App.Services;

/// <summary>
/// 全局快捷键（需求 3.4.1 / 3.10）：RegisterHotKey 注册，隐藏消息窗口接收 WM_HOTKEY。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly MessageWindow _messageWindow;
    private int _nextId = 0x5340;

    public HotkeyService()
    {
        _messageWindow = new MessageWindow();
        _messageWindow.Message += OnMessage;
    }

    public void Register(string spec, Action action)
    {
        if (!TryParse(spec, out uint mods, out uint vk)) return;
        int id = _nextId++;
        if (NativeMethods.RegisterHotKey(_messageWindow.Handle, id, mods | NativeMethods.MOD_NOREPEAT, vk))
            _handlers[id] = action;
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var act))
        {
            act();
            return true;
        }
        return false;
    }

    private static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= NativeMethods.MOD_CONTROL; break;
                case "ALT": mods |= NativeMethods.MOD_ALT; break;
                case "SHIFT": mods |= NativeMethods.MOD_SHIFT; break;
                case "WIN": mods |= NativeMethods.MOD_WIN; break;
                default:
                    vk = KeyToVk(p);
                    break;
            }
        }
        return vk != 0;
    }

    private static uint KeyToVk(string key)
    {
        if (key.Length == 1)
            return (uint)char.ToUpperInvariant(key[0]);
        if (key.StartsWith("F") && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);
        return key.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            _ => 0
        };
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        _handlers.Clear();
        _messageWindow.Dispose();
    }
}
