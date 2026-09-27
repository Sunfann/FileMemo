using System.Windows;
using System.Windows.Input;
using SuperNote.App.Services;

namespace SuperNote.App.Views;

/// <summary>桌面 Widget：快速便签、今日待办、最近剪贴板、文件备注悬浮卡。</summary>
public partial class WidgetWindow : Window
{
    public WidgetWindow()
    {
        InitializeComponent();
        // 默认贴在屏幕右上角
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 24;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var repo = App.Instance.Repo;
        TaskList.ItemsSource = repo.GetTasks().Where(t => !t.Done).Take(8).ToList();
        var clips = repo.GetClips(null, 6);
        foreach (var c in clips) c.Content = CryptoService.Decrypt(c.Content, true);
        ClipList.ItemsSource = clips;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 支持整窗拖动（贴边逻辑可按需扩展）
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void QuickNote_Click(object sender, RoutedEventArgs e) => App.Instance.ShowQuickNote();
    private void Clipboard_Click(object sender, RoutedEventArgs e) => App.Instance.ShowClipboardPanel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
