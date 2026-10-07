using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FileMemo.App.Data;
using FileMemo.App.Models;
using FileMemo.App.Services;
using FileMemo.App.ViewModels;

namespace FileMemo.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // 当前导航分区：用于防止记录列表的选中事件在非「便签」分区下覆盖右侧详情面板
    private NavSection _section = NavSection.Notes;

    // 防止「同步日期选择器」时再次触发 SelectedDateChanged 造成回环
    private bool _syncingRemindDate;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Instance.Repo);
        DataContext = _vm;
        // 「转为便签」等操作后，由视图模型请求导航到对应分区
        _vm.NavigateRequested += s => NavigateTo(s);
        Loaded += OnLoaded;
    }

    // ============================================================
    //  外观：DWM 背板（Mica）+ 窗口标题栏明暗跟随
    // ============================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DwmService.SetImmersiveDarkMode(hwnd, ThemeManager.IsDark);

            // Win11 上用 Mica 系统背板；此时窗口自身背景必须透明才能透出背板。
            // 若系统不支持（Win10 / 不满足 build 要求），保持不透明底色，避免出现全透明窗口。
            if (DwmService.ApplyBackdrop(hwnd, ThemeManager.IsDark))
            {
                Background = Brushes.Transparent;
            }
            else
            {
                Background = (Brush)FindResource("WindowBackdropFallbackBrush");
            }

            // 主题切换时同步原生标题栏明暗与窗口底色
            ThemeManager.ThemeChanged += OnThemeChanged;
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow.OnSourceInitialized", ex);
        }
    }

    private void OnThemeChanged()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            DwmService.SetImmersiveDarkMode(hwnd, ThemeManager.IsDark);

            // 不支持 Mica 时底色由资源驱动；支持 Mica 时保持透明
            if (!DwmService.SupportsSystemBackdrop)
                Background = (Brush)FindResource("WindowBackdropFallbackBrush");
        }
        catch { /* 主题切换失败不应影响主流程 */ }
    }

    /// <summary>
    /// 最大化时限制高度，避免 WPF 无边框窗口（WindowChrome）盖住任务栏。
    /// 这是自绘标题栏 + WindowChrome 的必写项，否则最大化会遮住任务栏。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
                // 最大化图标切换为「还原」
                BtnMaxIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "IconRestore");
                BtnMax.ToolTip = "向下还原";
            }
            else
            {
                MaxHeight = double.PositiveInfinity;
                BtnMaxIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "IconMaximize");
                BtnMax.ToolTip = "最大化";
            }
        }
        catch { /* 图标切换失败不影响窗口行为 */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// 关闭主窗口默认只隐藏到托盘（而非真正销毁），
    /// 保证托盘菜单、悬浮球「打开主页面」仍能唤回；
    /// 只有走托盘「退出」（App 进入关闭流程）时才真正关闭。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (!App.Instance.IsShuttingDown && App.Instance.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
        }
        catch { /* 关闭流程异常不应阻断退出 */ }
        base.OnClosing(e);
    }

    private static void LogCrash(string stage, Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileMemo-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {ex}\n\n");
        }
        catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { SettingsNav.SelectedIndex = 0; }   // 触发 SettingsNav_SelectionChanged → 构建「通用」分类
        catch (Exception ex) { LogCrash("InitSettingsNav", ex); }
        try { InitializeThemeGallery(); }
        catch (Exception ex) { LogCrash("InitializeThemeGallery", ex); }
        // 全局快捷键已在 App.OnStartup 集中注册（含备注弹窗），此处不再重复注册，避免冲突
        NavList.SelectedIndex = 0;
        SwitchPanels(NavSection.Notes);
        RefreshRecordDetail();

        // 订阅文件监听的数据变化通知：后台发现文件移动/重命名/删除时，
        // 自动刷新统一列表与文件树，让备注及时跟随到新路径（修复"移动后备注丢失、无法跟踪"）。
        try
        {
            var watcher = App.Instance.Watcher;
            if (watcher != null) watcher.DataChanged += OnWatcherDataChanged;
        }
        catch { }
    }

    /// <summary>
    /// 后台监听（USN / FileSystemWatcher）发现文件路径变化后的 UI 刷新回调。
    /// 刷新统一列表与文件树，并尽量保留当前选中项，避免打断正在查看的备注。
    /// </summary>
    private void OnWatcherDataChanged()
    {
        try
        {
            var selFileId = _vm.SelectedRecord?.FileRef?.Id;
            var selNoteId = _vm.SelectedRecord?.Note?.Id;

            _vm.Refresh();

            if (selFileId != null)
                _vm.SelectedRecord = _vm.Records.FirstOrDefault(r => r.IsFile && r.FileRef?.Id == selFileId);
            else if (selNoteId != null)
                _vm.SelectedRecord = _vm.Records.FirstOrDefault(r => !r.IsFile && r.Note?.Id == selNoteId);

            RefreshRecordDetail();
        }
        catch (Exception ex) { LogCrash("WatcherDataChanged", ex); }
    }

    /// <summary>
    /// 初始化主题画廊（XAML 静态声明，见 MainWindow.xaml 中 ThemeGallery）。
    ///
    /// ★ 为什么改成 XAML 静态声明 + ListBox，而不是代码动态构建：
    ///   1. XAML 由 MarkupCompilePass 在**编译期**验证全部资源引用，漏键直接编译失败，
    ///      不会像 FindResource 那样在运行时悄悄抛异常导致画廊消失；
    ///   2. 选中态交给 ListBox 的 IsSelected + DataTrigger，无需手动重建卡片；
    ///   3. 代码从 150 行降到 20 行，结构上没有可出错的 UI 构建逻辑。
    /// </summary>
    private void InitializeThemeGallery()
    {
        var s = App.Instance.Settings;
        var gallery = ThemeGallery;
        if (gallery is null) return;

        gallery.ItemsSource = Themes.ThemeCatalog.All;

        // 先设选中项再订阅事件：避免初始化赋值误触发一次写盘
        gallery.SelectedItem = ThemeManager.Current;
        gallery.SelectionChanged += (_, _) =>
        {
            if (gallery.SelectedItem is not Themes.ThemeDefinition picked) return;
            if (picked.Id == ThemeManager.CurrentId) return;   // 已是当前主题
            try
            {
                ThemeManager.SetTheme(picked.Id);
                s.Theme = picked.Id;
                s.Save();
            }
            catch (Exception ex)
            {
                LogCrash("ThemeGallery.Click:" + picked.Id, ex);
            }
        };
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化阶段（InitializeComponent）可能提前触发本事件，此时 VM 尚未就绪，直接忽略
        if (_vm is null) return;
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string tag &&
            Enum.TryParse<NavSection>(tag, out var section))
        {
            _vm.Section = section;
            SwitchPanels(section);
        }
    }

    /// <summary>
    /// 外部唤回入口（托盘 / 悬浮球 / 快捷键）：把左侧导航切换到指定分区。
    /// 选中对应 NavList 项即触发 SelectionChanged → SwitchPanels，完成页面切换。
    /// </summary>
    public void NavigateTo(NavSection section)
    {
        try
        {
            foreach (var obj in NavList.Items)
            {
                if (obj is ListBoxItem item && item.Tag is string tag &&
                    Enum.TryParse<NavSection>(tag, out var s) && s == section)
                {
                    NavList.SelectedItem = item;
                    break;
                }
            }
            // 若目标项已是当前选中项，SelectionChanged 不会触发，手动同步面板。
            if (_section != section)
            {
                if (_vm != null) _vm.Section = section;
                SwitchPanels(section);
            }
        }
        catch { }
    }

    /// <summary>统一记录列表选中变化：驱动右侧详情按记录类型自适应显示。</summary>
    private void RecordList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        // 仅在「便签」分区下刷新便签/备注详情，避免覆盖剪贴板、待办等分区的详情面板
        if (_section != NavSection.Notes) return;
        RefreshRecordDetail();
    }

    /// <summary>剪贴板捕获回调：由 App 在捕获到新内容时调用，实时刷新剪贴板列表并保留选中。</summary>
    public void OnClipCaptured(Clip clip)
    {
        _vm.ReloadClipsPreservingSelection();
        _vm.StatusText = "新剪贴内容已收录";
    }

    /// <summary>根据导航分区切换中间列表与右侧详情面板。</summary>
    private void SwitchPanels(NavSection section)
    {
        // 防御：XAML 尚未完全构建完成时不操作控件
        if (MiddleNotes is null) return;
        _section = section;
        MiddleNotes.Visibility = section == NavSection.Notes ? Visibility.Visible : Visibility.Collapsed;
        MiddleClips.Visibility = section == NavSection.Clips ? Visibility.Visible : Visibility.Collapsed;
        MiddleTasks.Visibility = section == NavSection.Tasks ? Visibility.Visible : Visibility.Collapsed;
        MiddleTree.Visibility = section == NavSection.FileTree ? Visibility.Visible : Visibility.Collapsed;
        MiddleSearch.Visibility = section == NavSection.Search ? Visibility.Visible : Visibility.Collapsed;
        MiddleRecycle.Visibility = section == NavSection.Recycle ? Visibility.Visible : Visibility.Collapsed;
        MiddleSettings.Visibility = section == NavSection.Settings ? Visibility.Visible : Visibility.Collapsed;

        // 设置分区：右栏收起（宽度归零），中间列改为星号占满，使设置面板横跨「中 + 右」。
        // 其它分区恢复常规三栏（中间固定 380，右栏占满剩余）。
        // 注意：必须放在 Notes 早退分支之前，否则从设置切回便签时列宽不会复位。
        ApplyLayoutFor(section);

        if (section == NavSection.Notes)
        {
            RefreshRecordDetail();
            return;
        }

        DetailNote.Visibility = Visibility.Collapsed;
        DetailAnnotation.Visibility = Visibility.Collapsed;
        DetailClip.Visibility = section == NavSection.Clips ? Visibility.Visible : Visibility.Collapsed;
        DetailTask.Visibility = section == NavSection.Tasks ? Visibility.Visible : Visibility.Collapsed;
        if (section == NavSection.Tasks) SyncRemindDate(_vm.SelectedTask);
        DetailEmpty.Visibility = section is NavSection.FileTree or NavSection.Search
            or NavSection.Recycle or NavSection.Settings ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前分区调整主体列宽。
    /// 设置页表单较宽，右栏只显示占位提示太浪费，因此进入设置时把右侧详情列折叠。
    ///
    /// ★ 为什么用索引而不是 x:Name：
    ///   ColumnDefinition 继承自 FrameworkContentElement（不是 FrameworkElement），
    ///   XAML 编译器不会为它的 x:Name 生成代码字段，写 x:Name 拿不到引用。
    ///   因此统一通过 MainGrid.ColumnDefinitions[1]/[2] 索引访问。
    ///   列顺序：[0]=左导航 232，[1]=中列，[2]=右列。
    /// </summary>
    private void ApplyLayoutFor(NavSection section)
    {
        if (MainGrid is null) return;
        var cols = MainGrid.ColumnDefinitions;
        if (cols.Count < 3) return;

        bool wideMiddle = section == NavSection.Settings;
        cols[1].Width = wideMiddle ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
        cols[2].Width = wideMiddle ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>
    /// 统一详情刷新：便签记录显示便签编辑器，文件/文件夹备注记录显示备注编辑器。
    /// 这是修复「点选行后右侧不显示备注」的关键——之前仅切换导航时才刷新。
    /// </summary>
    private void RefreshRecordDetail()
    {
        // 该面板只服务于「便签」分区的记录列表；其他分区由 SwitchPanels 决定右侧详情，
        // 避免在剪贴板/待办等分区被本方法用「便签编辑」面板顶掉（实测 bug）。
        if (_section != NavSection.Notes)
        {
            SwitchPanels(_section);
            return;
        }

        var row = _vm.SelectedRecord;

        if (row is { IsFile: false })
        {
            ShowOnly(DetailNote);
            return;
        }

        if (row is { IsFile: true })
        {
            ShowAnnotationDetail(row.FileRef, row.Annotation!);
            return;
        }

        // 未选择任何记录：右侧保持便签编辑器占位
        ShowOnly(DetailNote);
    }

    /// <summary>仅显示指定详情面板，其余隐藏。</summary>
    private void ShowOnly(DependencyObject? visible)
    {
        void One(DependencyObject o) =>
            ((UIElement)o).Visibility = ReferenceEquals(o, visible) ? Visibility.Visible : Visibility.Collapsed;
        One(DetailNote);
        One(DetailAnnotation);
        One(DetailClip);
        One(DetailTask);
        One(DetailEmpty);
    }

    /// <summary>在右侧显示文件 / 文件夹备注详情（供统一列表与文件树共用）。</summary>
    private void ShowAnnotationDetail(FileRef? fr, Annotation ann)
    {
        ShowOnly(DetailAnnotation);

        AnnotationFileName.Text = fr?.Name ?? "(未知对象)";
        AnnotationPath.Text = fr?.Path ?? "";
        AnnotationContentBox.Text = ann.Content;
        AnnotationTagsBox.Text = ann.Tags;
        AnnotationIntentBox.Text = ann.Intent;

        AnnotationStateBox.ItemsSource = Models.StateDefaults.States;
        AnnotationStateBox.SelectedItem = ann.StateLabel;

        TimelineList.ItemsSource = App.Instance.Repo.GetTimeline(ann.FileRefId)
            .Select(t => $"{t.CreatedAt:MM-dd HH:mm}  {t.Action}  {t.Detail}")
            .ToList();
    }

    /// <summary>
    /// 文件树节点选中：在右侧显示该文件 / 文件夹的备注详情。
    /// 修复「文件树点选后右侧一直显示占位」的问题。
    /// </summary>
    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm is null) return;
        if (e.NewValue is not FileNode node || string.IsNullOrWhiteSpace(node.Path)) return;

        var fr = App.Instance.Repo.GetFileRefByPath(node.Path);
        if (fr is null)
        {
            // 树中的目录聚合根可能没有对应 FileRef，此时给出明确占位提示
            ShowOnly(DetailEmpty);
            return;
        }

        var ann = App.Instance.Repo.GetAnnotation(fr.Id) ?? new Annotation { FileRefId = fr.Id };
        ShowAnnotationDetail(fr, ann);
    }

    /// <summary>待办列表勾选：立即持久化（无需再点保存），并联动删除线样式。</summary>
    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TaskItem t })
        {
            App.Instance.Repo.UpsertTask(t);
            _vm.StatusText = t.Done ? $"已完成：{t.Title}" : $"未完成：{t.Title}";
        }
    }

    /// <summary>待办详情：快捷填写提醒时间（点一下预设即设好，也可在文本框手动输入）。</summary>
    private void RemindPreset_Click(object sender, RoutedEventArgs e)
    {
        var t = _vm.SelectedTask;
        if (t is null) return;
        var tag = (sender as Button)?.Tag as string ?? "";
        DateTime? dt = tag switch
        {
            "15" => DateTime.Now.AddMinutes(15),
            "60" => DateTime.Now.AddHours(1),
            "tonight" => RemindTonight(),
            "tomorrow" => DateTime.Today.AddDays(1).AddHours(9),
            _ => (DateTime?)null   // "clear"
        };
        t.RemindAt = dt;
        t.Reminded = false;
        if (RemindTextBox != null) RemindTextBox.Text = dt?.ToString("yyyy-MM-dd HH:mm") ?? "";
        SyncRemindDate(t);
        try { App.Instance.Repo.UpsertTask(t); } catch { }
        _vm.StatusText = dt is DateTime v ? "提醒时间：" + v.ToString("yyyy-MM-dd HH:mm") : "已清除提醒";
    }

    /// <summary>今晚 20:00；若已过则顺延到明天。</summary>
    private static DateTime RemindTonight()
    {
        var t = DateTime.Today.AddHours(20);
        return t <= DateTime.Now ? t.AddDays(1) : t;
    }

    /// <summary>待办详情：把当前提醒日期同步到日期选择器（不触发写回）。</summary>
    private void SyncRemindDate(TaskItem? t)
    {
        if (RemindCalendar is null) return;
        _syncingRemindDate = true;
        try { RemindCalendar.SelectedDate = t?.RemindAt?.Date; }
        finally { _syncingRemindDate = false; }
    }

    /// <summary>待办详情：点击提醒时间输入框时弹出日期选择器，便于快速选择日期。</summary>
    private void RemindTextBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RemindPopup is null) return;
        if (RemindPopup.IsOpen) { RemindPopup.IsOpen = false; return; }
        SyncRemindDate(_vm.SelectedTask);
        // 延后到本次点击完全结束后再弹出，避免弹出层被同一次鼠标单击判定为“外部点击”而立刻关闭
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (RemindPopup is not null) RemindPopup.IsOpen = true;
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>待办详情：在日期选择器中选定日期后，保留原时间（无则默认 09:00）并写回提醒时间。</summary>
    private void RemindCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRemindDate) return;
        var t = _vm.SelectedTask;
        if (t is null) return;
        if (sender is not Calendar cal || cal.SelectedDate is not DateTime d) return;
        var time = t.RemindAt?.TimeOfDay ?? new TimeSpan(9, 0, 0);
        var dt = d.Date + time;
        t.RemindAt = dt;
        t.Reminded = false;
        if (RemindTextBox != null) RemindTextBox.Text = dt.ToString("yyyy-MM-dd HH:mm");
        try { App.Instance.Repo.UpsertTask(t); } catch { }
        _vm.StatusText = "提醒时间：" + dt.ToString("yyyy-MM-dd HH:mm");
        if (RemindPopup != null) RemindPopup.IsOpen = false;   // 选完即收起，避免遮挡
    }

    /// <summary>待办详情：插入本地图片，写入描述 Markdown 并即时预览。</summary>
    private void InsertTaskImage_Click(object sender, RoutedEventArgs e)
    {
        var t = _vm.SelectedTask;
        if (t is null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要插入待办的图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var md = $"![{System.IO.Path.GetFileName(dlg.FileName)}]({dlg.FileName})";
        t.Description = string.IsNullOrWhiteSpace(t.Description) ? md : t.Description.TrimEnd() + "\n" + md;
        App.Instance.Repo.UpsertTask(t);
        _vm.StatusText = "已插入图片：" + System.IO.Path.GetFileName(dlg.FileName);
    }

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.Annotation == null) return;
        var ann = row.Annotation;
        ann.Content = AnnotationContentBox.Text;
        ann.Tags = AnnotationTagsBox.Text;
        ann.Intent = AnnotationIntentBox.Text;
        if (AnnotationStateBox.SelectedItem is string st)
        {
            var idx = Array.IndexOf(Models.StateDefaults.States, st);
            if (idx >= 0) ann.State = (AnnotationState)idx;
        }
        App.Instance.Repo.UpsertAnnotation(ann);
        App.Instance.Repo.AddTimeline("file", ann.FileRefId, "edited", "备注已保存");
        _vm.ReloadRecords();
        RefreshRecordDetail();
        _vm.StatusText = "备注已保存";
    }

    /// <summary>
    /// 删除当前文件 / 文件夹备注。二次确认后删除 annotation 及其伴随数据
    /// （时间线 / sidecar / 链接），并清空选中、刷新列表与详情。
    /// 注意：不会删除磁盘上的文件本身。
    /// </summary>
    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.FileRef == null) return;

        var name = row.FileRef.Name;
        var res = MessageBox.Show(
            $"确定删除对「{name}」的文件备注吗？\n（不会删除文件本身，此操作不可撤销）",
            "删除备注", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        App.Instance.Repo.DeleteAnnotation(row.FileRef.Id);
        _vm.SelectedRecord = null;
        _vm.ReloadRecords();
        RefreshRecordDetail();
        _vm.StatusText = "文件备注已删除：" + name;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.FileRef == null) return;
        var path = EnsureFileRefCurrent(row.FileRef);
        if (path == null)
        {
            MessageBox.Show("文件已被移动或删除，无法打开：\n" + row.FileRef.Path,
                "文件不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.FileRef == null) return;
        var path = EnsureFileRefCurrent(row.FileRef);
        if (path == null)
        {
            MessageBox.Show("文件已被移动或删除，无法定位：\n" + row.FileRef.Path,
                "文件不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        catch (Exception ex) { MessageBox.Show("定位失败：" + ex.Message); }
    }

    /// <summary>
    /// 取备注对应的「当前有效路径」。若记录路径已失效（例如文件在应用未运行时被移动/重命名，
    /// 或监听漏事件导致路径未同步），先借助指纹找回真实路径、刷新列表与详情，再返回；
    /// 确实找不到时返回 null，由调用方提示用户。
    /// </summary>
    private string? EnsureFileRefCurrent(FileRef fr)
    {
        try
        {
            if (System.IO.File.Exists(fr.Path) || System.IO.Directory.Exists(fr.Path)) return fr.Path;

            var located = App.Instance?.Watcher?.Reconcile(fr);
            if (!string.IsNullOrEmpty(located))
            {
                _vm.ReloadRecords();
                RefreshRecordDetail();
                _vm.StatusText = "检测到文件已移动，已自动更新路径：" + located;
                return located;
            }
        }
        catch { }
        return null;
    }

    /// <summary>当前设置二级分类（与 SettingsNav 的 Tag 对应）。</summary>
    private string _settingsCategory = "General";

    /// <summary>设置页二级菜单切换：外观主题走 XAML 静态面板，其余分类由代码构建。</summary>
    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav?.SelectedItem is ListBoxItem item && item.Tag is string tag && tag.Length > 0)
        {
            _settingsCategory = tag;
            ShowSettingsCategory(tag);
        }
    }

    /// <summary>按所选分类显示设置内容并（除「外观主题」外）重建对应分组。</summary>
    private void ShowSettingsCategory(string category)
    {
        bool isAppearance = category == "Appearance";
        if (AppearancePanel != null)
            AppearancePanel.Visibility = isAppearance ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsPanel != null)
            SettingsPanel.Visibility = isAppearance ? Visibility.Collapsed : Visibility.Visible;
        if (isAppearance) return;
        try { BuildSettingsPanel(category); }
        catch (Exception ex) { LogCrash("BuildSettingsPanel." + category, ex); }
    }

    /// <summary>按需求 4.7 设置页规范，按分类构建设置面板（含分组与设置行）。</summary>
    private void BuildSettingsPanel(string category)
    {
        var s = App.Instance.Settings;
        SettingsPanel.Children.Clear();

        bool Cat(params string[] names) => System.Array.IndexOf(names, category) >= 0;

        void Section(string title)
        {
            // 分组标题：左侧 3px 主色竖条 + 标题文字（浅深主题下都清晰）
            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 22, 0, 8),
            };
            try
            {
                head.Children.Add(new Border
                {
                    Width = 3,
                    Height = 14,
                    CornerRadius = new CornerRadius(1.5),
                    Background = (Brush)FindResource("PrimaryBrush"),
                    Margin = new Thickness(0, 1, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            catch { /* 装饰条拿不到资源就跳过，不影响文字 */ }
            head.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)FindResource("TextTitle"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            SettingsPanel.Children.Add(head);
        }

        void Row(string title, string desc, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("TextTitle") });
            sp.Children.Add(new TextBlock { Text = desc, Style = (Style)FindResource("TextCaption"), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(sp, 0);
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(sp);
            grid.Children.Add(control);
            SettingsPanel.Children.Add(grid);
        }

        // 分组级容错：任一分组构建失败只跳过该组，不影响后续分组与整页。
        void Guard(string group, Action build)
        {
            try { build(); }
            catch (Exception ex)
            {
                LogCrash("SettingsPanel." + group, ex);
            }
        }

        CheckBox Check(string title, string desc, bool value, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => { set(true); s.Save(); };
            cb.Unchecked += (_, _) => { set(false); s.Save(); };
            Row(title, desc, cb);
            return cb;
        }

        TextBox Text(string title, string desc, string value, Action<string> set)
        {
            var tb = new TextBox { Text = value, Width = 240, Style = (Style)FindResource("AppTextBox") };
            tb.LostFocus += (_, _) => { set(tb.Text); s.Save(); };
            Row(title, desc, tb);
            return tb;
        }

        if (Cat("General")) Guard("通用", () =>
        {
            Section("通用");
            // 开机启动：勾选即时写入 HKCU Run 注册项（免管理员），取消即删除；
            // 若注册表写入失败则回滚设置与勾选状态，避免“界面已开启、系统实际未生效”的不一致。
            bool startupGuard = false;
            CheckBox? cbStartup = null;
            cbStartup = Check("开机启动", "登录 Windows 后自动运行并常驻托盘（写入 HKCU Run，免管理员）", s.StartWithWindows, v =>
            {
                if (startupGuard) return;
                s.StartWithWindows = v;
                bool ok = App.Instance.Startup?.Apply(v) ?? true;
                if (!ok)
                {
                    s.StartWithWindows = !v;
                    startupGuard = true;
                    try { if (cbStartup != null) cbStartup.IsChecked = !v; } finally { startupGuard = false; }
                    MessageBox.Show(this, "无法写入开机自启项，请检查系统策略或注册表权限。", "文笺 FileMemo");
                }
            });
            Check("关闭时最小化到托盘", "关闭主窗口后监听继续运行", s.MinimizeToTray, v => s.MinimizeToTray = v);
        });

        if (Cat("General")) Guard("待办提醒", () =>
        {
            Section("待办提醒");
            Check("启用待办提醒", "待办到达提醒时间时，弹出托盘气泡与提醒窗口（可稍后提醒 / 标记完成）", s.ReminderEnabled, v =>
            {
                s.ReminderEnabled = v;
                App.Instance.Reminders?.Restart();
            });
            Text("检查间隔（秒）", "10 ~ 600，越小越准时、越耗资源（默认 30）", s.ReminderCheckSeconds.ToString(), v =>
            {
                if (int.TryParse(v, out var n)) s.ReminderCheckSeconds = Math.Clamp(n, 10, 600);
                App.Instance.Reminders?.Restart();
            });
            Text("提前提醒（分钟）", "0 = 到点提醒；大于 0 表示提前多少分钟提醒", s.ReminderAdvanceMinutes.ToString(), v =>
            {
                if (int.TryParse(v, out var n)) s.ReminderAdvanceMinutes = Math.Clamp(n, 0, 1440);
            });
            Check("允许稍后提醒", "提醒窗口提供「稍后提醒 10 分钟」按钮", s.ReminderSnoozeEnabled, v => s.ReminderSnoozeEnabled = v);
        });

        // ---- 外观主题：已改为 XAML 静态声明的 ThemeGallery（见 MainWindow.xaml），
        //      由 InitializeThemeGallery() 在 OnLoaded 中初始化，不再动态构建 ----

        if (Cat("Clipboard")) Guard("剪贴板", () =>
        {
            Section("剪贴板");
            Check("启用剪贴板随记", "监听系统剪贴板，记录文本 / 图片 / 文件路径 / HTML", s.ClipboardEnabled, v => s.ClipboardEnabled = v);
            Check("敏感内容加密", "剪贴板、文件路径、文件备注强制加密（DPAPI）", s.ClipboardEncrypt, v => s.ClipboardEncrypt = v);
            Text("保留条数", "范围 100 ~ 10000，固定项不受裁剪", s.ClipboardRetention.ToString(), v =>
            {
                if (int.TryParse(v, out var n)) s.ClipboardRetention = Math.Clamp(n, 100, 10000);
            });
            Text("排除应用", "逗号分隔的进程名，命中的复制内容不记录", string.Join(",", s.ClipboardExcludedApps ?? new()), v =>
            {
                s.ClipboardExcludedApps = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            });
        });

        if (Cat("Files")) Guard("文件追踪", () =>
        {
            Section("文件追踪");
            Check("全盘追踪", "默认仅追踪已添加备注的文件/文件夹", s.FullDiskTracking, v => s.FullDiskTracking = v);
            Check("监听 USN Journal", "后台实时捕获重命名 / 移动", s.WatchUsnJournal, v => s.WatchUsnJournal = v);
            Text("追踪文件数量上限", "超过后停止新增索引，保护性能", s.MaxTrackedFiles.ToString(), v =>
            {
                if (int.TryParse(v, out var n)) s.MaxTrackedFiles = n;
            });
        });

        if (Cat("Search")) Guard("搜索与 Everything", () =>
        {
            Section("搜索与 Everything");
            Check("启用 Everything", "本地固定盘通过 es.exe 调用，未安装时自动降级", s.UseEverything, v => s.UseEverything = v);
            Text("es.exe 路径", "留空则自动探测常见安装位置", s.EverythingPath, v => s.EverythingPath = v);
        });

        if (Cat("Hotkeys")) Guard("快捷键", () =>
        {
            Section("快捷键");
            Text("快速便签", "例如 Ctrl+Alt+N", s.HotkeyQuickNote, v => s.HotkeyQuickNote = v);
            Text("剪贴板面板", "例如 Ctrl+Alt+V", s.HotkeyClipboard, v => s.HotkeyClipboard = v);
        });

        if (Cat("Sync")) Guard("同步与备份", () =>
        {
            Section("同步与备份");
            Check("云同步", "仅同步本机 SQLite 主库（MVP 预留）", s.CloudSyncEnabled, v => s.CloudSyncEnabled = v);
            Check("Sidecar", "检测到移动盘 / NAS 时生成伴随文件", s.SidecarEnabled, v => s.SidecarEnabled = v);
        });

        if (Cat("Files")) Guard("P1 · 追踪与迁移", () =>
        {
            Section("P1 · 追踪与迁移");
            Check("哈希兜底", "跨卷移动时用完整哈希 + 大小 + 时间 + 名称相似度找回备注", s.HashFallbackEnabled, v => s.HashFallbackEnabled = v);
            Check("跨卷迁移交互确认", "多个候选时显示匹配度并交由用户合并 / 迁移", s.CrossVolumeConfirmEnabled, v => s.CrossVolumeConfirmEnabled = v);
            Check("网络盘 / NAS 索引", "网络盘使用内置轻量索引（卷序列号 + 路径 + 哈希）", s.NetworkPathIndexEnabled, v => s.NetworkPathIndexEnabled = v);
        });

        if (Cat("Files")) Guard("P1 · sidecar 伴生文件", () =>
        {
            Section("P1 · sidecar 伴生文件");
            Check("启用 sidecar", "移动盘 / NAS 场景把备注写入伴随文件作为权威副本", s.SidecarEnabled, v => s.SidecarEnabled = v);
            Check("sidecar 隐藏", "同目录下生成的 sidecar 设为隐藏属性", s.SidecarHidden, v => s.SidecarHidden = v);
            Text("sidecar 命名后缀", "同目录命名规则，例如 .supernote", s.SidecarSuffix, v => s.SidecarSuffix = v);
            Text("集中目录", "选择「集中目录」策略时的存放路径，留空用 %APPDATA%\\SuperNote\\sidecars", s.SidecarCentralDir, v => s.SidecarCentralDir = v);
        });

        if (Cat("Search")) Guard("P1 · OCR 搜索", () =>
        {
            Section("P1 · OCR 搜索");
            Check("启用图片 OCR", "把剪贴板图片 / 备注插图的文字提取为可搜索文本（需系统 OCR 语言包）", s.OcrEnabled, v => s.OcrEnabled = v);
            Text("OCR 语言", "Windows.Media.Ocr 语言标签，例如 zh-Hans-CN", s.OcrLanguage, v => s.OcrLanguage = v);
        });

        if (Cat("Sync")) Guard("P1 · 云同步（预留）", () =>
        {
            Section("P1 · 云同步（预留）");
            Check("启用云同步", "仅同步本机 SQLite 主库，冲突保留 .conflict 副本", s.CloudSyncEnabled, v => s.CloudSyncEnabled = v);
            Text("同步地址", "WebDAV / OneDrive 远端地址", s.CloudSyncEndpoint, v => s.CloudSyncEndpoint = v);
            Text("账号", "WebDAV 用户名 / OneDrive 标识", s.CloudSyncUser, v => s.CloudSyncUser = v);
            Check("传输端到端加密", "上传前对主库做 DPAPI 加密", s.CloudSyncEncrypt, v => s.CloudSyncEncrypt = v);
        });

        if (Cat("Files")) Guard("P1 · 副本继承策略", () =>
        {
            Section("P1 · 副本继承策略");
            Text("复制文件时", "Ask / Always / Never / SameDirOnly / SidecarOnly", s.InheritDefault.ToString(), v =>
        {
            if (Enum.TryParse<Models.InheritPolicy>(v.Trim(), ignoreCase: true, out var p)) s.InheritDefault = p;
        });
        });

        if (Cat("Files")) Guard("P1 · 时间线", () =>
        {
            Section("P1 · 时间线");
            Text("启用事件分类", "逗号分隔；默认仅备注编辑 + 文件关键变化", string.Join(",", s.TimelineEnabledCategories), v =>
        {
            s.TimelineEnabledCategories = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        });
        Text("时间线最大条数", "超出后滚动淘汰最旧记录", s.TimelineMaxEntries.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) s.TimelineMaxEntries = Math.Max(50, n);
        });
        });

        if (Cat("Advanced")) Guard("P2 · 关系图谱 / 知识网络", () =>
        {
            Section("P2 · 关系图谱 / 知识网络");
            Check("启用知识网络", "把便签、文件备注、待办及其关联可视化", s.GraphEnabled, v => s.GraphEnabled = v);
            Text("视图节点上限", "超过后仅显示前 N 个节点，保护性能", s.GraphMaxNodes.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) s.GraphMaxNodes = Math.Max(50, n);
        });
        {
            var btn = new Button { Content = "打开知识网络", Style = (Style)FindResource("SecondaryButton") };
            btn.Click += (_, _) => { try { new GraphWindow().Show(); } catch { } };
            Row("打开图谱", "以节点-连线方式查看记录 / 文件 / 待办的关联关系", btn);
        }
        });

        if (Cat("Advanced")) Guard("P2 · 图片向量搜索", () =>
        {
            Section("P2 · 图片向量搜索");
            Check("启用图片向量", "本地特征向量（148 维）相似图检索，不引入 AI / 云服务", s.ImageVectorEnabled, v => s.ImageVectorEnabled = v);
            Text("相似度阈值", "0 ~ 1，越高越严格", s.VectorMatchThreshold.ToString("0.00"), v =>
        {
            if (double.TryParse(v, out var d)) s.VectorMatchThreshold = Math.Clamp(d, 0, 1);
        });
        {
            var btn = new Button { Content = "重建向量索引", Style = (Style)FindResource("SecondaryButton") };
            btn.Click += (_, _) =>
            {
                try
                {
                    var n = App.Instance.ImageVectors?.ReindexAll() ?? 0;
                    MessageBox.Show($"已索引 {n} 张图片。", "图片向量");
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "图片向量"); }
            };
            Row("重建索引", "扫描全部图片备注与剪贴板图片并计算特征向量", btn);
        }
        });

        if (Cat("Advanced")) Guard("P2 · 团队协作（预留）", () =>
        {
            Section("P2 · 团队协作（预留）");
            Check("启用协作", "开启后可用工作区 / 成员元数据模型（默认本地，不连远端）", s.CollaborationEnabled, v => s.CollaborationEnabled = v);
            Text("协作提供方", "local 或自定义 ICollaborationProvider 名称", s.CollaborationProvider, v => s.CollaborationProvider = v);
        });

        if (Cat("Advanced")) Guard("P2 · 插件系统", () =>
        {
            Section("P2 · 插件系统");
            Check("启用插件", "扫描插件目录并加载 IPlugin 实现（独立加载上下文）", s.PluginsEnabled, v => s.PluginsEnabled = v);
            Text("插件目录", "留空用 %APPDATA%\\SuperNote\\plugins", s.PluginsDir, v => s.PluginsDir = v);
        });

        if (Cat("Advanced")) Guard("P2 · 后台服务", () =>
        {
            Section("P2 · 后台服务");
            Check("安装为后台服务", "大规模追踪 / 网络盘监控 / 开机索引（需管理员运行 scripts\\install-service.ps1）", s.InstallBackgroundService, v => s.InstallBackgroundService = v);
        });

        if (Cat("FloatingBall")) Guard("桌面悬浮图标", () =>
        {
            Section("桌面悬浮图标");
            var bc = App.Instance.BallConfig;
            Check("启用悬浮图标", "桌面常驻悬浮图标：吸附屏幕边缘时近乎透明；悬停呼出变为半透明并展开动作面板；单击打开/关闭主页，右键展开/收起面板，拖拽移动", bc.Current.Enabled, v =>
            {
                bc.Current.Enabled = v;
                if (v) App.Instance.BuildFloatingBall(); else App.Instance.CloseFloatingBall();
            });
            Text("图标直径", "像素，38 ~ 56（默认 46）", bc.Current.MainBall.Size.ToString("0"), v =>
            {
                if (double.TryParse(v, out var n)) { bc.Current.MainBall.Size = Math.Clamp(n, 38, 56); App.Instance.RebuildFloatingBall(); }
            });
            Check("吸附屏幕边缘", "拖拽松手后自动吸附到屏幕左右边缘，只露出一小段近乎透明的凸起；关闭后可自由悬浮", bc.Current.MainBall.SnapToEdge, v =>
            {
                bc.Current.MainBall.SnapToEdge = v; App.Instance.RebuildFloatingBall();
            });
            Text("吸附时透明度", "0.02 ~ 1.0，越小越透明（近乎透明推荐 0.10）", bc.Current.MainBall.DockedOpacity.ToString("0.00"), v =>
            {
                if (double.TryParse(v, out var n)) { bc.Current.MainBall.DockedOpacity = Math.Clamp(n, 0.02, 1.0); App.Instance.RebuildFloatingBall(); }
            });
            Text("呼出时透明度", "0.05 ~ 1.0（半透明推荐 0.72）", bc.Current.MainBall.RevealedOpacity.ToString("0.00"), v =>
            {
                if (double.TryParse(v, out var n)) { bc.Current.MainBall.RevealedOpacity = Math.Clamp(n, 0.05, 1.0); App.Instance.RebuildFloatingBall(); }
            });
            Check("显示展开面板", "悬停悬浮图标时从贴边一侧带动画展开：快速便签 / 打开剪切板 / 快速待办 / 快速文件树备注（纯图标，一行从右到左，单击直达）", bc.Current.MainBall.ShowSatellite, v =>
            {
                bc.Current.MainBall.ShowSatellite = v; App.Instance.RebuildFloatingBall();
            });
            Check("呼吸动画", "常态下图标缓慢呼吸（缩放 1.0 ↔ 1.05）", bc.Current.MainBall.Breathing, v =>
            {
                bc.Current.MainBall.Breathing = v; App.Instance.RebuildFloatingBall();
            });
            {
                var btn = new Button { Content = "复位到右上角", Style = (Style)FindResource("SecondaryButton") };
                btn.Click += (_, _) => { bc.Current.Position = ""; bc.Save(); App.Instance.RebuildFloatingBall(); };
                Row("悬浮图标位置", "拖动后自动记忆；点此复位到屏幕右上角", btn);
            }
            {
                var btn = new Button { Content = "打开配置文件", Style = (Style)FindResource("SecondaryButton") };
                btn.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bc.FilePath) { UseShellExecute = true }); }
                    catch { }
                };
                Row("外观与高级参数", $"外观 / 动画 / 控制点等细化参数已收进配置文件：{bc.FilePath}", btn);
            }
        });

        if (Cat("Privacy")) Guard("隐私与安全", () =>
        {
            Section("隐私与安全");
            Check("应用锁", "启动时要求 Windows Hello / 密码（MVP 预留）", s.AppLockEnabled, v => s.AppLockEnabled = v);
        });

        if (Cat("About")) Guard("关于", () =>
        {
            Section("关于");
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "文笺 FileMemo —— 签随文件走的超级便签",
                Style = (Style)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap
            });
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "每份文件都值得一纸文笺：图文备注 · 五重指纹追踪 · 文件树 · Everything 联合搜索",
                Style = (Style)FindResource("TextCaption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "v1.0 · 本地优先 · 纯 Windows · 无 AI",
                Style = (Style)FindResource("TextCaption")
            });
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "数据目录：" + System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperNote"),
                Style = (Style)FindResource("TextCaption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        });
    }
}
