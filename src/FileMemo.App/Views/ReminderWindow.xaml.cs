using System.Windows;
using FileMemo.App.Models;

namespace FileMemo.App.Views;

/// <summary>
/// 待办到点提醒弹窗。操作：稍后提醒（+10 分钟并重置已提醒标记）/ 标记完成 / 知道了。
/// 所有写库操作均容错，避免提醒窗口本身出错影响主程序。
/// </summary>
public partial class ReminderWindow : Window
{
    private readonly TaskItem _task;

    public ReminderWindow(TaskItem task)
    {
        InitializeComponent();
        _task = task;
        TitleText.Text = string.IsNullOrWhiteSpace(task.Title) ? "(无标题待办)" : task.Title;
        TimeText.Text = "提醒时间：" + (task.RemindAt?.ToString("yyyy-MM-dd HH:mm") ?? "");
        // 「稍后提醒」按钮受设置控制
        try { SnoozeBtn.Visibility = App.Instance.Settings.ReminderSnoozeEnabled ? Visibility.Visible : Visibility.Collapsed; }
        catch { }
        Loaded += (_, _) => Activate();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _task.RemindAt = DateTime.Now.AddMinutes(10);
            _task.Reminded = false;
            App.Instance.Repo.UpsertTask(_task);
            App.Instance.Repo.AddTimeline("task", _task.Id, "snooze", "稍后提醒 10 分钟");
        }
        catch { }
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _task.Done = true;
            App.Instance.Repo.UpsertTask(_task);
        }
        catch { }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
