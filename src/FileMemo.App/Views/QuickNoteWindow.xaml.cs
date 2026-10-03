using System.Windows;
using System.Windows.Input;
using FileMemo.App.Models;
using FileMemo.App.Services;

namespace FileMemo.App.Views;

/// <summary>全局快捷键呼出的快速便签，一键落地为记录。</summary>
public partial class QuickNoteWindow : Window
{
    public QuickNoteWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { ContentBox.Focus(); RefreshPinVisual(); };
        PreviewKeyDown += OnKey;
        WindowPin.ApplyOnLoad(this, PinKind.QuickNote);   // 恢复上次的固定状态
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        WindowPin.Toggle(this, PinKind.QuickNote);
        RefreshPinVisual();
    }

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
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) Save_Click(sender, e);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = ContentBox.Text.Trim();
        if (text.Length == 0) { Close(); return; }

        var firstLine = text.Split('\n')[0];
        var note = new NoteRecord
        {
            Title = firstLine.Length > 40 ? firstLine[..40] : firstLine,
            ContentMd = text,
            Kind = RecordKind.Note,
            Tags = "临时"
        };
        App.Instance.Repo.UpsertNote(note);
        App.Instance.Repo.AddTimeline("record", note.Id, "created", "快速便签创建");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
