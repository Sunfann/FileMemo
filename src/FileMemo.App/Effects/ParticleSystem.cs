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
        /// <summary>true = 透明度全程线性 1→0（规格）；false = 仅最后 25% 淡出（旧行为）。</summary>
        public bool LinearFade;
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
            p.LinearFade = false;      // 旧式发射：仅最后 25% 淡出（池复用必须显式重置）
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

    /// <summary>
    /// 径向发射（退出消散特效专用）：按「随机 360° 方向 + 指定扩散距离 + 固定存活时长」生成粒子。
    /// 初速 = 距离 / 时长，之后逐帧被 damping 衰减 → 位移曲线自然趋近 ease-out。
    /// </summary>
    /// <param name="distMin">扩散距离下限 px（规格 50）。</param>
    /// <param name="distMax">扩散距离上限 px（规格 150）。</param>
    /// <param name="lifeSec">单颗粒子存活时长 秒（规格 0.6）。</param>
    /// <param name="sizeMin">粒径下限 px（规格 2）。</param>
    /// <param name="sizeMax">粒径上限 px（规格 8）。</param>
    public void EmitRadial(double cx, double cy, IReadOnlyList<Color> colors, int count,
                           double distMin, double distMax, double lifeSec,
                           double sizeMin, double sizeMax)
    {
        if (colors.Count == 0) return;
        if (lifeSec <= 0) lifeSec = 0.6;
        if (sizeMax < sizeMin) sizeMax = sizeMin;
        if (distMax < distMin) distMax = distMin;

        for (int i = 0; i < count; i++)
        {
            var p = Rent();
            if (p == null) return;

            double ang = _rnd.NextDouble() * Math.PI * 2;      // 随机 360°
            double dist = distMin + _rnd.NextDouble() * (distMax - distMin);
            double speed = dist / lifeSec;                     // 初速：走完目标距离所需的平均速度

            p.X = cx;
            p.Y = cy;
            p.VX = Math.Cos(ang) * speed;
            p.VY = Math.Sin(ang) * speed;
            p.Size = sizeMin + _rnd.NextDouble() * (sizeMax - sizeMin);
            p.MaxLife = lifeSec;
            p.Life = lifeSec;
            p.LinearFade = true;                              // 规格要求透明度全程 1 → 0
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
                // LinearFade：整个生命周期线性淡出（1 → 0）；
                // 否则沿用旧行为：仅在最后 25% 生命淡出。
                p.El.Opacity = p.LinearFade ? t : (t < 0.25 ? t / 0.25 : 1.0);
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
