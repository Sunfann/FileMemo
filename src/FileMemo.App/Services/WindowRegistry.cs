using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FileMemo.App.Services;

/// <summary>
/// 便签 / 浮层窗口注册表：供「一键收纳 / 释放」批量动画使用。
/// 收纳动画：TranslateTransform + ScaleTransform 收拢到主球方向（280ms，CubicEase）。
/// 支持「窗口固定」：被固定的窗口置顶显示，且不参与批量收纳 / 最小化 / 释放。
/// </summary>
public sealed class WindowRegistry
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(280);
    private readonly List<Window> _windows = new();
    private readonly HashSet<Window> _pinned = new();

    public IReadOnlyList<Window> Windows => _windows;

    public void Register(Window w)
    {
        if (w == null) return;
        if (!_windows.Contains(w)) _windows.Add(w);
        w.Closed += (_, _) =>
        {
            _windows.Remove(w);
            _pinned.Remove(w);
        };
    }

    public void Unregister(Window w) => _windows.Remove(w);

    // ---- 窗口固定 ----

    /// <summary>该窗口是否处于「固定」状态。</summary>
    public bool IsPinned(Window w) => w != null && _pinned.Contains(w);

    /// <summary>当前被固定的窗口数量。</summary>
    public int PinnedCount => _pinned.Count;

    /// <summary>设置窗口固定：固定则置顶（Topmost），并排除在批量收纳 / 最小化之外。</summary>
    public void SetPinned(Window w, bool pinned)
    {
        if (w == null) return;
        if (pinned) _pinned.Add(w); else _pinned.Remove(w);
        try { w.Topmost = pinned; } catch { }
    }

    public bool AnyVisible()
    {
        foreach (var w in _windows)
        {
            try { if (w.IsVisible) return true; } catch { }
        }
        return false;
    }

    /// <summary>收纳全部（已固定窗口跳过）：向主球中心收拢并淡出，然后隐藏。</summary>
    public void CollapseAll(Point ballCenterScreen)
    {
        foreach (var w in _windows.ToList())
        {
            try
            {
                if (IsPinned(w)) continue;          // 固定窗口不收纳
                if (!w.IsVisible) continue;
                var fe = w.Content as FrameworkElement;
                if (fe == null) { w.Hide(); continue; }

                fe.RenderTransformOrigin = new Point(0.5, 0.5);
                var scale = new ScaleTransform(1, 1);
                var trans = new TranslateTransform(0, 0);
                var group = new TransformGroup();
                group.Children.Add(scale);
                group.Children.Add(trans);
                fe.RenderTransform = group;

                double cx = w.Left + w.ActualWidth / 2;
                double cy = w.Top + w.ActualHeight / 2;
                double dx = ballCenterScreen.X - cx;
                double dy = ballCenterScreen.Y - cy;

                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.2, Duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.2, Duration) { EasingFunction = ease });
                trans.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, Duration) { EasingFunction = ease });
                trans.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, Duration) { EasingFunction = ease });
                w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, Duration) { EasingFunction = ease });

                var timer = new DispatcherTimer { Interval = Duration };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    w.Hide();
                    w.Opacity = 1;
                    fe.RenderTransform = Transform.Identity;
                };
                timer.Start();
            }
            catch { try { w.Hide(); } catch { } }
        }
    }

    /// <summary>释放全部（已固定窗口跳过）：反向动画重新显示。</summary>
    public void ReleaseAll()
    {
        foreach (var w in _windows.ToList())
        {
            try
            {
                if (IsPinned(w)) continue;          // 固定窗口始终可见，无需释放动画
                var fe = w.Content as FrameworkElement;
                double dx = 0, dy = 0;
                ScaleTransform? scale = null; TranslateTransform? trans = null;

                if (fe != null)
                {
                    fe.RenderTransformOrigin = new Point(0.5, 0.5);
                    scale = new ScaleTransform(0.2, 0.2);
                    trans = new TranslateTransform(dx, dy);
                    var group = new TransformGroup();
                    group.Children.Add(scale);
                    group.Children.Add(trans);
                    fe.RenderTransform = group;
                    w.Opacity = 0;
                }

                w.Show();

                if (fe != null && scale != null && trans != null)
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, 1, Duration) { EasingFunction = ease });
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, Duration) { EasingFunction = ease });
                    w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration) { EasingFunction = ease });
                }
                w.Activate();
            }
            catch { try { w.Show(); w.Opacity = 1; } catch { } }
        }
    }

    /// <summary>最小化全部（已固定窗口跳过）。</summary>
    public void MinimizeAll()
    {
        foreach (var w in _windows.ToList())
        {
            try
            {
                if (IsPinned(w)) continue;          // 固定窗口不最小化
                if (w.IsVisible) w.WindowState = WindowState.Minimized;
            }
            catch { }
        }
    }
}
