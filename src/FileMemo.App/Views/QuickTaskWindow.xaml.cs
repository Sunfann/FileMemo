using System.Windows;
using System.Windows.Input;
using FileMemo.App.Models;

namespace FileMemo.App.Views;

/// <summary>悬浮图标「待办」单击呼出的快速待办：一键落地为待办任务。</summary>
public partial class QuickTaskWindow : Window
{
    public QuickTaskWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) Add_Click(sender, e);
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
        App.Instance.Repo.UpsertTask(task);
        App.Instance.Repo.AddTimeline("task", task.Id, "created", "快速待办创建");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
