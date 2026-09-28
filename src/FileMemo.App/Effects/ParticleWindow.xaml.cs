using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileMemo.App.Models;

namespace FileMemo.App.Effects;

/// <summary>全屏透明粒子消散窗口：播放完毕后回调（退出应用）。</summary>
public partial class ParticleWindow : Window
{
    private readonly ParticleSystem _system;
    private DispatcherTimer? _finish;

    public ParticleWindow()
    {
        InitializeComponent();
        _system = new ParticleSystem(Stage);
    }

    /// <param name="area">主球在屏幕上的矩形（DIP）。</param>
    /// <param name="colors">粒子取色集合。</param>
    /// <param name="cfg">粒子配置。</param>
    /// <param name="onComplete">播放完成回调。</param>
    public void Play(Rect area, IEnumerable<Color> colors, ParticleConfig cfg, Action onComplete)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Stage.Width = Width;
        Stage.Height = Height;

        _system.Prewarm(Math.Max(400, cfg.Count + 80));

        var palette = colors.ToList();
        if (palette.Count == 0) palette.Add(Colors.White);

        // 球心 → 全屏坐标
        double cx = area.X - Left + area.Width / 2;
        double cy = area.Y - Top + area.Height / 2;

        _system.Emit(cx, cy, palette, cfg.Count, 2, 8, cfg.DurationMs / 1000.0);
        _system.Start(cfg.Gravity, cfg.Damping);

        // 轻微屏幕闪白（200ms）
        var flash = new Rectangle
        {
            Fill = Brushes.White,
            Opacity = 0,
            Width = Width,
            Height = Height,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(flash, 0);
        Canvas.SetTop(flash, 0);
        Stage.Children.Add(flash);
        var fa = new DoubleAnimationUsingKeyFrames();
        fa.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fa.KeyFrames.Add(new LinearDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        fa.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        flash.BeginAnimation(OpacityProperty, fa);

        _finish = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(cfg.DurationMs + 250) };
        _finish.Tick += (_, _) =>
        {
            _finish?.Stop();
            onComplete();
        };
        _finish.Start();
    }

    public void StopAll()
    {
        _system.Stop();
        _finish?.Stop();
    }
}
