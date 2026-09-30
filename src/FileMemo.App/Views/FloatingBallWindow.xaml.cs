using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileMemo.App.Effects;
using FileMemo.App.Interop;
using FileMemo.App.Models;
using FileMemo.App.Services;
using FileMemo.App.ViewModels;

namespace FileMemo.App.Views;

/// <summary>
/// 未来感桌面悬浮控制球（Floating Intelligence Orb）。
///
/// 结构：一颗主球（圆角磁贴）+ 一块展开动作面板。
///       · 主球唯一（产品身份）——「文件夹」，是文笺的化身；
///       · 面板动作项：快速便签 / 打开剪贴板 / 快速待办 / 快速文件树备注，
///         纯图标样式（不显示汉字），一行「从右到左」排列，点击直接执行，不弹二级菜单；
///       · 红点（管理控制点）独立于面板——收纳 / 释放永远可用，不受面板配置影响。
///
/// 形态：平时只是屏幕边缘的一个小凸起——近乎透明（仅留一道淡影，不占视觉）；
///       你靠近它，它滑出来并淡入为半透明（面板一并展开）；你离开，它缩回去并恢复近乎透明。
///
/// 交互：单击主球 → 打开 / 关闭主页；单击面板图标 → 直达对应入口；
///       右键 → 展开 / 收起面板；拖拽 → 移动。
/// </summary>
public partial class FloatingBallWindow : Window
{
    // -------- 画布几何（窗口 240×240，中心 120）--------
    /// <summary>悬浮球窗口画布边长（DIP）。外部（App）计算球心时需要。</summary>
    public const double CanvasSize = 240;
    /// <summary>画布中心到球心的偏移（DIP）。</summary>
    public const double Center = 120;
    private const double MinBall = 38;
    private const double MaxBall = 56;
    /// <summary>贴边收拢时，主球在屏幕边缘露出的宽度（DIP）——形成「小凸起」。</summary>
    private const double Peek = 14;

    // -------- 不透明度（吸附时近乎透明 / 呼出时半透明，均可在设置页调整）--------
    /// <summary>贴边收拢态：近乎透明，只在屏幕边缘留一道可察觉的淡影（默认 0.10）。</summary>
    private double DockedOpacity => Math.Clamp(S.MainBall.DockedOpacity, 0.02, 1.0);
    /// <summary>呼出（滑出 / 悬停）态：半透明，既能看清又不遮挡底层内容（默认 0.72）。</summary>
    private double RevealedOpacity => Math.Clamp(S.MainBall.RevealedOpacity, 0.05, 1.0);

    /// <summary>贴边停靠方向。</summary>
    private enum DockSide { Left, Right }

    // -------- 时间参数 --------
    private static readonly TimeSpan IntroDuration = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>交互状态机（NORMAL → HOVER → PRESS → EXIT_ANIMATION → DESTROY）。</summary>
    private enum OrbState { Normal, Hover, Press, ExitAnimation, Destroy }

    private BallConfigService Config => App.Instance.BallConfig;
    private BallSettings S => Config.Current;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _hoverProbe = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _fullScreenTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    // -------- 视觉元素 --------
    private Grid? _orbBody;          // OrbBody：深灰圆角磁贴（渐变底板 + 中央应用图标）

    private Grid? _controlPointHost; // 管理控制点（含扩大命中区）
    private Ellipse? _controlPoint;  // 控制点可见圆
    private ScaleTransform? _controlPointScale;
    private Brush? _controlPointBrush;

    private readonly ScaleTransform _mainScale = new(1, 1);
    private readonly Stopwatch _redPress = new();

    private OrbState _state = OrbState.Normal;
    private bool _mainPressing, _redPressing, _longPressFired, _busy;

    // -------- 展开面板 / 贴边滑出 --------
    private Canvas? _orbitHost;                                    // 动作面板容器（整体显隐 / 命中）
    private readonly List<(FrameworkElement host, TranslateTransform off, double dx, double dy)> _satellites = new();
    private readonly TranslateTransform _sceneShift = new(0, 0);   // 贴边「小凸起 ↔ 滑出」整体位移
    // 面板悬停热区（画布坐标，由 BuildActionPanel 填充；_bandX1 <= _bandX0 表示无面板）
    private double _bandX0, _bandX1, _bandCy, _bandHalfH;
    private bool _snapped;                                         // 当前是否贴边停靠
    private bool _revealed = true;                                 // 当前是否已滑出（展开）
    private DockSide _side = DockSide.Right;                       // 停靠边

    // -------- 拖动 --------
    private bool _dragging;
    private Point _dragStartScreen;
    private Point _dragOffsetDevice;

    private double BallSize => Math.Clamp(S.MainBall.Size, MinBall, MaxBall);
    private double ControlSize => Math.Clamp(S.MainBall.ControlPointSize, 20, 40);

    /// <summary>主球球心在屏幕上的位置（DIP），供 App 计算收纳动画收拢点。</summary>
    public Point BallCenterDip => new(Left + Center + _sceneShift.X, Top + Center);

    public FloatingBallWindow()
    {
        InitializeComponent();

        _hideTimer.Interval = HideDelay;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); ConcealControlPoint(); Retract(); };

        // MouseEnter / MouseLeave 在 WS_EX_NOACTIVATE 下可能不可靠，用光标距离探测兜底：
        // 靠近球 → 滑出并淡入为半透明；离开 → 延迟收回成屏幕边缘近乎透明的「小凸起」。
        _hoverProbe.Tick += (_, _) =>
        {
            if (_busy) return;
            if (IsCursorNearOrb())
            {
                _hideTimer.Stop();
                if (_snapped) Reveal();
            }
            else if (_state is OrbState.Normal or OrbState.Hover)
            {
                ConcealControlPoint();
                if (!_hideTimer.IsEnabled) _hideTimer.Start();
            }
        };

        _fullScreenTimer.Tick += (_, _) => UpdateFullScreenVisibility();

        Loaded += (_, _) =>
        {
            // 先确定吸附状态与贴边方向（面板与「悬浮图标同一行」的布局、控制点方位都依赖它们），再构建视觉。
            _snapped = S.MainBall.SnapToEdge;
            _side = DetermineSide();
            BuildVisual();
            RestorePosition();
            if (S.MainBall.PlayIntroAnimation)
            {
                PlayIntroAnimation();   // 内部 Completed → StartBreathing
            }
            else
            {
                SetStableScale(1.0);
                if (S.MainBall.Breathing) StartBreathing();
            }
            _fullScreenTimer.Start();
            _hoverProbe.Start();
        };

        Closed += (_, _) =>
        {
            _fullScreenTimer.Stop();
            _hoverProbe.Stop();
            CompositionTarget.Rendering -= OnProgressRendering;   // 兜底退订，防止泄漏
        };

        MouseLeave += (_, _) => { _hideTimer.Start(); };
        MouseEnter += (_, _) => { _hideTimer.Stop(); HoverMain(true); Reveal(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNoActivateStyles();
    }

    private void ApplyNoActivateStyles()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            ex &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, (int)ex);
        }
        catch { }
    }

    // ==================================================================
    //  绘制
    // ==================================================================
    private void BuildVisual()
    {
        Stage.Children.Clear();

        // 「贴边小凸起 ↔ 滑出」：整体位移作用于整个场景（球 / 面板 / 控制点一起滑动）
        Stage.RenderTransformOrigin = new Point(0, 0);
        Stage.RenderTransform = _sceneShift;

        double d = BallSize;
        double r = d / 2;

        // 1) 展开动作面板（位于主球之下）
        BuildOrbit();

        // 2) OrbBody：圆角方形「悬浮磁贴」——深灰渐变圆角方块 + 中央应用图标。
        double cornerRadius = d * 0.30;   // 圆角半径（圆角方形）
        double iconSize = d * 0.66;       // 图标边长占磁贴比例

        _orbBody = new Grid
        {
            Width = d,
            Height = d,
            Cursor = Cursors.SizeAll,
            ToolTip = "文笺 FileMemo\n单击：打开/关闭主页  右键：展开/收起面板  拖拽：移动",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _mainScale,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.35,
                RenderingBias = RenderingBias.Performance
            }
        };

        var plate = new Rectangle
        {
            Width = d,
            Height = d,
            RadiusX = cornerRadius,
            RadiusY = cornerRadius,
            Fill = new LinearGradientBrush(
                Color.FromRgb(0x45, 0x45, 0x4D),
                Color.FromRgb(0x2A, 0x2A, 0x31),
                new Point(0.5, 0), new Point(0.5, 1)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x5F)),
            StrokeThickness = 1,
            SnapsToDevicePixels = true
        };
        _orbBody.Children.Add(plate);

        var iconBrush = LoadPackImage("ball_tile_icon.png");
        if (iconBrush != null)
        {
            var icon = new System.Windows.Controls.Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Source = iconBrush.ImageSource
            };
            _orbBody.Children.Add(icon);
        }
        Canvas.SetLeft(_orbBody, Center - r);
        Canvas.SetTop(_orbBody, Center - r);
        _orbBody.MouseEnter += (_, _) => { _hideTimer.Stop(); RevealControlPoint(); HoverMain(true); Reveal(); };
        _orbBody.MouseLeave += (_, _) =>
        {
            _hideTimer.Start();
            if (_state is OrbState.Hover) GoState(OrbState.Normal);
        };
        _orbBody.MouseLeftButtonDown += MainBall_MouseLeftButtonDown;
        _orbBody.MouseMove += MainBall_MouseMove;
        _orbBody.MouseLeftButtonUp += MainBall_MouseLeftButtonUp;
        _orbBody.MouseRightButtonUp += (_, e) => { e.Handled = true; ToggleRing(); };
        Stage.Children.Add(_orbBody);

        // 4) 管理控制点（磁贴右侧水平居中）
        if (S.RedDot.Enabled && S.MainBall.ShowControlPoint) BuildControlPoint();

        // 初始不透明度按「当前是否已呼出」决定；贴边收拢时近乎透明。
        Opacity = TargetOpacity();
    }

    private void BuildControlPoint()
    {
        double cs = ControlSize;
        // 停靠在左边缘时控制点移到磁贴左侧，避免被屏幕边缘裁掉。
        double cx = _side == DockSide.Left
            ? Center - BallSize / 2 - cs / 2 - 10
            : Center + BallSize / 2 + cs / 2 + 10;
        double cy = Center;

        _controlPointScale = new ScaleTransform(0.8, 0.8);
        _controlPointHost = new Grid
        {
            Width = cs + 16,
            Height = cs + 16,
            Opacity = 0,
            Cursor = Cursors.Hand,
            ToolTip = "单击：最小化全部便签 长按：一键收纳 / 释放",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _controlPointScale
        };

        // 扩大命中区：必须有非 null 的 Fill 才参与命中测试（Transparent 可行，x:Null 不行）
        var hitArea = new Ellipse
        {
            Width = cs + 16,
            Height = cs + 16,
            Fill = Brushes.Transparent,
            IsHitTestVisible = true
        };
        _controlPointHost.Children.Add(hitArea);

        _controlPointBrush = MakeControlBrush(ParseColor(S.MainBall.Color, Color.FromRgb(0x0A, 0x0A, 0x0C)));
        _controlPoint = new Ellipse
        {
            Width = cs,
            Height = cs,
            Fill = _controlPointBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x00, 0x00, 0x00), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.45 }
        };
        _controlPointHost.Children.Add(_controlPoint);

        _controlPointHost.MouseEnter += (_, _) => { _hideTimer.Stop(); RevealControlPoint(); };
        _controlPointHost.MouseLeftButtonDown += RedDot_MouseLeftButtonDown;
        _controlPointHost.MouseLeftButtonUp += RedDot_MouseLeftButtonUp;

        Canvas.SetLeft(_controlPointHost, cx - (cs + 16) / 2);
        Canvas.SetTop(_controlPointHost, cy - (cs + 16) / 2);
        Stage.Children.Add(_controlPointHost);

        UpdateControlPointColor(animate: false);
    }

    // ==================================================================
    //  展开面板（图标动作项，一行从右到左）
    // ==================================================================
    /// <summary>构建动作面板容器并落位。</summary>
    private void BuildOrbit()
    {
        _orbitHost = new Canvas { Width = CanvasSize, Height = CanvasSize, IsHitTestVisible = true };
        Stage.Children.Add(_orbitHost);
        RebuildOrbit();
        SetOrbitVisible(_revealed, animate: false);
    }

    private void RebuildOrbit()
    {
        if (_orbitHost == null) return;
        _orbitHost.Children.Clear();
        _satellites.Clear();

        // 重置面板悬停热区（画布坐标），由 BuildActionPanel 按需重新填充。
        _bandX0 = _bandX1 = _bandCy = _bandHalfH = 0;

        // 设置页「显示展开面板」开关关闭时，不构建任何动作项。
        // （右键悬浮图标切换的也是这个开关；旧版 RingEnabled 光环开关不再拦截面板。）
        if (!S.MainBall.ShowSatellite) return;

        BuildActionPanel();
    }

    /// <summary>
    /// 构建「动作面板」：以悬浮磁贴为把手，悬停时从贴边一侧展开一块圆角深灰面板，
    /// 水平「一行从右到左」排列若干纯图标动作项（打开便签 / 打开剪贴板 / 打开待办 /
    /// 打开文件文件夹备注）——图标即按钮，不显示任何汉字。
    /// 面板整体注册进 _satellites，复用既有的「唤醒滑出 / 收拢退回」显隐动画。
    /// </summary>
    private void BuildActionPanel()
    {
        var enabled = new List<SatelliteConfig>();
        foreach (var s in S.Satellites) if (s.Enabled) enabled.Add(s);
        if (enabled.Count == 0) return;

        bool right = _side == DockSide.Right;

        // 一行排列；停靠右边缘时 FlowDirection=RightToLeft → 第一个动作（便签）显示在最右侧、
        // 贴着磁贴，其余依次向左排，即「从右到左」。停靠左边缘时镜像为从左到右。
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            FlowDirection = right ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        foreach (var sat in enabled) row.Children.Add(MakeActionIcon(sat));

        var panel = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(0x45, 0x45, 0x4D), Color.FromRgb(0x2A, 0x2A, 0x31),
                new Point(0.5, 0), new Point(0.5, 1)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x5F)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5, 4, 5, 4),
            Child = row,
            MaxHeight = CanvasSize - 16,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3,
                Direction = 270, Opacity = 0.40, RenderingBias = RenderingBias.Performance
            },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var off = new TranslateTransform(0, 0);
        panel.RenderTransform = off;

        // 量测后按当前形态摆放。
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = panel.DesiredSize;

        double left, top, hideDx, hideDy;
        if (_snapped)
        {
            // 贴边：面板只在「滑出」时可见，因此按滑出后的屏幕布局反推画布坐标 ——
            // 球贴向屏幕边缘，面板贴着球内侧水平展开（一行、从右到左）；
            // 未滑出时面板随场景位移退到画布外（不可见、不响应命中）。
            double shift = right ? RevealedShift() : -RevealedShift();
            double screenLeft = right
                ? (Center + shift) - BallSize / 2 - 8 - sz.Width
                : (Center + shift) + BallSize / 2 + 8;
            left = screenLeft - shift;
            top = Center - sz.Height / 2;
            hideDx = right ? -26 : 26;   // 收回时向贴边一侧退回
            hideDy = 0;
        }
        else
        {
            // 自由漂浮：球居画布中央，面板改为球下方横排（仍是一行、从右到左）。
            left = Center - sz.Width / 2;
            top = Center + BallSize / 2 + 8;
            hideDx = 0;
            hideDy = 26;                 // 收回时向上缩回球底
        }
        // 只约束垂直方向；水平方向允许探出画布边缘（滑出时才随场景位移进入可视区）。
        top = Math.Max(4, Math.Min(CanvasSize - sz.Height - 4, top));
        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);

        _orbitHost.Children.Add(panel);
        _satellites.Add((panel, off, hideDx, hideDy));

        // 记录面板悬停热区（画布坐标）：光标移向面板图标时不误触发收回。
        _bandX0 = left - 8;
        _bandX1 = left + sz.Width + 8;
        _bandCy = top + sz.Height / 2;
        _bandHalfH = sz.Height / 2 + 10;
    }

    /// <summary>
    /// 单个动作项：纯图标圆角磁贴按钮（36×36，仅图标，无文字），悬停高亮 + 微放大，
    /// 单击直接执行对应动作（不弹二级菜单）。
    /// </summary>
    private FrameworkElement MakeActionIcon(SatelliteConfig sat)
    {
        var color = ParseColor(sat.Color, Color.FromRgb(0xB8, 0xC4, 0xD0));
        // 深色玻璃底色（#151A21 一类）在面板上没有辨识度，统一提亮为冷白灰作为图标色。
        if (color.R < 0x60 && color.G < 0x60 && color.B < 0x70)
            color = Color.FromRgb(0xC9, 0xD1, 0xDC);
        var label = string.IsNullOrWhiteSpace(sat.Label) ? sat.Action.ToString() : sat.Label;

        // 图标：优先当 Icons.xaml 矢量资源键解析；失败则按任意文字 / emoji 渲染。
        FrameworkElement icon;
        var vector = !string.IsNullOrWhiteSpace(sat.Icon) ? TryFindResource(sat.Icon) as Geometry : null;
        if (vector != null)
        {
            icon = new System.Windows.Shapes.Path
            {
                Data = vector,
                Fill = new SolidColorBrush(color),
                Width = 17, Height = 17,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            icon = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(sat.Icon) ? "●" : sat.Icon,
                FontSize = 15,
                Foreground = new SolidColorBrush(color),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var scale = new ScaleTransform(1, 1);
        var normal = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        var hover = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));

        var btn = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = normal,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            Child = icon,
            ToolTip = label,               // 图标无汉字，悬停提示保留文字说明
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };
        btn.MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            btn.Background = hover;
            var a = new DoubleAnimation(1.14, TimeSpan.FromMilliseconds(130))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = normal;
            var a = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        };
        btn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_busy) return;
            DispatchSatellite(sat.Action);   // 图标是动作：单击直接执行，不弹二级菜单。
        };
        return btn;
    }

    // ==================================================================
    //  贴边「小凸起 ↔ 滑出」 + 面板显隐 + 不透明度
    // ==================================================================
    /// <summary>收拢（贴边）时场景整体位移：让主球只在屏幕边缘露出一小段。</summary>
    private double CollapsedShift()
        => (_side == DockSide.Right ? 1 : -1) * (CanvasSize - Center - Peek + BallSize / 2);

    /// <summary>展开（滑出）时场景整体位移：把磁贴球推到紧贴屏幕边缘（仅留 3px 呼吸边距）。</summary>
    private double RevealedShift()
        => (_side == DockSide.Right ? 1 : -1) * (CanvasSize - Center - BallSize / 2 - EdgeMargin);

    /// <summary>贴边展开态：磁贴球与屏幕边缘之间保留的呼吸边距（像素）。</summary>
    private const double EdgeMargin = 3;

    private void AnimateShift(double to, int ms)
    {
        if (ms <= 0)
        {
            _sceneShift.BeginAnimation(TranslateTransform.XProperty, null);
            _sceneShift.X = to;
            return;
        }
        _sceneShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>目标不透明度：贴边收拢时近乎透明；呼出 / 悬停 / 自由漂浮时半透明。</summary>
    private double TargetOpacity()
    {
        if (_state is OrbState.ExitAnimation or OrbState.Destroy)
            return 1;
        return (_snapped && !_revealed) ? DockedOpacity : RevealedOpacity;
    }

    /// <summary>把整窗不透明度动画到当前状态的目标值（吸附近乎透明 ↔ 呼出半透明）。</summary>
    private void FadeToTargetOpacity(int ms = 200)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(TargetOpacity(), TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void SetOrbitVisible(bool on, bool animate)
    {
        if (_orbitHost == null) return;
        _orbitHost.IsHitTestVisible = on;
        _orbitHost.BeginAnimation(OpacityProperty, null);
        _orbitHost.Opacity = 1;

        if (!animate)
        {
            // 无动画（启动 / 布局切换）：直接落位。收起时把面板收回主球一侧并隐藏。
            foreach (var (host, off, dx, dy) in _satellites)
            {
                host.BeginAnimation(OpacityProperty, null);
                host.Opacity = on ? 1 : 0;
                off.BeginAnimation(TranslateTransform.XProperty, null);
                off.BeginAnimation(TranslateTransform.YProperty, null);
                off.X = on ? 0 : -dx;
                off.Y = on ? 0 : -dy;
            }
            return;
        }

        if (on) AnimateSatellitesIn();
        else AnimateSatellitesOut();
    }

    /// <summary>唤醒：面板从主球身后「滑出」并回弹（Spring-like），营造丝滑感。</summary>
    private void AnimateSatellitesIn()
    {
        int i = 0;
        foreach (var (host, off, dx, dy) in _satellites)
        {
            int delay = 45 * i++;
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            var dur = TimeSpan.FromMilliseconds(440);
            off.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = -dx, To = 0, Duration = dur,
                BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = spring
            });
            off.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = -dy, To = 0, Duration = dur,
                BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = spring
            });
            host.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(260),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>收起：面板「滑回主球身后」并淡出。</summary>
    private void AnimateSatellitesOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        foreach (var (host, off, dx, dy) in _satellites)
        {
            var dur = TimeSpan.FromMilliseconds(220);
            off.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = -dx, Duration = dur, EasingFunction = ease });
            off.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = -dy, Duration = dur, EasingFunction = ease });
            host.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = ease });
        }
    }

    /// <summary>靠近主球 → 滑出、淡入为半透明并展开面板。</summary>
    private void Reveal()
    {
        if (!_snapped || _revealed) { FadeToTargetOpacity(); return; }
        _revealed = true;
        SetOrbitVisible(true, animate: true);
        AnimateShift(RevealedShift(), 260);
        FadeToTargetOpacity(220);
    }

    /// <summary>离开 → 收回成屏幕边缘近乎透明的「小凸起」。</summary>
    private void Retract()
    {
        if (!_snapped || !_revealed) { FadeToTargetOpacity(); return; }
        _revealed = false;
        SetOrbitVisible(false, animate: true);
        AnimateShift(CollapsedShift(), 260);
        FadeToTargetOpacity(300);
    }

    /// <summary>光标是否靠近主球（动态热区：收拢时取球体附近，展开时覆盖磁贴 + 面板条带）。</summary>
    private bool IsCursorNearOrb()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var p = System.Windows.Forms.Cursor.Position;   // 设备像素
            double sx = p.X / dpi.DpiScaleX;
            double sy = p.Y / dpi.DpiScaleY;
            double cx = Left + Center + _sceneShift.X;
            double cy = Top + Center;
            double dx = sx - cx, dy = sy - cy;

            if (_revealed && S.MainBall.ShowSatellite)
            {
                // 展开态：圆形热区（主球周边）∪ 面板矩形热区（防止光标移向面板图标时误收回）。
                double hot = Math.Max(96, BallSize / 2 + 24);
                if (dx * dx + dy * dy <= hot * hot) return true;
                if (_bandX1 > _bandX0)
                {
                    double canvasX = sx - Left - _sceneShift.X;
                    double canvasY = sy - Top;
                    if (canvasX >= _bandX0 && canvasX <= _bandX1
                        && Math.Abs(canvasY - _bandCy) <= _bandHalfH)
                        return true;
                }
                return false;
            }

            double h = BallSize / 2 + 20;
            return dx * dx + dy * dy <= h * h;
        }
        catch { return true; }
    }

    // ==================================================================
    //  笔刷工厂
    // ==================================================================
    /// <summary>控制点的深色玻璃质感：左上亮 → 主色 → 边缘暗。</summary>
    private static Brush MakeControlBrush(Color core)
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.36, 0.28),
            Center = new Point(0.44, 0.40),
            RadiusX = 0.78,
            RadiusY = 0.78
        };
        b.GradientStops.Add(new GradientStop(Lighten(core, 0.45), 0.0));
        b.GradientStops.Add(new GradientStop(core, 0.45));
        b.GradientStops.Add(new GradientStop(Darken(core, 0.35), 1.0));
        return b;
    }

    // ==================================================================
    //  状态机（动画目标值唯一出口）
    // ==================================================================
    private void GoState(OrbState next)
    {
        if (_state == next) return;
        _state = next;

        switch (next)
        {
            case OrbState.Normal:
                StopBreathing();
                // 缩放先回落到 1.0，完成后再恢复呼吸，避免呼吸动画（1.0↔1.05）瞬间顶掉回落过渡。
                var settle = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                settle.Completed += (_, _) =>
                {
                    if (_state == OrbState.Normal && S.MainBall.Breathing) StartBreathing();
                };
                _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
                _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
                UnsubscribeRendering();
                break;

            case OrbState.Hover:
                StopBreathing();
                AnimateScale(1.08, 200, new CubicEase { EasingMode = EasingMode.EaseOut });
                break;

            case OrbState.Press:
                StopBreathing();
                AnimateScale(0.85, 80, new QuadraticEase { EasingMode = EasingMode.EaseOut });
                break;

            case OrbState.ExitAnimation:
                UnsubscribeRendering();
                if (_orbBody != null) _orbBody.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)));
                AnimateScale(0.2, 140, new CubicEase { EasingMode = EasingMode.EaseIn });
                break;

            case OrbState.Destroy:
                break;
        }

    }

    private void SubscribeRendering()
    {
        CompositionTarget.Rendering -= OnProgressRendering;
        CompositionTarget.Rendering += OnProgressRendering;
    }

    private void UnsubscribeRendering() => CompositionTarget.Rendering -= OnProgressRendering;

    // ==================================================================
    //  动画：缩放 / 点击反馈 / 出现
    // ==================================================================
    private void AnimateScale(double to, int ms, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>直接设定缩放（不播动画），用于关闭呼吸/出现动画的场景。</summary>
    private void SetStableScale(double v)
    {
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _mainScale.ScaleX = v;
        _mainScale.ScaleY = v;
    }

    private void StartBreathing()
    {
        if (!S.MainBall.Breathing) return;
        var breathe = new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(1250))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    private void StopBreathing()
    {
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void HoverMain(bool on)
    {
        if (_busy) return;
        if (_state is OrbState.Press) return;
        GoState(on ? OrbState.Hover : OrbState.Normal);
    }

    /// <summary>按下后的弹性回弹：0.85 → 1.15 → 1.0（超冲）；可指定播放完成后的回调。</summary>
    private void PlayPressSpring(Action? onCompleted = null)
    {
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(128)))
        {
            KeySpline = new KeySpline(0.34, 1.56, 0.64, 1)
        });
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)))
        {
            KeySpline = new KeySpline(0.34, 1.56, 0.64, 1)
        });
        if (onCompleted != null) kf.Completed += (_, _) => onCompleted();
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, kf);
    }

    /// <summary>点击反馈：不出光芒、不出波纹——只做一次快速「闪烁」（主球淡化后回弹，220ms）。</summary>
    private void PlayRipple()
    {
        if (_orbBody == null) return;
        var blink = new DoubleAnimationUsingKeyFrames();
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        _orbBody.BeginAnimation(OpacityProperty, blink);
    }

    /// <summary>首次出现动画：1.2s fade in + scale bounce（淡入到当前状态的目标不透明度）。</summary>
    private void PlayIntroAnimation()
    {
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)))
        {
            KeySpline = new KeySpline(0.4, 0, 0.6, 1)
        });
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)))
        {
            KeySpline = new KeySpline(0.34, 1.56, 0.64, 1)
        });
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(840)))
        {
            KeySpline = new KeySpline(0.4, 0, 0.6, 1)
        });
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(IntroDuration)));
        kf.Completed += (_, _) => { if (S.MainBall.Breathing) StartBreathing(); };
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, kf);

        // 整体淡入：吸附态淡到近乎透明，呼出态淡到半透明
        var fade = new DoubleAnimation(0, TargetOpacity(), TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // ==================================================================
    //  主球：拖动 / 单击（打开 / 关闭主页）
    // ==================================================================
    private void MainBall_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        e.Handled = true;

        _mainPressing = true;
        _longPressFired = false;
        _dragging = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        _dragOffsetDevice = new Point(
            _dragStartScreen.X - Left * dpi.DpiScaleX,
            _dragStartScreen.Y - Top * dpi.DpiScaleY);
        _orbBody?.CaptureMouse();
        GoState(OrbState.Press);
    }

    private void MainBall_MouseMove(object sender, MouseEventArgs e)
    {
        // 按下后拖拽移动（超过阈值判定为拖动）
        if (!_mainPressing || _busy) return;
        var cur = PointToScreen(e.GetPosition(this));
        double dx = cur.X - _dragStartScreen.X;
        double dy = cur.Y - _dragStartScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 5) _dragging = true;
        if (_dragging)
        {
            UnsubscribeRendering();
            MoveToDevice(cur);
        }
    }

    private void MainBall_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_orbBody?.IsMouseCaptured == true) _orbBody.ReleaseMouseCapture();
        UnsubscribeRendering();
        _mainPressing = false;
        if (_busy) return;
        e.Handled = true;

        if (_dragging)
        {
            FinishDrag();
            GoState(OrbState.Normal);
            return;
        }

        // 单击（未拖拽）→ 打开 / 关闭主页
        // 弹性回弹播完后再切回悬停/普通态，避免状态切换的缩放动画覆盖回弹。
        PlayRipple();
        bool backToHover = IsMouseOver;
        PlayPressSpring(() => GoState(backToHover ? OrbState.Hover : OrbState.Normal));
        try { App.Instance.ToggleMainWindow(); } catch { }
    }

    private void FinishDrag()
    {
        _snapped = S.MainBall.SnapToEdge;
        if (_snapped)
        {
            double screenMid = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth / 2;
            double cx = Left + Width / 2;
            bool right = cx >= screenMid;
            _side = right ? DockSide.Right : DockSide.Left;
            double target = right
                ? SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width
                : SystemParameters.VirtualScreenLeft;
            BeginAnimation(LeftProperty, new DoubleAnimation(Left, target, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Left = target;

            // 停靠边可能已切换：重建面板 / 控制点使其落到新一侧。
            RebuildOrbit();
            if (_controlPointHost != null)
            {
                double cs = ControlSize;
                double ccx = _side == DockSide.Left
                    ? Center - BallSize / 2 - cs / 2 - 10
                    : Center + BallSize / 2 + cs / 2 + 10;
                Canvas.SetLeft(_controlPointHost, ccx - (cs + 16) / 2);
            }

            // 松手时保持展开；离开后由探测计时器收回成屏幕边缘的「小凸起」。
            _revealed = true;
            SetOrbitVisible(true, animate: false);
            AnimateShift(RevealedShift(), 0);
        }
        else
        {
            // 自由漂浮：整幅可见，面板常显、保持半透明。
            AnimateShift(0, 0);
            SetOrbitVisible(true, animate: false);
        }
        FadeToTargetOpacity(180);
        SavePosition();
    }

    private void MoveToDevice(Point cursorScreen)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = (cursorScreen.X - _dragOffsetDevice.X) / dpi.DpiScaleX;
        double top = (cursorScreen.Y - _dragOffsetDevice.Y) / dpi.DpiScaleY;
        double minL = SystemParameters.VirtualScreenLeft;
        double minT = SystemParameters.VirtualScreenTop;
        double maxL = minL + SystemParameters.VirtualScreenWidth - Width;
        double maxT = minT + SystemParameters.VirtualScreenHeight - Height;
        Left = Math.Clamp(left, minL, Math.Max(minL, maxL));
        Top = Math.Clamp(top, minT, Math.Max(minT, maxT));
    }

    // ==================================================================
    //  控制点长按进度（CompositionTarget.Rendering 逐帧驱动）
    // ==================================================================
    private void OnProgressRendering(object? sender, EventArgs e)
    {
        // 控制点长按：外圈小进度环
        if (_redPressing && !_longPressFired)
        {
            double t = _redPress.Elapsed.TotalMilliseconds / Math.Max(100, S.RedDot.LongPressMs);
            if (t >= 1.0)
            {
                _longPressFired = true;
                _redPressing = false;
                UnsubscribeRendering();
                TriggerCollapseWithFeedback();
            }
        }
    }

    // ==================================================================
    //  管理控制点
    // ==================================================================
    private void RevealControlPoint()
    {
        if (_controlPointHost == null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _controlPointHost.BeginAnimation(OpacityProperty, new DoubleAnimation(_controlPointHost.Opacity, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        if (_controlPointScale != null)
        {
            _controlPointScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(_controlPointScale.ScaleX, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            _controlPointScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(_controlPointScale.ScaleY, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
        UpdateControlPointColor();
    }

    private void ConcealControlPoint()
    {
        if (_controlPointHost == null || _controlPointHost.Opacity <= 0.01) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _controlPointHost.BeginAnimation(OpacityProperty, new DoubleAnimation(_controlPointHost.Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        if (_controlPointScale != null)
        {
            _controlPointScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(_controlPointScale.ScaleX, 0.8, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            _controlPointScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(_controlPointScale.ScaleY, 0.8, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }
    }

    /// <summary>管理控制点统一为与主球一致的深色玻璃质感。</summary>
    private void UpdateControlPointColor(bool animate = true)
    {
        if (_controlPointBrush == null) return;
        var target = ParseColor(S.MainBall.Color, Color.FromRgb(0x0A, 0x0A, 0x0C));
        if (_controlPointBrush is RadialGradientBrush rgba && animate)
        {
            AnimateStopColor(rgba.GradientStops[0], Lighten(target, 0.45));
            AnimateStopColor(rgba.GradientStops[1], target);
            if (rgba.GradientStops.Count > 2) AnimateStopColor(rgba.GradientStops[2], Darken(target, 0.35));
        }
        else
        {
            _controlPointBrush = MakeControlBrush(target);
            if (_controlPoint != null) _controlPoint.Fill = _controlPointBrush;
        }
        if (_controlPoint?.Effect is DropShadowEffect de)
            de.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>把渐变停靠点的颜色动画到目标值（from 取当前值，实现平滑过渡）。</summary>
    private static void AnimateStopColor(GradientStop stop, Color to)
    {
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(250)));
    }

    private void RedDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        e.Handled = true;
        _redPressing = true;
        _longPressFired = false;
        _redPress.Restart();
        _controlPointHost?.CaptureMouse();
        SubscribeRendering();
    }

    private void RedDot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_controlPointHost?.IsMouseCaptured == true) _controlPointHost.ReleaseMouseCapture();
        UnsubscribeRendering();
        e.Handled = true;
        if (_busy) return;

        if (_longPressFired) { _longPressFired = false; return; }

        // 单击 → 最小化全部便签
        if (_redPressing)
        {
            _redPressing = false;
            try { App.Instance.MinimizeAllNotes(); } catch { }
            UpdateControlPointColor();
        }
    }

    private void TriggerCollapseWithFeedback()
    {
        App.Instance.ToggleCollapseNotes();
        Shake();
        UpdateControlPointColor();
    }

    private void Shake()
    {
        StopBreathing();
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.03, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        kf.Completed += (_, _) =>
        {
            SetStableScale(_state is OrbState.Hover ? 1.08 : 1.0);
            if (S.MainBall.Breathing && _state is OrbState.Normal) StartBreathing();
        };
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, kf);
    }

    // ==================================================================
    //  面板开关（右键主球切换）
    // ==================================================================
    /// <summary>右键悬浮图标：展开 / 收起动作面板（切换「显示展开面板」开关并即时重建）。</summary>
    private void ToggleRing()
    {
        S.MainBall.ShowSatellite = !S.MainBall.ShowSatellite;
        Config.Save();
        RebuildOrbit();
        SetOrbitVisible(_revealed, animate: false);
    }

    // ==================================================================
    //  动作分发：单击图标直达「快速」入口
    //    悬浮图标 → 软件主页；便签 → 快速便签；剪切板 → 剪贴板面板；
    //    待办 → 快速待办；文件文件夹备注 → 快速文件树备注。
    // ==================================================================
    private void DispatchSatellite(SatelliteAction action)
    {
        switch (action)
        {
            case SatelliteAction.ToggleCollapse: App.Instance.ToggleCollapseNotes(); UpdateControlPointColor(); break;
            case SatelliteAction.OpenRecentNote: App.Instance.ShowQuickNote(); break;          // 快速便签
            case SatelliteAction.OpenTasks: App.Instance.ShowQuickTask(); break;               // 快速待办
            case SatelliteAction.OpenClips: App.Instance.ShowClipboardPanel(); break;          // 剪切板
            case SatelliteAction.OpenFileNotes: App.Instance.ShowAnnotationPopup(); break;     // 快速文件树备注
            case SatelliteAction.OpenMainWindow: App.Instance.ShowMainWindow(); break;         // 软件主页
            case SatelliteAction.OpenSettings: App.Instance.ShowMainWindow(NavSection.Settings); break;
            case SatelliteAction.Exit: PlayDissolveAndExit(); break;
        }
    }

    // ==================================================================
    //  粒子消散退出
    // ==================================================================
    private void PlayDissolveAndExit()
    {
        if (_busy) return;
        _busy = true;
        GoState(OrbState.ExitAnimation);

        var palette = new List<Color>
        {
            Colors.White, Colors.White, Colors.White, Colors.White,
            Color.FromRgb(0xE0, 0x45, 0x45)
        };

        var ballRect = new Rect(Left + (Center - BallSize / 2), Top + (Center - BallSize / 2), BallSize, BallSize);

        var win = new ParticleWindow();
        win.Show();
        win.Play(ballRect, palette, S.Particle, () =>
        {
            _state = OrbState.Destroy;
            try { App.Instance.Shutdown(); } catch { Application.Current?.Shutdown(); }
        });

        var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
        hideTimer.Start();
    }

    // ==================================================================
    //  位置 / 全屏
    // ==================================================================
    /// <summary>根据记忆位置推断贴边方向（屏幕中线左 / 右），默认右侧。</summary>
    private DockSide DetermineSide()
    {
        try
        {
            var pos = S.Position;
            if (!string.IsNullOrWhiteSpace(pos))
            {
                var parts = pos.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], out var l))
                {
                    double midX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth / 2;
                    return l < midX ? DockSide.Left : DockSide.Right;
                }
            }
        }
        catch { }
        return DockSide.Right;
    }

    private void RestorePosition()
    {
        _snapped = S.MainBall.SnapToEdge;
        _revealed = true;
        _side = DetermineSide();

        double minT = SystemParameters.VirtualScreenTop;
        double maxT = minT + SystemParameters.VirtualScreenHeight - Height;
        double top = SystemParameters.WorkArea.Top + 40;

        try
        {
            var pos = S.Position;
            if (!string.IsNullOrWhiteSpace(pos))
            {
                var parts = pos.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[1], out var t))
                    top = t;
            }
        }
        catch { }

        Top = Math.Clamp(top, minT, Math.Max(minT, maxT));

        if (_snapped)
        {
            // 贴边停靠：窗口整幅落在屏内，用「场景位移」把主球推到屏幕边缘形成小凸起。
            Left = _side == DockSide.Right
                ? SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width
                : SystemParameters.VirtualScreenLeft;
            _revealed = false;
            SetOrbitVisible(false, animate: false);
            AnimateShift(CollapsedShift(), 0);
        }
        else
        {
            // 自由漂浮：整幅可见，面板常显、保持半透明。
            double minL = SystemParameters.VirtualScreenLeft;
            double maxL = minL + SystemParameters.VirtualScreenWidth - Width;
            Left = Math.Clamp(SystemParameters.WorkArea.Right - Width - 28, minL, Math.Max(minL, maxL));
            AnimateShift(0, 0);
            SetOrbitVisible(true, animate: false);
        }
        FadeToTargetOpacity(0);
    }

    private void SavePosition()
    {
        try { S.Position = $"{Left:0},{Top:0}"; Config.Save(); } catch { }
    }

    private void UpdateFullScreenVisibility()
    {
        try
        {
            if (_state is OrbState.ExitAnimation) return;

            if (Native.SHQueryUserNotificationState(out var state) == 0)
            {
                bool full = state == Native.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                         || state == Native.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
                var target = full ? Visibility.Hidden : Visibility.Visible;
                if (Visibility != target)
                {
                    Visibility = target;
                    if (!full) GoState(OrbState.Normal);   // 恢复显示时归位
                }
            }
        }
        catch { }
    }

    // ==================================================================
    //  小工具
    // ==================================================================
    private static ImageBrush? LoadPackImage(string relative)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri("pack://application:,,,/Assets/" + relative);
            bmp.EndInit();
            bmp.Freeze();
            return new ImageBrush(bmp) { Stretch = Stretch.Uniform };
        }
        catch { return null; }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { if (ColorConverter.ConvertFromString(hex) is Color c) return c; } catch { }
        return fallback;
    }

    private static Color Lighten(Color c, double amount)
        => Color.FromRgb((byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
                         (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
                         (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));

    private static Color Darken(Color c, double amount)
        => Color.FromRgb((byte)Math.Clamp(c.R * (1 - amount), 0, 255),
                         (byte)Math.Clamp(c.G * (1 - amount), 0, 255),
                         (byte)Math.Clamp(c.B * (1 - amount), 0, 255));
}
