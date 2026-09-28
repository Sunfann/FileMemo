using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

namespace FileMemo.App.Views;

/// <summary>
/// 桌面悬浮球系统（按提示词实现）：
/// - 深色玻璃质感主球 + 环绕光环；呼吸动画、悬停放大。
/// - 卫星球（默认 4 个）沿弧线排列，悬停主球淡入展开，鼠标离开 1.5s 收拢。
/// - 红点常驻：长按 1000ms（Mouse.Capture + Stopwatch + 环形进度）触发全局「收纳 / 释放」。
/// - 长按主球 1500ms 触发粒子消散特效并退出。
/// - 拖拽移动 + 靠近边缘吸附 + 越过边缘半隐藏；WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW；
///   全屏应用（D3D / 演示模式）时自动隐藏。
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const double Center = 110;
    private const double SatelliteRadius = 70;
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MainLongPress = TimeSpan.FromMilliseconds(1500);

    private BallConfigService Config => App.Instance.BallConfig;
    private BallSettings S => Config.Current;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _fullScreenTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly List<Grid> _satellites = new();

    private Ellipse? _mainBall;
    private ScaleTransform? _mainScale;
    private Ellipse? _redDot;
    private Path? _progressArc;
    private PathFigure? _progressFigure;
    private ArcSegment? _progressSeg;

    private readonly Stopwatch _redPress = new();
    private readonly Stopwatch _mainPress = new();
    private bool _redPressing, _mainPressing, _longPressFired, _busy;

    private bool _dragging;
    private Point _dragStartScreen;
    private Point _dragOffsetDevice;

    private Popup? _menu;
    private bool _menuOpen;

    private double BallSize => Math.Clamp(S.MainBall.Size, 32, 128);

    public FloatingBallWindow()
    {
        InitializeComponent();
        _hideTimer.Interval = HideDelay;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); CollapseSatellites(); };
        _fullScreenTimer.Tick += (_, _) => UpdateFullScreenVisibility();
        Loaded += (_, _) => { BuildVisual(); RestorePosition(); StartBreathing(); _fullScreenTimer.Start(); };
        Closed += (_, _) =>
        {
            _fullScreenTimer.Stop();
            CompositionTarget.Rendering -= OnProgressRendering;
            CloseMenu();
        };
        MouseLeave += (_, _) => { if (!_menuOpen) _hideTimer.Start(); };
        MouseEnter += (_, _) => { _hideTimer.Stop(); RevealSatellites(); HoverMain(true); };
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

    // ------------------------------------------------------------ 绘制
    private void BuildVisual()
    {
        Stage.Children.Clear();
        _satellites.Clear();

        double r = BallSize / 2;
        var accent = ParseColor(S.MainBall.GlowColor, Color.FromRgb(0x00, 0x78, 0xD4));

        // 环绕光环
        if (S.MainBall.RingEnabled)
        {
            var rings = BuildRings(r * 2.35);
            Canvas.SetLeft(rings, Center - r * 1.18);
            Canvas.SetTop(rings, Center - r * 1.18);
            Stage.Children.Add(rings);
        }

        // 卫星球（先加入 → 处于主球下层）
        var sats = S.Satellites.Where(s => s.Enabled).Take(6).ToList();
        int n = sats.Count;
        for (int i = 0; i < n; i++)
        {
            double ang = n <= 1 ? 0 : (-45 + 90.0 * i / (n - 1)) * Math.PI / 180.0;
            double sx = Center + SatelliteRadius * Math.Cos(ang);
            double sy = Center + SatelliteRadius * Math.Sin(ang);
            double size = Math.Max(16, BallSize * 0.36);
            var color = ParseColor(sats[i].Color, accent);
            var sat = MakeSatellite(size, color, sats[i], sx, sy);
            _satellites.Add(sat);
            Stage.Children.Add(sat);
        }

        // 主球
        _mainScale = new ScaleTransform(1, 1);
        _mainBall = MakeBall(BallSize, ParseColor(S.MainBall.Color, Color.FromRgb(0x0A, 0x0A, 0x0A)), accent, S.MainBall.UseImage);
        _mainBall.RenderTransformOrigin = new Point(0.5, 0.5);
        _mainBall.RenderTransform = _mainScale;
        Canvas.SetLeft(_mainBall, Center - r);
        Canvas.SetTop(_mainBall, Center - r);
        _mainBall.MouseEnter += (_, _) => { _hideTimer.Stop(); RevealSatellites(); HoverMain(true); };
        _mainBall.MouseLeave += (_, _) => { if (!_menuOpen) _hideTimer.Start(); HoverMain(false); };
        _mainBall.MouseLeftButtonDown += MainBall_MouseLeftButtonDown;
        _mainBall.MouseMove += MainBall_MouseMove;
        _mainBall.MouseLeftButtonUp += MainBall_MouseLeftButtonUp;
        _mainBall.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenRadialMenu(); };
        Stage.Children.Add(_mainBall);

        // 红点 + 环形进度
        if (S.RedDot.Enabled)
        {
            double dotD = Math.Max(10, BallSize * 0.22);
            double dotCx = Center + r * 0.78;
            double dotCy = Center - r * 0.78;

            _progressArc = BuildProgressArc(new Point(dotCx, dotCy), dotD * 0.95);
            _progressArc.Opacity = 0;
            Stage.Children.Add(_progressArc);

            _redDot = new Ellipse
            {
                Width = dotD,
                Height = dotD,
                Fill = MakeDotBrush(),
                Cursor = Cursors.Hand,
                ToolTip = "长按一秒：一键收纳 / 释放全部便签",
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0xFF, 0x3B, 0x30),
                    BlurRadius = 20,
                    ShadowDepth = 0,
                    Opacity = 0.85
                }
            };
            Canvas.SetLeft(_redDot, dotCx - dotD / 2);
            Canvas.SetTop(_redDot, dotCy - dotD / 2);
            _redDot.MouseLeftButtonDown += RedDot_MouseLeftButtonDown;
            _redDot.MouseLeftButtonUp += RedDot_MouseLeftButtonUp;
            Stage.Children.Add(_redDot);
        }

        Opacity = Math.Clamp(S.MainBall.Opacity, 0.3, 1.0);
        CollapseSatellites(instant: true);
    }

    private Ellipse MakeBall(double size, Color baseColor, Color glow, bool useImage)
    {
        var ball = new Ellipse
        {
            Width = size,
            Height = size,
            Cursor = Cursors.SizeAll,
            Effect = new DropShadowEffect { Color = glow, BlurRadius = size * 0.55, ShadowDepth = 0, Opacity = 0.6 }
        };
        if (useImage)
        {
            var img = LoadPackImage("ball.png");
            ball.Fill = img ?? (Brush)MakeGlassBrush(baseColor, glow);
        }
        else
        {
            ball.Fill = MakeGlassBrush(baseColor, glow);
        }
        return ball;
    }

    private static Brush MakeGlassBrush(Color baseColor, Color glow)
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.34, 0.26),
            Center = new Point(0.42, 0.40),
            RadiusX = 0.78,
            RadiusY = 0.78
        };
        b.GradientStops.Add(new GradientStop(Lighten(baseColor, 0.42), 0.0));
        b.GradientStops.Add(new GradientStop(baseColor, 0.55));
        b.GradientStops.Add(new GradientStop(Darken(baseColor, 0.35), 0.86));
        b.GradientStops.Add(new GradientStop(glow, 1.0));
        return b;
    }

    private static Brush MakeDotBrush()
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.36, 0.30),
            Center = new Point(0.45, 0.42),
            RadiusX = 0.72,
            RadiusY = 0.72
        };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xC8, 0xC0), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x3B, 0x30), 0.5));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xC0, 0x14, 0x0C), 1.0));
        return b;
    }

    private Canvas BuildRings(double size)
    {
        var canvas = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        double c = size / 2;
        var glow = ParseColor(S.MainBall.GlowColor, Color.FromRgb(0x00, 0x78, 0xD4));
        for (int k = 0; k < 2; k++)
        {
            double rad = size * (0.30 + k * 0.16);
            var arc = new Ellipse
            {
                Width = rad * 2,
                Height = rad * 2,
                Stroke = new SolidColorBrush(glow),
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 1.4, 2.6 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.7 - k * 0.25,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0)
            };
            Canvas.SetLeft(arc, c - rad);
            Canvas.SetTop(arc, c - rad);
            canvas.Children.Add(arc);
            if (k == 0)
            {
                ((RotateTransform)arc.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        return canvas;
    }

    private Grid MakeSatellite(double size, Color color, SatelliteConfig cfg, double cx, double cy)
    {
        var host = new Grid { Width = size, Height = size, Cursor = Cursors.Hand, ToolTip = cfg.Action.ToString() };
        var ball = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = MakeGlassBrush(color, color),
            Effect = new DropShadowEffect { Color = color, BlurRadius = size * 0.7, ShadowDepth = 0, Opacity = 0.5 }
        };
        var glyph = new TextBlock
        {
            Text = string.IsNullOrEmpty(cfg.Icon) ? "\uE8B7" : cfg.Icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"),
            FontSize = size * 0.42,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        host.Children.Add(ball);
        host.Children.Add(glyph);

        var scale = new ScaleTransform(0.3, 0.3);
        var trans = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(trans);
        host.RenderTransformOrigin = new Point(0.5, 0.5);
        host.RenderTransform = group;
        host.Opacity = 0;
        Canvas.SetLeft(host, cx - size / 2);
        Canvas.SetTop(host, cy - size / 2);
        host.Tag = new object[] { scale, trans, cx - size / 2, cy - size / 2 };
        host.MouseLeftButtonUp += (_, e) => { e.Handled = true; DispatchSatellite(cfg.Action); };
        host.MouseEnter += (_, _) => _hideTimer.Stop();
        return host;
    }

    private void DispatchSatellite(SatelliteAction action)
    {
        switch (action)
        {
            case SatelliteAction.ToggleCollapse: App.Instance.ToggleCollapseNotes(); break;
            case SatelliteAction.OpenRecentNote: App.Instance.ShowQuickNote(); break;
            case SatelliteAction.OpenTasks: App.Instance.ShowMainWindow(); break;
            case SatelliteAction.OpenClips: App.Instance.ShowClipboardPanel(); break;
            case SatelliteAction.OpenMainWindow: App.Instance.ShowMainWindow(); break;
            case SatelliteAction.OpenSettings: App.Instance.ShowMainWindow(); break;
            case SatelliteAction.Exit: PlayDissolveAndExit(); break;
        }
    }

    private void RevealSatellites()
    {
        if (_busy) return;
        int i = 0;
        foreach (var g in _satellites)
        {
            if (g.Tag is not object[] tag) continue;
            var scale = (ScaleTransform)tag[0];
            var trans = (TranslateTransform)tag[1];
            double fx = (double)tag[2], fy = (double)tag[3];
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var delay = TimeSpan.FromMilliseconds(40 * i++);
            double dx = Center - fx;
            double dy = Center - fy;
            trans.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(240)) { BeginTime = delay, EasingFunction = ease });
            trans.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(240)) { BeginTime = delay, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay, EasingFunction = ease });
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(g.Opacity, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = delay });
        }
    }

    private void CollapseSatellites(bool instant = false)
    {
        foreach (var g in _satellites)
        {
            if (instant) { g.Opacity = 0; continue; }
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(g.Opacity, 0, TimeSpan.FromMilliseconds(200)));
        }
    }

    private void HoverMain(bool on)
    {
        if (_mainScale == null) return;
        double to = on ? 1.08 : 1.0;
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
        if (_mainBall?.Effect is DropShadowEffect sh)
            sh.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.95 : 0.6, TimeSpan.FromMilliseconds(140)));
    }

    private void StartBreathing()
    {
        if (_mainScale == null) return;
        var breathe = new DoubleAnimation(1.0, 1.02, TimeSpan.FromSeconds(1.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _mainScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        _mainScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    // ------------------------------------------------------------ 主球拖动 / 点击 / 长按
    private void MainBall_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        e.Handled = true;
        CloseMenu();

        if (e.ClickCount == 2)
        {
            App.Instance.ShowClipboardPanel();   // 双击 → 剪贴板面板
            return;
        }

        _mainPressing = true;
        _longPressFired = false;
        _mainPress.Restart();
        _dragging = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        _dragOffsetDevice = new Point(
            _dragStartScreen.X - Left * dpi.DpiScaleX,
            _dragStartScreen.Y - Top * dpi.DpiScaleY);
        _mainBall?.CaptureMouse();
        CompositionTarget.Rendering -= OnProgressRendering;
        CompositionTarget.Rendering += OnProgressRendering;
    }

    private void MainBall_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mainPressing || _busy) return;
        var cur = PointToScreen(e.GetPosition(this));
        double dx = cur.X - _dragStartScreen.X;
        double dy = cur.Y - _dragStartScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 5) _dragging = true;
        if (_dragging) MoveToDevice(cur);
    }

    private void MainBall_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mainBall?.IsMouseCaptured == true) _mainBall.ReleaseMouseCapture();
        CompositionTarget.Rendering -= OnProgressRendering;
        _mainPressing = false;
        if (_busy) return;
        e.Handled = true;

        if (_longPressFired) return;
        if (_dragging) FinishDrag();
        else App.Instance.ShowQuickNote();   // 单击 → 命令面板（占位用快速便签）
    }

    private void FinishDrag()
    {
        if (S.MainBall.SnapToEdge)
        {
            double screenMid = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth / 2;
            double cx = Left + Width / 2;
            double target = cx < screenMid
                ? SystemParameters.VirtualScreenLeft
                : SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width;
            BeginAnimation(LeftProperty, new DoubleAnimation(Left, target, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Left = target;
        }
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

    // ------------------------------------------------------------ 红点长按
    private void RedDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        e.Handled = true;
        _redPressing = true;
        _longPressFired = false;
        _redPress.Restart();
        _redDot?.CaptureMouse();
        CompositionTarget.Rendering -= OnProgressRendering;
        CompositionTarget.Rendering += OnProgressRendering;
    }

    private void RedDot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_redDot?.IsMouseCaptured == true) _redDot.ReleaseMouseCapture();
        _redPressing = false;
        CompositionTarget.Rendering -= OnProgressRendering;
        SetProgress(0);
        e.Handled = true;
    }

    private void OnProgressRendering(object? sender, EventArgs e)
    {
        if (_redPressing)
        {
            double t = _redPress.Elapsed.TotalMilliseconds / Math.Max(100, S.RedDot.LongPressMs);
            SetProgress(Math.Min(1.0, t));
            if (t >= 1.0 && !_longPressFired)
            {
                _longPressFired = true;
                _redPressing = false;
                CompositionTarget.Rendering -= OnProgressRendering;
                SetProgress(0);
                TriggerCollapseWithFeedback();
            }
        }
        else if (_mainPressing && !_dragging)
        {
            if (_mainPress.Elapsed >= MainLongPress && !_longPressFired)
            {
                _longPressFired = true;
                _mainPressing = false;
                CompositionTarget.Rendering -= OnProgressRendering;
                PlayDissolveAndExit();
            }
        }
    }

    private void TriggerCollapseWithFeedback()
    {
        App.Instance.ToggleCollapseNotes();
        Shake();
    }

    private void Shake()
    {
        if (_mainScale == null || _mainBall == null) return;
        var sb = new Storyboard();
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        Storyboard.SetTarget(kf, _mainBall);
        Storyboard.SetTargetProperty(kf, new PropertyPath("RenderTransform.(ScaleTransform.ScaleX)"));
        sb.Children.Add(kf);
        sb.Begin();
    }

    private Path BuildProgressArc(Point center, double radius)
    {
        var fig = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
        var seg = new ArcSegment
        {
            Point = new Point(center.X, center.Y - radius),
            Size = new Size(radius, radius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        fig.Segments.Add(seg);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        _progressFigure = fig;
        _progressSeg = seg;
        return new Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4E)),
            StrokeThickness = 2.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
    }

    private void SetProgress(double t)
    {
        if (_progressArc == null || _progressFigure == null || _progressSeg == null) return;
        double dotD = Math.Max(10, BallSize * 0.22);
        double cxp = Center + (BallSize / 2) * 0.78;
        double cyp = Center - (BallSize / 2) * 0.78;
        double rad = dotD * 0.95;
        double ang = (-90 + 360 * Math.Clamp(t, 0, 1)) * Math.PI / 180.0;
        _progressFigure.StartPoint = new Point(cxp, cyp - rad);
        _progressSeg.Point = new Point(cxp + rad * Math.Cos(ang), cyp + rad * Math.Sin(ang));
        _progressSeg.Size = new Size(rad, rad);
        _progressSeg.IsLargeArc = t > 0.5;
        _progressArc.Opacity = t <= 0.001 ? 0 : 0.95;
    }

    // ------------------------------------------------------------ 径向菜单
    private void OpenRadialMenu()
    {
        CloseMenu();
        var canvas = new Canvas { Width = 220, Height = 220 };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x20, 0x22)),
            CornerRadius = new CornerRadius(110),
            Child = canvas,
            Width = 220,
            Height = 220,
            Effect = (Effect)FindResource("ShadowPopup")
        };

        var items = new (string text, SatelliteAction action)[]
        {
            ("新建便签", SatelliteAction.OpenRecentNote),
            ("新建待办", SatelliteAction.OpenTasks),
            ("打开主窗口", SatelliteAction.OpenMainWindow),
            ("设置", SatelliteAction.OpenSettings),
            ("退出", SatelliteAction.Exit),
        };
        double rr = 78;
        for (int i = 0; i < items.Length; i++)
        {
            double ang = (-90 + 360.0 * i / items.Length) * Math.PI / 180.0;
            double x = 110 + rr * Math.Cos(ang);
            double y = 110 + rr * Math.Sin(ang);
            var btn = new Button
            {
                Content = items[i].text,
                Width = 84,
                Height = 30,
                FontSize = 12,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x3A, 0x3E)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = items[i].action
            };
            btn.Click += (s, _) =>
            {
                var act = (SatelliteAction)((Button)s!).Tag;
                CloseMenu();
                DispatchSatellite(act);
            };
            Canvas.SetLeft(btn, x - 42);
            Canvas.SetTop(btn, y - 15);
            canvas.Children.Add(btn);
        }

        _menu = new Popup
        {
            Child = border,
            PlacementTarget = _mainBall ?? (UIElement)this,
            Placement = PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        _menu.Closed += (_, _) => _menuOpen = false;
        _menuOpen = true;
        _menu.IsOpen = true;
    }

    private void CloseMenu()
    {
        try { if (_menu != null) _menu.IsOpen = false; } catch { }
        _menu = null;
        _menuOpen = false;
    }

    // ------------------------------------------------------------ 粒子消散退出
    private void PlayDissolveAndExit()
    {
        if (_busy) return;
        _busy = true;
        CloseMenu();

        var palette = new List<Color>
        {
            ParseColor(S.MainBall.GlowColor, Color.FromRgb(0x00, 0x78, 0xD4)),
            Color.FromRgb(0xFF, 0x3B, 0x30),
            Color.FromRgb(0x2E, 0x86, 0xFF),
            Colors.White
        };

        var ballRect = new Rect(Left + (Center - BallSize / 2), Top + (Center - BallSize / 2), BallSize, BallSize);

        var win = new ParticleWindow();
        win.Show();
        win.Play(ballRect, palette, S.Particle, () =>
        {
            try { App.Instance.Shutdown(); } catch { Application.Current?.Shutdown(); }
        });

        Hide();
    }

    // ------------------------------------------------------------ 位置 / 全屏
    private void RestorePosition()
    {
        try
        {
            var pos = S.Position;
            if (!string.IsNullOrWhiteSpace(pos))
            {
                var parts = pos.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], out var l) && double.TryParse(parts[1], out var t))
                {
                    Left = l; Top = t; return;
                }
            }
        }
        catch { }
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
    }

    private void SavePosition()
    {
        try { S.Position = $"{Left:0},{Top:0}"; Config.Save(); } catch { }
    }

    private void UpdateFullScreenVisibility()
    {
        try
        {
            if (Native.SHQueryUserNotificationState(out var state) == 0)
            {
                bool full = state == Native.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                         || state == Native.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
                Visibility = full ? Visibility.Hidden : Visibility.Visible;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ 小工具
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
