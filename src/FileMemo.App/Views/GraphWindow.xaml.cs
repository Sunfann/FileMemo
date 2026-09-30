using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FileMemo.App.Models;
using FileMemo.App.Services;

namespace FileMemo.App.Views;

/// <summary>
/// 关系图谱 / 知识网络视图（需求 3.9，P2）。
///
/// 数据来自 <c>App.Instance.Graph</c>；采用轻量力导向布局（斥力 + 弹簧 + 向心），
/// 在 Canvas 上绘制节点与连线。无第三方依赖，Win10/11 通用。
///
/// 配色不再硬编码：节点 / 连线色从主题资源（GraphLegendBrush1..4、TextPrimaryBrush 等）
/// 读取，因此浅色 / 深色主题下都能保持可读；主题切换时重建已绘制元素。
/// </summary>
public partial class GraphWindow : Window
{
    private GraphData _graph = new();
    private string? _focusId;

    public GraphWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DwmService.ApplyShellTheme(this);
        Loaded += (_, _) =>
        {
            Reload();
            ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>主题切换：重绘画布（已绘制的 SolidColorBrush 不会自动跟随）+ 同步标题栏。</summary>
    private void OnThemeChanged()
    {
        try { DwmService.ApplyShellTheme(this); Render(); } catch { }
    }

    // 从资源字典取画刷，取不到时给出安全回退色，避免抛异常
    private Brush Res(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private SolidColorBrush Tint(Brush source, Color fallback)
        => source is SolidColorBrush s ? s : new SolidColorBrush(fallback);

    private Color KindColor(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Note => Tint(Res("GraphLegendBrush1", Brushes.SteelBlue), Colors.SteelBlue).Color,
        GraphNodeKind.File => Tint(Res("GraphLegendBrush2", Brushes.Green), Colors.Green).Color,
        GraphNodeKind.Folder => Tint(Res("GraphLegendBrush3", Brushes.MediumPurple), Colors.MediumPurple).Color,
        GraphNodeKind.Task => Tint(Res("GraphLegendBrush4", Brushes.Orange), Colors.Orange).Color,
        _ => Colors.Gray,
    };

    private Color EdgeColor(string type) => type switch
    {
        "wiki" => KindColor(GraphNodeKind.Note),
        "version" => KindColor(GraphNodeKind.File),
        "task" => KindColor(GraphNodeKind.Task),
        _ => Colors.Gray,
    };

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reset_Click(object sender, RoutedEventArgs e) { _focusId = null; Reload(); }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_focusId)) return;
        try
        {
            _graph = App.Instance.Graph.Neighborhood(_focusId, 2);
            Layout();
            Render();
            StatusText.Text = $"聚焦模式：{_graph.Nodes.Count} 节点 / {_graph.Edges.Count} 连线";
        }
        catch { }
    }

    private void Reload()
    {
        try
        {
            _graph = _focusId != null
                ? App.Instance.Graph.Neighborhood(_focusId, 2)
                : App.Instance.Graph.Build(App.Instance.Settings.GraphMaxNodes);
            Layout();
            Render();
            StatusText.Text = $"{_graph.Nodes.Count} 节点 / {_graph.Edges.Count} 连线";
        }
        catch (Exception ex)
        {
            StatusText.Text = "图谱构建失败：" + ex.Message;
        }
    }

    /// <summary>轻量力导向布局：斥力 + 弹簧 + 向心，迭代收敛后固定坐标。</summary>
    private void Layout()
    {
        var nodes = _graph.Nodes;
        if (nodes.Count == 0) return;

        double w = 1800, h = 1400, cx = w / 2, cy = h / 2;
        var rnd = new Random(20260927);

        // 初始环形分布，避免完全重合
        for (int i = 0; i < nodes.Count; i++)
        {
            double ang = 2 * Math.PI * i / Math.Max(1, nodes.Count);
            double radius = Math.Min(cx, cy) * (0.5 + rnd.NextDouble() * 0.4);
            nodes[i].X = cx + radius * Math.Cos(ang);
            nodes[i].Y = cy + radius * Math.Sin(ang);
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++) index[nodes[i].Id] = i;

        var edges = _graph.Edges
            .Where(e => index.ContainsKey(e.FromId) && index.ContainsKey(e.ToId))
            .Select(e => (a: index[e.FromId], b: index[e.ToId]))
            .ToList();

        const int iterations = 260;
        const double kRepel = 26000, kSpring = 0.02, kCenter = 0.004, damping = 0.85;
        var vx = new double[nodes.Count];
        var vy = new double[nodes.Count];

        for (int it = 0; it < iterations; it++)
        {
            // 斥力（近似：只对邻近若干节点计算，控制 O(n^2) 规模）
            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    double dx = nodes[i].X - nodes[j].X;
                    double dy = nodes[i].Y - nodes[j].Y;
                    double d2 = dx * dx + dy * dy + 0.01;
                    if (d2 > 400 * 400) continue;
                    double f = kRepel / d2;
                    double d = Math.Sqrt(d2);
                    double ux = dx / d, uy = dy / d;
                    vx[i] += ux * f; vy[i] += uy * f;
                    vx[j] -= ux * f; vy[j] -= uy * f;
                }
            }

            // 弹簧
            foreach (var (a, b) in edges)
            {
                double dx = nodes[b].X - nodes[a].X;
                double dy = nodes[b].Y - nodes[a].Y;
                double d = Math.Sqrt(dx * dx + dy * dy) + 0.01;
                double f = kSpring * (d - 140);
                double ux = dx / d, uy = dy / d;
                vx[a] += ux * f; vy[a] += uy * f;
                vx[b] -= ux * f; vy[b] -= uy * f;
            }

            // 向心 + 阻尼 + 位移
            for (int i = 0; i < nodes.Count; i++)
            {
                vx[i] += (cx - nodes[i].X) * kCenter;
                vy[i] += (cy - nodes[i].Y) * kCenter;
                vx[i] *= damping; vy[i] *= damping;
                nodes[i].X += Math.Clamp(vx[i], -60, 60);
                nodes[i].Y += Math.Clamp(vy[i], -60, 60);
            }
        }

        // 归一化到画布留边范围
        double minX = nodes.Min(n => n.X), maxX = nodes.Max(n => n.X);
        double minY = nodes.Min(n => n.Y), maxY = nodes.Max(n => n.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        double padX = 80, padY = 80;
        double availW = w - 2 * padX, availH = h - 2 * padY;
        double scale = Math.Min(availW / spanX, availH / spanY);
        foreach (var n in nodes)
        {
            n.X = padX + (n.X - minX) * scale;
            n.Y = padY + (n.Y - minY) * scale;
        }
    }

    private void Render()
    {
        GraphCanvas.Children.Clear();
        var pos = new Dictionary<string, (double x, double y)>(StringComparer.Ordinal);
        foreach (var n in _graph.Nodes) pos[n.Id] = (n.X, n.Y);

        // 连线
        foreach (var e in _graph.Edges)
        {
            if (!pos.TryGetValue(e.FromId, out var a) || !pos.TryGetValue(e.ToId, out var b)) continue;
            var color = EdgeColor(e.Type);
            var line = new Line
            {
                X1 = a.x, Y1 = a.y, X2 = b.x, Y2 = b.y,
                Stroke = new SolidColorBrush(color) { Opacity = 0.45 },
                StrokeThickness = e.Type == "version" ? 2.2 : 1.2,
            };
            GraphCanvas.Children.Add(line);
        }

        // 节点
        foreach (var n in _graph.Nodes)
        {
            double r = 12 + Math.Min(18, n.Degree * 2.2);
            var color = KindColor(n.Kind);

            var ellipse = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = new SolidColorBrush(color),
                Stroke = Res("GraphNodeStrokeBrush", Brushes.White),
                StrokeThickness = 2,
                ToolTip = $"{n.KindLabel}：{n.Label}\n标签：{(string.IsNullOrEmpty(n.Tags) ? "无" : n.Tags)}" +
                          (string.IsNullOrEmpty(n.State) ? "" : $"\n状态：{n.State}") + $"\n连边：{n.Degree}",
                Cursor = Cursors.Hand,
                Tag = n
            };
            Canvas.SetLeft(ellipse, n.X - r);
            Canvas.SetTop(ellipse, n.Y - r);

            // 拖动微调
            bool dragging = false;
            Point dragStart = default;
            ellipse.MouseLeftButtonDown += (_, ev) =>
            {
                dragging = true;
                dragStart = ev.GetPosition(GraphCanvas);
                ellipse.CaptureMouse();
            };
            ellipse.MouseMove += (_, ev) =>
            {
                if (!dragging) return;
                var p = ev.GetPosition(GraphCanvas);
                double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
                n.X += dx; n.Y += dy;
                dragStart = p;
                Canvas.SetLeft(ellipse, n.X - r);
                Canvas.SetTop(ellipse, n.Y - r);
                // 同步标签位置
                foreach (var child in GraphCanvas.Children.OfType<TextBlock>().Where(t => ReferenceEquals(t.Tag, n)))
                {
                    Canvas.SetLeft(child, n.X - child.Width / 2);
                    Canvas.SetTop(child, n.Y + r + 2);
                }
            };
            ellipse.MouseLeftButtonUp += (_, _) => { dragging = false; ellipse.ReleaseMouseCapture(); };
            ellipse.MouseRightButtonUp += (_, _) => { _focusId = n.Id; Focus_Click(this, new RoutedEventArgs()); };

            GraphCanvas.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = Trim(n.Label, 12),
                FontSize = 11,
                Foreground = Res("TextPrimaryBrush", Brushes.Black),
                Tag = n,
                ToolTip = n.Label
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, n.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, n.Y + r + 2);
            GraphCanvas.Children.Add(label);
        }
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
