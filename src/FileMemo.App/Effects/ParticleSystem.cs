using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FileMemo.App.Effects;

/// <summary>
/// 粒子系统：对象池 + CompositionTarget.Rendering 逐帧驱动（帧内不分配新对象）。
/// 物理：v *= damping，vy += gravity。
/// </summary>
public sealed class ParticleSystem
{
    private sealed class Particle
    {
        public double X, Y, VX, VY, Size, Life, MaxLife;
        public Color Color;
        public Ellipse? El;
        public bool Alive;
        public readonly SolidColorBrush Brush = new(Colors.White);
    }

    private readonly Canvas _canvas;
    private readonly List<Particle> _pool = new();
    private readonly List<Particle> _active = new();
    private readonly Random _rnd = new();
    private double _gravity = 0.15, _damping = 0.98;
    private bool _running;
    private TimeSpan _last;

    public ParticleSystem(Canvas canvas) => _canvas = canvas;

    public int ActiveCount => _active.Count;

    /// <summary>预建对象池，避免运行期反复分配。</summary>
    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var p = new Particle();
            var el = new Ellipse { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Fill = p.Brush };
            _canvas.Children.Add(el);
            p.El = el;
            _pool.Add(p);
        }
    }

    public void Emit(double cx, double cy, IReadOnlyList<Color> colors, int count,
                     double speedMin, double speedMax, double lifeSec)
    {
        if (colors.Count == 0) return;
        for (int i = 0; i < count; i++)
        {
            var p = Rent();
            if (p == null) return;
            double ang = _rnd.NextDouble() * Math.PI * 2;
            double spd = speedMin + _rnd.NextDouble() * (speedMax - speedMin);
            p.X = cx;
            p.Y = cy;
            p.VX = Math.Cos(ang) * spd;
            p.VY = Math.Sin(ang) * spd;
            p.Size = 3 + _rnd.NextDouble() * 7;
            p.MaxLife = lifeSec * (0.75 + _rnd.NextDouble() * 0.5);
            p.Life = p.MaxLife;
            p.Color = colors[_rnd.Next(colors.Count)];
            p.Alive = true;
            if (p.El != null)
            {
                p.Brush.Color = p.Color;
                p.El.Width = p.Size;
                p.El.Height = p.Size;
                p.El.Opacity = 1;
                p.El.Visibility = Visibility.Visible;
                Canvas.SetLeft(p.El, p.X - p.Size / 2);
                Canvas.SetTop(p.El, p.Y - p.Size / 2);
            }
            _active.Add(p);
        }
    }

    private Particle? Rent()
    {
        for (int i = 0; i < _pool.Count; i++)
            if (!_pool[i].Alive) return _pool[i];
        return null;
    }

    public void Start(double gravity, double damping)
    {
        _gravity = gravity;
        _damping = damping;
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double dt = 1.0;
        if (e is RenderingEventArgs re)
        {
            if (_last != TimeSpan.Zero) dt = (re.RenderingTime - _last).TotalSeconds * 60.0;
            _last = re.RenderingTime;
        }
        if (dt <= 0 || dt > 5) dt = 1;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.VX *= _damping;
            p.VY *= _damping;
            p.VY += _gravity * dt;
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.Life -= dt / 60.0;

            double t = p.MaxLife <= 0 ? 0 : Math.Max(0, p.Life / p.MaxLife);
            if (p.El != null)
            {
                Canvas.SetLeft(p.El, p.X - p.Size / 2);
                Canvas.SetTop(p.El, p.Y - p.Size / 2);
                p.El.Opacity = t < 0.25 ? t / 0.25 : 1.0;   // 最后 25% 淡出
            }
            if (p.Life <= 0) Release(p, i);
        }
        if (_active.Count == 0) Stop();
    }

    private void Release(Particle p, int index)
    {
        p.Alive = false;
        if (p.El != null) p.El.Visibility = Visibility.Collapsed;
        _active.RemoveAt(index);
    }
}
