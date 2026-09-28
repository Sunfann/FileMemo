using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileMemo.App.Models;

namespace FileMemo.App.Views;

/// <summary>
/// 文件/文件夹备注弹窗（快捷键呼出）：
/// - 未备注过的文件 → 直接在此新建备注；
/// - 已备注过的文件 → 自动回显已有备注（含状态/标签/意图/正文/图片）；
/// - 支持多选：批量应用同一份备注；
/// - 支持粘贴 / 拖入 / 选择图片。
/// 拖动仅绑定在标题区，避免抢占状态下拉框、图片与文本框的鼠标操作。
/// </summary>
public partial class FloatingNoteWindow : Window
{
    private readonly IReadOnlyList<string> _paths;
    private readonly List<FileRef> _refs = new();
    private readonly List<string> _images = new();
    private FileRef? _primary;

    public FloatingNoteWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        _paths = paths;
        Loaded += (_, _) => { LoadNotes(); PositionNearCursor(); };
    }

    private void LoadNotes()
    {
        try
        {
            // 为每个目标建立/获取 FileRef 与指纹（保证日后重命名/移动仍能关联）
            foreach (var p in _paths)
                _refs.Add(AnnotationHelper.CreateOrGet(App.Instance.Repo, p));

            _primary = _refs[0];
            var ann = App.Instance.Repo.GetAnnotation(_primary.Id);

            if (_paths.Count == 1)
            {
                FileNameText.Text = _primary.Name;
                PathText.Text = _primary.Path;
            }
            else
            {
                FileNameText.Text = $"{_paths.Count} 个文件/文件夹";
                PathText.Text = string.Join("\n", _paths.Take(3)) + (_paths.Count > 3 ? "\n…" : "");
            }

            FingerprintText.Text = BuildFingerprintStatus(_primary);

            bool hasNote = ann != null &&
                (!string.IsNullOrWhiteSpace(ann.Content) ||
                 !string.IsNullOrWhiteSpace(ann.Tags) ||
                 !string.IsNullOrWhiteSpace(ann.Intent) ||
                 !string.IsNullOrWhiteSpace(ann.ImagesJson));
            HeaderHint.Text = hasNote ? "已有备注，可直接编辑" : "新建备注";

            // 回显：状态 / 标签 / 意图 / 正文 / 图片
            StateBox.ItemsSource = StateDefaults.States;
            StateBox.SelectedItem = ann != null ? StateDefaults.Display(ann.State) : StateDefaults.States[0];
            TagsBox.Text = ann?.Tags ?? "";
            IntentBox.Text = ann?.Intent ?? "";
            ContentBox.Text = ann?.Content ?? "";

            _images.Clear();
            if (ann != null && !string.IsNullOrWhiteSpace(ann.ImagesJson))
            {
                try
                {
                    var imgs = JsonSerializer.Deserialize<List<string>>(ann.ImagesJson);
                    if (imgs != null)
                        foreach (var im in imgs)
                            if (!string.IsNullOrWhiteSpace(im) && File.Exists(im)) _images.Add(im);
                }
                catch { /* 历史数据格式异常则忽略图片 */ }
            }
            RebuildImagePanel();

            ContentBox.Focus();
            ContentBox.CaretIndex = ContentBox.Text.Length;
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "加载备注失败：" + ex.Message;
        }
    }

    private static string BuildFingerprintStatus(FileRef fr)
    {
        var parts = new List<string> { fr.IsDir ? "文件夹" : "文件" };
        if (!fr.IsDir && fr.Size != null) parts.Add("大小 " + FileRef.FormatSize(fr.Size));
        parts.Add(fr.FileId != null ? "File ID ✓" : "File ID ✗(非 NTFS)");
        if (!string.IsNullOrEmpty(fr.VolumeGuid)) parts.Add("卷 GUID ✓");
        if (!string.IsNullOrEmpty(fr.QuickHash)) parts.Add("快速哈希 ✓");
        if (fr.Offline) parts.Add("离线");
        return string.Join(" · ", parts);
    }

    // ---------------- 图片处理 ----------------
    private void RebuildImagePanel()
    {
        ImagePanel.Children.Clear();
        foreach (var path in _images.ToList())
        {
            var thumb = TryLoadThumbnail(path);
            var img = new Image { Source = thumb, Width = 72, Height = 72, Stretch = Stretch.Uniform };

            var border = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderBrush = (Brush)FindResource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("SurfaceAltBrush"),
                Cursor = Cursors.Hand,
                ToolTip = "点击移除：" + path,
                Child = img,
            };
            var captured = path;
            border.MouseLeftButtonUp += (_, _) => { _images.Remove(captured); RebuildImagePanel(); };
            ImagePanel.Children.Add(border);
        }
        ImageStrip.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapSource? TryLoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 144;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void AddImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!_images.Contains(path, StringComparer.OrdinalIgnoreCase)) _images.Add(path);
        RebuildImagePanel();
    }

    private string SaveClipboardImage(BitmapSource img)
    {
        var file = Path.Combine(App.Instance.Settings.AttachmentDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        using var fs = File.Create(file);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(fs);
        return file;
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff";
    }

    private void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img != null) AddImage(SaveClipboardImage(img));
                return;
            }
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                foreach (var f in System.Windows.Clipboard.GetFileDropList())
                    if (f != null && IsImageFile(f)) AddImage(f);
                return;
            }
            HeaderHint.Text = "剪贴板里没有图片";
        }
        catch (Exception ex) { HeaderHint.Text = "粘贴图片失败：" + ex.Message; }
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff"
        };
        if (dlg.ShowDialog() == true) AddImage(dlg.FileName);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var img = System.Windows.Clipboard.GetImage();
                    if (img != null) { AddImage(SaveClipboardImage(img)); e.Handled = true; }
                }
                else if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    bool any = false;
                    foreach (var f in System.Windows.Clipboard.GetFileDropList())
                        if (f != null && IsImageFile(f)) { AddImage(f); any = true; }
                    if (any) e.Handled = true;
                }
            }
            catch { }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files)
                        if (IsImageFile(f)) AddImage(f);
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img != null) AddImage(SaveClipboardImage(img));
            }
        }
        catch { }
    }

    /// <summary>
    /// 弹窗出现在鼠标附近并做屏幕边界钳制（贴合“就地呼出”的体验）。
    /// WPF 的 Left/Top 为逻辑像素(DIP)，而 WinForms 的 Cursor/Screen 为物理像素，
    /// 高 DPI 下必须按缩放比换算，否则会偏移甚至跑到屏幕外。
    /// </summary>
    private void PositionNearCursor()
    {
        try
        {
            var pos = System.Windows.Forms.Cursor.Position;
            var area = System.Windows.Forms.Screen.FromPoint(pos).WorkingArea;

            double scaleX = 1.0, scaleY = 1.0;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                scaleX = src.CompositionTarget.TransformToDevice.M11;
                scaleY = src.CompositionTarget.TransformToDevice.M22;
            }
            if (scaleX <= 0) scaleX = 1.0;
            if (scaleY <= 0) scaleY = 1.0;

            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;

            // 物理像素 → DIP
            double left = pos.X / scaleX + 16;
            double top = pos.Y / scaleY + 16;
            double right = area.Right / scaleX;
            double bottom = area.Bottom / scaleY;
            double areaLeft = area.Left / scaleX;
            double areaTop = area.Top / scaleY;

            if (left + w > right) left = right - w - 8;
            if (top + h > bottom) top = bottom - h - 8;
            if (left < areaLeft) left = areaLeft + 8;
            if (top < areaTop) top = areaTop + 8;

            Left = left;
            Top = top;
        }
        catch { /* 定位失败则使用默认位置 */ }
    }

    // 仅标题区可拖动窗口
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private AnnotationState CurrentState()
    {
        if (StateBox.SelectedItem is string st)
        {
            int idx = Array.IndexOf(StateDefaults.States, st);
            if (idx >= 0) return (AnnotationState)idx;
        }
        return AnnotationState.Draft;
    }

    /// <summary>
    /// “完成”语义：保存成功后按需求自动关闭弹窗；多选时对所有目标应用同一份备注。
    /// </summary>
    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (SaveNotes()) Close();
    }

    /// <summary>保存（不关闭），保留给需要“保存并继续编辑”的场景。</summary>
    private void Save_Click(object sender, RoutedEventArgs e) => SaveNotes();

    private bool SaveNotes()
    {
        try
        {
            var state = CurrentState();
            var imagesJson = JsonSerializer.Serialize(_images);
            foreach (var fr in _refs)
            {
                var ann = App.Instance.Repo.GetAnnotation(fr.Id) ?? new Annotation { FileRefId = fr.Id };
                ann.Content = ContentBox.Text;
                ann.Tags = TagsBox.Text;
                ann.Intent = IntentBox.Text;
                ann.State = state;
                ann.ImagesJson = imagesJson;
                App.Instance.Repo.UpsertAnnotation(ann);
                App.Instance.Repo.AddTimeline("file", fr.Id, "edited", "快捷键弹窗快速编辑");

                // P1：写出 sidecar（移动盘 / NAS 场景的权威副本）
                try { App.Instance.Sidecar?.Write(fr, ann); } catch { }
                // P1：解析内容里的 [[双向链接]] 并同步出边
                try { App.Instance.Links?.SyncOutgoing(fr.Id, ann.Content ?? ""); } catch { }
            }
            HeaderHint.Text = _refs.Count > 1 ? $"已保存（{_refs.Count} 项）✓" : "已保存 ✓";
            return true;
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "保存失败：" + ex.Message;
            return false;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_primary == null) return;
        try { Process.Start(new ProcessStartInfo(_primary.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_primary == null) return;
        try { Process.Start("explorer.exe", "/select,\"" + _primary.Path + "\""); }
        catch (Exception ex) { MessageBox.Show("定位失败：" + ex.Message); }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw) { mw.Show(); mw.Activate(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
