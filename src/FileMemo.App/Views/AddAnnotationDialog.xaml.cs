using System.Windows;

namespace FileMemo.App.Views;

public partial class AddAnnotationDialog : Window
{
    public string FilePath => PathBox.Text.Trim();

    public AddAnnotationDialog()
    {
        InitializeComponent();
        PathBox.Focus();
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要添加备注的文件" };
        if (dlg.ShowDialog() == true) PathBox.Text = dlg.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加备注的文件夹" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) PathBox.Text = dlg.SelectedPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            MessageBox.Show("请输入或选择一个文件 / 文件夹路径。", "文笺 FileMemo");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
