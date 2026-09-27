using System.Windows;
using SuperNote.App.Models;
using SuperNote.App.Services;

namespace SuperNote.App.Views;

/// <summary>剪贴板快捷面板（Alt+V 风格）：搜索、固定、转便签、删除。</summary>
public partial class ClipboardPanelWindow : Window
{
    public ClipboardPanelWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { Reload(); SearchBox.Focus(); };
    }

    private void Reload(string? search = null)
    {
        var list = App.Instance.Repo.GetClips(search, 300);
        foreach (var c in list) c.Content = CryptoService.Decrypt(c.Content, true);
        ClipList.ItemsSource = list;
    }

    private Models.Clip? Selected => ClipList.SelectedItem as Models.Clip;

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Reload(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text);

    private void ToNote_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var note = new NoteRecord
        {
            Title = "来自剪贴板 " + DateTime.Now.ToString("HH:mm"),
            ContentMd = Selected.Content,
            Kind = RecordKind.Note,
            Tags = "临时"
        };
        App.Instance.Repo.UpsertNote(note);
        MessageBox.Show("已转为便签。", "超级便签");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        Selected.Pinned = !Selected.Pinned;
        App.Instance.Repo.SetClipPinned(Selected.Id, Selected.Pinned);
        Reload(SearchBox.Text);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        App.Instance.Repo.DeleteClip(Selected.Id);
        Reload(SearchBox.Text);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        try { System.Windows.Clipboard.SetText(Selected.Content); }
        catch { }
    }
}
