using System.IO;
using System.Windows.Media.Imaging;
using SuperNote.App.Data;
using SuperNote.App.Interop;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 剪贴板随记（需求 3.2）：监听系统剪贴板，记录文本/图片/文件路径/HTML，
/// 自动去重、敏感应用过滤、分级保留、敏感内容加密。
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;
    private readonly MessageWindow _messageWindow;

    public event Action<Clip>? ClipCaptured;

    public ClipboardMonitorService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
        _messageWindow = new MessageWindow();
    }

    public void Start()
    {
        NativeMethods.AddClipboardFormatListener(_messageWindow.Handle);
        _messageWindow.Message += OnMessage;
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            // 延迟读取，避免与其他应用争用剪贴板
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(CaptureCurrent),
                System.Windows.Threading.DispatcherPriority.Background);
            return true;
        }
        return false;
    }

    private void CaptureCurrent()
    {
        try
        {
            string sourceApp = GetForegroundApp();

            // 敏感应用排除列表
            if (_settings.ClipboardExcludedApps.Any(a =>
                    sourceApp.Contains(a, StringComparison.OrdinalIgnoreCase)))
                return;

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                var paths = files.Cast<string>().ToList();
                Save(ClipKind.FileDrop, string.Join("\n", paths), sourceApp, null);
                return;
            }

            if (System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                var path = SaveImage(img);
                Save(ClipKind.Image, "[图片]", sourceApp, path, RunOcrIfEnabled(path));
                return;
            }

            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                Save(ClipKind.Text, text, sourceApp, null);
            }
        }
        catch { /* 剪贴板偶发占用，忽略本次 */ }
    }

    private string? SaveImage(BitmapSource? img)
    {
        if (img == null) return null;
        try
        {
            var file = Path.Combine(_settings.AttachmentDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            using var fs = File.Create(file);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            enc.Save(fs);
            return file;
        }
        catch { return null; }
    }

    /// <summary>P1：图片落盘后异步 OCR，把识别文本一并入库参与搜索。</summary>
    private string RunOcrIfEnabled(string? imagePath)
    {
        try
        {
            if (!_settings.OcrEnabled || string.IsNullOrEmpty(imagePath)) return "";
            var ocr = App.Instance?.Ocr;
            if (ocr == null || !ocr.IsAvailable) return "";
            return ocr.RecognizeToTextAsync(imagePath).GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    private void Save(ClipKind kind, string content, string sourceApp, string? imagePath, string ocrText = "")
    {
        var raw = content + "|" + imagePath;
        var hash = CryptoService.TextHash(raw);

        // 自动去重：与既有相同内容则跳过
        if (_repo.ClipHashExists(hash)) return;

        var clip = new Clip
        {
            Kind = kind,
            // 敏感数据加密存储（需求：剪贴板强制加密）
            Content = CryptoService.Encrypt(content, _settings.ClipboardEncrypt),
            ImagePath = imagePath,
            SourceApp = sourceApp,
            ContentHash = hash,
            OcrText = ocrText,
            CreatedAt = DateTime.Now
        };
        _repo.InsertClip(clip);

        // 超限裁剪（保留 pinned）
        int keep = Math.Clamp(_settings.ClipboardRetention, 100, 10000);
        _repo.TrimClips(keep);

        ClipCaptured?.Invoke(clip);
    }

    private static string GetForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public void Dispose()
    {
        NativeMethods.RemoveClipboardFormatListener(_messageWindow.Handle);
        _messageWindow.Dispose();
    }
}
