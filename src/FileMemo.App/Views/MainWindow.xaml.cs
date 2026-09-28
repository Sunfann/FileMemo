using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Instance.Repo);
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildSettingsPanel();
        // 全局快捷键已在 App.OnStartup 集中注册（含备注弹窗），此处不再重复注册，避免冲突
        NavList.SelectedIndex = 0;
        SwitchPanels(NavSection.Notes);
        RefreshRecordDetail();
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

    /// <summary>统一记录列表选中变化：驱动右侧详情按记录类型自适应显示。</summary>
    private void RecordList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        // 仅在「便签」分区下刷新便签/备注详情，避免覆盖剪贴板、待办等分区的详情面板
        if (_section != NavSection.Notes) return;
        RefreshRecordDetail();
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

        if (section == NavSection.Notes)
        {
            RefreshRecordDetail();
            return;
        }

        DetailNote.Visibility = Visibility.Collapsed;
        DetailAnnotation.Visibility = Visibility.Collapsed;
        DetailClip.Visibility = section == NavSection.Clips ? Visibility.Visible : Visibility.Collapsed;
        DetailTask.Visibility = section == NavSection.Tasks ? Visibility.Visible : Visibility.Collapsed;
        DetailEmpty.Visibility = section is NavSection.FileTree or NavSection.Search
            or NavSection.Recycle or NavSection.Settings ? Visibility.Visible : Visibility.Collapsed;
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

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.FileRef == null) return;
        try { Process.Start(new ProcessStartInfo(row.FileRef.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRecord;
        if (row is not { IsFile: true } || row.FileRef == null) return;
        try { Process.Start("explorer.exe", "/select,\"" + row.FileRef.Path + "\""); }
        catch (Exception ex) { MessageBox.Show("定位失败：" + ex.Message); }
    }

    /// <summary>按需求 4.7 设置页规范动态构建设置面板（含分组与设置行）。</summary>
    private void BuildSettingsPanel()
    {
        var s = App.Instance.Settings;
        SettingsPanel.Children.Clear();

        void Section(string title)
        {
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)FindResource("TextTitle"),
                Margin = new Thickness(0, 16, 0, 8)
            });
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

        Section("通用");
        Check("开机启动", "登录 Windows 后自动运行并常驻托盘", s.StartWithWindows, v => s.StartWithWindows = v);
        Check("关闭时最小化到托盘", "关闭主窗口后监听继续运行", s.MinimizeToTray, v => s.MinimizeToTray = v);

        Section("剪贴板");
        Check("启用剪贴板随记", "监听系统剪贴板，记录文本 / 图片 / 文件路径 / HTML", s.ClipboardEnabled, v => s.ClipboardEnabled = v);
        Check("敏感内容加密", "剪贴板、文件路径、文件备注强制加密（DPAPI）", s.ClipboardEncrypt, v => s.ClipboardEncrypt = v);
        Text("保留条数", "范围 100 ~ 10000，固定项不受裁剪", s.ClipboardRetention.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) s.ClipboardRetention = Math.Clamp(n, 100, 10000);
        });
        Text("排除应用", "逗号分隔的进程名，命中的复制内容不记录", string.Join(",", s.ClipboardExcludedApps), v =>
        {
            s.ClipboardExcludedApps = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        });

        Section("文件追踪");
        Check("全盘追踪", "默认仅追踪已添加备注的文件/文件夹", s.FullDiskTracking, v => s.FullDiskTracking = v);
        Check("监听 USN Journal", "后台实时捕获重命名 / 移动", s.WatchUsnJournal, v => s.WatchUsnJournal = v);
        Text("追踪文件数量上限", "超过后停止新增索引，保护性能", s.MaxTrackedFiles.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) s.MaxTrackedFiles = n;
        });

        Section("搜索与 Everything");
        Check("启用 Everything", "本地固定盘通过 es.exe 调用，未安装时自动降级", s.UseEverything, v => s.UseEverything = v);
        Text("es.exe 路径", "留空则自动探测常见安装位置", s.EverythingPath, v => s.EverythingPath = v);

        Section("快捷键");
        Text("快速便签", "例如 Ctrl+Alt+N", s.HotkeyQuickNote, v => s.HotkeyQuickNote = v);
        Text("剪贴板面板", "例如 Ctrl+Alt+V", s.HotkeyClipboard, v => s.HotkeyClipboard = v);

        Section("同步与备份");
        Check("云同步", "仅同步本机 SQLite 主库（MVP 预留）", s.CloudSyncEnabled, v => s.CloudSyncEnabled = v);
        Check("Sidecar", "检测到移动盘 / NAS 时生成伴随文件", s.SidecarEnabled, v => s.SidecarEnabled = v);

        Section("P1 · 追踪与迁移");
        Check("哈希兜底", "跨卷移动时用完整哈希 + 大小 + 时间 + 名称相似度找回备注", s.HashFallbackEnabled, v => s.HashFallbackEnabled = v);
        Check("跨卷迁移交互确认", "多个候选时显示匹配度并交由用户合并 / 迁移", s.CrossVolumeConfirmEnabled, v => s.CrossVolumeConfirmEnabled = v);
        Check("网络盘 / NAS 索引", "网络盘使用内置轻量索引（卷序列号 + 路径 + 哈希）", s.NetworkPathIndexEnabled, v => s.NetworkPathIndexEnabled = v);

        Section("P1 · sidecar 伴生文件");
        Check("启用 sidecar", "移动盘 / NAS 场景把备注写入伴随文件作为权威副本", s.SidecarEnabled, v => s.SidecarEnabled = v);
        Check("sidecar 隐藏", "同目录下生成的 sidecar 设为隐藏属性", s.SidecarHidden, v => s.SidecarHidden = v);
        Text("sidecar 命名后缀", "同目录命名规则，例如 .supernote", s.SidecarSuffix, v => s.SidecarSuffix = v);
        Text("集中目录", "选择「集中目录」策略时的存放路径，留空用 %APPDATA%\\SuperNote\\sidecars", s.SidecarCentralDir, v => s.SidecarCentralDir = v);

        Section("P1 · OCR 搜索");
        Check("启用图片 OCR", "把剪贴板图片 / 备注插图的文字提取为可搜索文本（需系统 OCR 语言包）", s.OcrEnabled, v => s.OcrEnabled = v);
        Text("OCR 语言", "Windows.Media.Ocr 语言标签，例如 zh-Hans-CN", s.OcrLanguage, v => s.OcrLanguage = v);

        Section("P1 · 云同步（预留）");
        Check("启用云同步", "仅同步本机 SQLite 主库，冲突保留 .conflict 副本", s.CloudSyncEnabled, v => s.CloudSyncEnabled = v);
        Text("同步地址", "WebDAV / OneDrive 远端地址", s.CloudSyncEndpoint, v => s.CloudSyncEndpoint = v);
        Text("账号", "WebDAV 用户名 / OneDrive 标识", s.CloudSyncUser, v => s.CloudSyncUser = v);
        Check("传输端到端加密", "上传前对主库做 DPAPI 加密", s.CloudSyncEncrypt, v => s.CloudSyncEncrypt = v);

        Section("P1 · 副本继承策略");
        Text("复制文件时", "Ask / Always / Never / SameDirOnly / SidecarOnly", s.InheritDefault.ToString(), v =>
        {
            if (Enum.TryParse<Models.InheritPolicy>(v.Trim(), ignoreCase: true, out var p)) s.InheritDefault = p;
        });

        Section("P1 · 时间线");
        Text("启用事件分类", "逗号分隔；默认仅备注编辑 + 文件关键变化", string.Join(",", s.TimelineEnabledCategories), v =>
        {
            s.TimelineEnabledCategories = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        });
        Text("时间线最大条数", "超出后滚动淘汰最旧记录", s.TimelineMaxEntries.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) s.TimelineMaxEntries = Math.Max(50, n);
        });

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

        Section("P2 · 团队协作（预留）");
        Check("启用协作", "开启后可用工作区 / 成员元数据模型（默认本地，不连远端）", s.CollaborationEnabled, v => s.CollaborationEnabled = v);
        Text("协作提供方", "local 或自定义 ICollaborationProvider 名称", s.CollaborationProvider, v => s.CollaborationProvider = v);

        Section("P2 · 插件系统");
        Check("启用插件", "扫描插件目录并加载 IPlugin 实现（独立加载上下文）", s.PluginsEnabled, v => s.PluginsEnabled = v);
        Text("插件目录", "留空用 %APPDATA%\\SuperNote\\plugins", s.PluginsDir, v => s.PluginsDir = v);

        Section("P2 · 后台服务");
        Check("安装为后台服务", "大规模追踪 / 网络盘监控 / 开机索引（需管理员运行 scripts\\install-service.ps1）", s.InstallBackgroundService, v => s.InstallBackgroundService = v);

        Section("桌面悬浮球");
        var bc = App.Instance.BallConfig;
        Check("启用悬浮球", "桌面常驻悬浮球：单击唤出命令面板，双击剪贴板，右键径向菜单", bc.Current.Enabled, v =>
        {
            bc.Current.Enabled = v;
            if (v) App.Instance.BuildFloatingBall(); else App.Instance.CloseFloatingBall();
        });
        Text("主球直径", "像素，32 ~ 128", bc.Current.MainBall.Size.ToString("0"), v =>
        {
            if (double.TryParse(v, out var n)) { bc.Current.MainBall.Size = Math.Clamp(n, 32, 128); App.Instance.RebuildFloatingBall(); }
        });
        Text("主球底色", "十六进制，例如 #0A0A0A", bc.Current.MainBall.Color, v =>
        {
            bc.Current.MainBall.Color = v; App.Instance.RebuildFloatingBall();
        });
        Text("光晕颜色", "十六进制，例如 #0078D4", bc.Current.MainBall.GlowColor, v =>
        {
            bc.Current.MainBall.GlowColor = v; App.Instance.RebuildFloatingBall();
        });
        Check("环绕光环", "主球外围的旋转弧线", bc.Current.MainBall.RingEnabled, v =>
        {
            bc.Current.MainBall.RingEnabled = v; App.Instance.RebuildFloatingBall();
        });
        Text("不透明度", "0.3 ~ 1.0", bc.Current.MainBall.Opacity.ToString("0.00"), v =>
        {
            if (double.TryParse(v, out var d)) { bc.Current.MainBall.Opacity = Math.Clamp(d, 0.3, 1.0); App.Instance.RebuildFloatingBall(); }
        });
        Check("主球使用 3D 贴图", "关闭则使用深色玻璃渐变（更省资源）", bc.Current.MainBall.UseImage, v =>
        {
            bc.Current.MainBall.UseImage = v; App.Instance.RebuildFloatingBall();
        });
        Check("靠近边缘吸附", "拖拽松手后吸附到最近的屏幕边缘", bc.Current.MainBall.SnapToEdge, v =>
        {
            bc.Current.MainBall.SnapToEdge = v; bc.Save();
        });
        Check("显示小红点", "主球旁红点：长按收纳 / 释放全部便签", bc.Current.RedDot.Enabled, v =>
        {
            bc.Current.RedDot.Enabled = v; App.Instance.RebuildFloatingBall();
        });
        Text("红点长按毫秒", "250 ~ 3000，红点按下多久触发收纳 / 释放", bc.Current.RedDot.LongPressMs.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) bc.Current.RedDot.LongPressMs = Math.Clamp(n, 250, 3000);
        });
        Text("卫星球个数", "0 ~ 6，悬停主球时沿弧线展开", bc.Current.Satellites.Count.ToString(), v =>
        {
            if (int.TryParse(v, out var n))
            {
                n = Math.Clamp(n, 0, 6);
                var list = bc.Current.Satellites;
                while (list.Count > n) list.RemoveAt(list.Count - 1);
                while (list.Count < n) list.Add(new Models.SatelliteConfig { Color = "#8764B8", Action = Models.SatelliteAction.OpenMainWindow });
                App.Instance.RebuildFloatingBall();
            }
        });
        Text("粒子数量", "退出消散特效的粒子数（默认 320）", bc.Current.Particle.Count.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) bc.Current.Particle.Count = Math.Clamp(n, 80, 800);
        });
        {
            var btn = new Button { Content = "复位到右下角", Style = (Style)FindResource("SecondaryButton") };
            btn.Click += (_, _) => { bc.Current.Position = ""; bc.Save(); App.Instance.RebuildFloatingBall(); };
            Row("悬浮球位置", "拖动后自动记忆；点此复位到屏幕右下角", btn);
        }
        {
            var btn = new Button { Content = "打开配置文件", Style = (Style)FindResource("SecondaryButton") };
            btn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bc.FilePath) { UseShellExecute = true }); }
                catch { }
            };
            Row("球配置 JSON", $"位于 {bc.FilePath}", btn);
        }

        Section("隐私与安全");
        Check("应用锁", "启动时要求 Windows Hello / 密码（MVP 预留）", s.AppLockEnabled, v => s.AppLockEnabled = v);

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
            Text = "MVP v0.1.0 · 本地优先 · 纯 Windows · 无 AI",
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
    }
}
