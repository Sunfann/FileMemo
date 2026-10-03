using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileMemo.App.Models;
using FileMemo.App.Services;

namespace FileMemo.App.Views;

/// <summary>悬浮图标「待办」单击呼出的快速待办：快捷预设 + 日期/时间选择，一键落地为待办任务。</summary>
public partial class QuickTaskWindow : Window
{
    public QuickTaskWindow()
    {
        InitializeComponent();

        // 时间选择器：小时 00-23，分钟 00-59
        for (int h = 0; h < 24; h++) RemindHour.Items.Add(h.ToString("00"));
        for (int m = 0; m < 60; m++) RemindMin.Items.Add(m.ToString("00"));
        RemindHour.SelectedIndex = 9;   // 默认 09 时
        RemindMin.SelectedIndex = 0;
        RemindDate.SelectedDate = null; // 默认不提醒，用户点预设或选日期才启用

        RemindDate.SelectedDateChanged += (_, _) => UpdatePreview();
        RemindHour.SelectionChanged += (_, _) => UpdatePreview();
        RemindMin.SelectionChanged += (_, _) => UpdatePreview();

        Loaded += (_, _) => { TitleBox.Focus(); RefreshPinVisual(); };
        PreviewKeyDown += OnKey;
        UpdatePreview();
        WindowPin.ApplyOnLoad(this, PinKind.QuickTask);   // 恢复上次的固定状态
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        WindowPin.Toggle(this, PinKind.QuickTask);
        RefreshPinVisual();
    }

    /// <summary>刷新图钉按钮的提示与高亮（固定时用主题色）。</summary>
    private void RefreshPinVisual()
    {
        bool pinned = WindowPin.IsPinned(this);
        PinBtn.ToolTip = pinned ? "已固定（点击取消）" : "固定窗口（置顶，不随批量收纳 / 最小化）";
        try { PinIcon.Stroke = (System.Windows.Media.Brush)FindResource(pinned ? "PrimaryBrush" : "TextSecondaryBrush"); }
        catch { }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) Add_Click(sender, e);
    }

    /// <summary>快捷预设：点一下即填好提醒时间。</summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string ?? "";
        switch (tag)
        {
            case "15": Apply(DateTime.Now.AddMinutes(15)); break;
            case "60": Apply(DateTime.Now.AddHours(1)); break;
            case "tonight": Apply(Tonight()); break;
            case "tomorrow": Apply(DateTime.Today.AddDays(1).AddHours(9)); break;
            case "tomorrownow": Apply(DateTime.Now.AddDays(1)); break;
            default: Clear(); break;   // "clear"
        }
        UpdatePreview();
    }

    private void Apply(DateTime dt)
    {
        RemindDate.SelectedDate = dt.Date;
        RemindHour.SelectedIndex = dt.Hour;
        RemindMin.SelectedIndex = dt.Minute;
    }

    private void Clear()
    {
        RemindDate.SelectedDate = null;
    }

    /// <summary>今晚 20:00；若已过则顺延到明天。（供预设与文档说明一致）</summary>
    private static DateTime Tonight()
    {
        var t = DateTime.Today.AddHours(20);
        return t <= DateTime.Now ? t.AddDays(1) : t;
    }

    /// <summary>从三个控件读出提醒时间；未选日期则返回 null（表示不提醒）。</summary>
    private DateTime? ReadRemind()
    {
        if (RemindDate.SelectedDate is not DateTime d) return null;
        int h = RemindHour.SelectedIndex >= 0 ? RemindHour.SelectedIndex : 0;
        int m = RemindMin.SelectedIndex >= 0 ? RemindMin.SelectedIndex : 0;
        return d.Date.AddHours(h).AddMinutes(m);
    }

    private void UpdatePreview()
    {
        if (RemindPreview is null) return;
        var dt = ReadRemind();
        RemindPreview.Text = dt is DateTime v
            ? "将于 " + v.ToString("yyyy-MM-dd HH:mm") + " 提醒"
            : "未设置提醒";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { Close(); return; }

        var task = new TaskItem
        {
            Title = title,
            State = TaskState.NotStarted,
            Done = false
        };

        // 可选提醒时间（由快捷预设或选择器得出）
        if (ReadRemind() is DateTime dt) task.RemindAt = dt;

        App.Instance.Repo.UpsertTask(task);
        App.Instance.Repo.AddTimeline("task", task.Id, "created", "快速待办创建");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
