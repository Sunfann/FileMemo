using System.Collections.ObjectModel;
using System.Windows;
using SuperNote.App.Data;
using SuperNote.App.Models;
using SuperNote.App.Services;

namespace SuperNote.App.ViewModels;

// 说明：便签与文件/文件夹备注已合并为统一「记录」。
// 左侧只保留一个「便签 / 文件备注」分区，中间为统一列表，右侧为统一编辑器。
public enum NavSection { Notes, Clips, Tasks, FileTree, Search, Recycle, Settings }

/// <summary>主窗口视图模型：左导航 + 中间列表 + 右侧详情。</summary>
public sealed class MainViewModel : ObservableEntity
{
    private readonly Repository _repo;

    public MainViewModel(Repository repo)
    {
        _repo = repo;

        NewNoteCommand = new RelayCommand(NewNote);
        SaveNoteCommand = new RelayCommand(SaveNote);
        DeleteNoteCommand = new RelayCommand(DeleteNote);
        NewTaskCommand = new RelayCommand(NewTask);
        SaveTaskCommand = new RelayCommand(SaveTask);
        ConvertClipToNoteCommand = new RelayCommand(ConvertClipToNote);
        PinClipCommand = new RelayCommand(PinClip);
        DeleteClipCommand = new RelayCommand(DeleteClip);
        AddAnnotationCommand = new RelayCommand(AddAnnotationFromDialog);
        RefreshCommand = new RelayCommand(Refresh);
        RunSearchCommand = new RelayCommand(RunSearch);

        Refresh();
    }

    // ---------------- 导航 ----------------
    private NavSection _section = NavSection.Notes;
    public NavSection Section
    {
        get => _section;
        set { if (Set(ref _section, value)) { OnSectionChanged(); Raise(nameof(Title)); } }
    }

    public string Title => _section switch
    {
        NavSection.Notes => "便签 / 文件备注",
        NavSection.Clips => "剪贴板随记",
        NavSection.Tasks => "待办事项",
        NavSection.FileTree => "文件树",
        NavSection.Search => "搜索",
        NavSection.Recycle => "回收站",
        NavSection.Settings => "设置",
        _ => "超级便签"
    };

    // ---------------- 集合 ----------------
    /// <summary>统一记录列表：普通便签 + 文件/文件夹备注混排。</summary>
    public ObservableCollection<RecordRow> Records { get; } = new();
    public ObservableCollection<Clip> Clips { get; } = new();
    public ObservableCollection<TaskItem> Tasks { get; } = new();
    public ObservableCollection<FileNode> FileTree { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    // ---------------- 选中项 ----------------
    private RecordRow? _selectedRecord;
    public RecordRow? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (Set(ref _selectedRecord, value))
            {
                if (value != null)
                {
                    SelectedClip = null;
                    SelectedTask = null;
                }
                // 关键：通知右侧编辑器切换到当前记录的模型
                Raise(nameof(SelectedNote));
                Raise(nameof(SelectedAnnotation));
            }
        }
    }

    /// <summary>
    /// 当前记录若为便签，返回其 NoteRecord，供右侧便签编辑器双向绑定。
    /// 保留 setter：既满足 WPF 双向绑定对可写性的要求，也避免运行时绑定异常。
    /// </summary>
    public NoteRecord? SelectedNote
    {
        get => SelectedRecord is { IsFile: false } r ? r.Note : null;
        set { /* 便签编辑通过 SelectedRecord.Note 的属性双向绑定，无需在此赋值 */ }
    }

    /// <summary>当前记录若为文件/文件夹备注，返回其标注模型，供右侧备注编辑器使用。</summary>
    public Annotation? SelectedAnnotation => SelectedRecord is { IsFile: true } r ? r.Annotation : null;

    private Clip? _selectedClip;
    public Clip? SelectedClip { get => _selectedClip; set { if (Set(ref _selectedClip, value) && value != null) SelectedRecord = null; } }

    private TaskItem? _selectedTask;
    public TaskItem? SelectedTask { get => _selectedTask; set { if (Set(ref _selectedTask, value) && value != null) SelectedRecord = null; } }

    // ---------------- 搜索 / 编辑器 ----------------
    private string _searchText = "";
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string[] States => Models.StateDefaults.States;
    public string[] DefaultTags => Models.StateDefaults.Tags;

    // ---------------- 命令 ----------------
    public RelayCommand NewNoteCommand { get; }
    public RelayCommand SaveNoteCommand { get; }
    public RelayCommand DeleteNoteCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand SaveTaskCommand { get; }
    public RelayCommand ConvertClipToNoteCommand { get; }
    public RelayCommand PinClipCommand { get; }
    public RelayCommand DeleteClipCommand { get; }
    public RelayCommand AddAnnotationCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RunSearchCommand { get; }

    private void OnSectionChanged()
    {
        switch (_section)
        {
            case NavSection.Notes: LoadRecords(); break;
            case NavSection.Clips: LoadClips(); break;
            case NavSection.Tasks: LoadTasks(); break;
            case NavSection.FileTree: LoadFileTree(); break;
        }
    }

    public void Refresh()
    {
        LoadRecords(); LoadClips(); LoadTasks(); LoadFileTree();
    }

    /// <summary>供右侧编辑器保存后刷新统一列表。</summary>
    public void ReloadRecords() => LoadRecords();

    /// <summary>统一记录列表：便签（note_record）+ 文件/文件夹备注（annotation）。</summary>
    private void LoadRecords()
    {
        var q = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
        var buffer = new List<RecordRow>();

        foreach (var n in _repo.GetNotes(q))
            buffer.Add(new RecordRow { IsFile = false, Note = n });

        foreach (var a in _repo.GetAllAnnotations())
        {
            var fr = _repo.GetFileRefById(a.FileRefId);
            buffer.Add(new RecordRow { IsFile = true, Annotation = a, FileRef = fr });
        }

        Records.Clear();
        // 便签与文件备注按更新时间倒序混排，形成统一的"记录"时间流
        foreach (var r in buffer.OrderByDescending(r => r.UpdatedAt))
            Records.Add(r);
    }

    private void LoadClips()
    {
        Clips.Clear();
        foreach (var c in _repo.GetClips(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText, 500))
        {
            // 解密用于展示
            c.Content = CryptoService.Decrypt(c.Content, true);
            Clips.Add(c);
        }
    }

    private void LoadTasks()
    {
        Tasks.Clear();
        foreach (var t in _repo.GetTasks()) Tasks.Add(t);
    }

    private void LoadFileTree()
    {
        FileTree.Clear();
        // 根：按已备注文件所在目录聚合（文件树 ≠ 资源管理器，仅展示备注覆盖情况）
        var groups = _repo.GetAllAnnotations()
            .Select(a => (a, fr: _repo.GetFileRefById(a.FileRefId)))
            .Where(x => x.fr != null)
            .GroupBy(x => x.fr!.IsDir ? x.fr!.Path : System.IO.Path.GetDirectoryName(x.fr!.Path) ?? x.fr!.Path);

        foreach (var g in groups)
        {
            var root = new FileNode { Name = g.Key, IsDir = true, Path = g.Key };
            foreach (var (a, fr) in g)
            {
                var node = new FileNode
                {
                    Name = fr!.Name,
                    IsDir = fr.IsDir,
                    Path = fr.Path,
                    HasAnnotation = a != null,
                    StateLabel = a?.StateLabel ?? "",
                    Tags = a?.Tags ?? "",
                    TaskCount = _repo.GetTasks(fr.Id).Count(t => !t.Done)
                };
                root.Children.Add(node);
            }
            FileTree.Add(root);
        }
    }

    // ---------------- 动作 ----------------
    private void NewNote()
    {
        var n = new NoteRecord { Title = "新便签", Kind = RecordKind.Note };
        _repo.UpsertNote(n);
        Section = NavSection.Notes;
        SelectedRecord = new RecordRow { IsFile = false, Note = n };
        LoadRecords();
        StatusText = "已创建便签";
    }

    private void SaveNote()
    {
        var n = SelectedNote;
        if (n == null) return;
        _repo.UpsertNote(n);
        MarkdownService.SyncBacklinks(_repo, n.Id, n.ContentMd);
        _repo.AddTimeline("record", n.Id, "edited", "便签已保存");
        StatusText = $"已保存：{n.Title}";
        LoadRecords();
    }

    private void DeleteNote()
    {
        var n = SelectedNote;
        if (n == null) return;
        _repo.DeleteNote(n.Id);
        SelectedRecord = null;
        LoadRecords();
        StatusText = "便签已移入回收站";
    }

    private void NewTask()
    {
        var t = new TaskItem { Title = "新待办" };
        _repo.UpsertTask(t);
        Tasks.Insert(0, t);
        SelectedTask = t;
        Section = NavSection.Tasks;
        StatusText = "已创建待办";
    }

    private void SaveTask()
    {
        if (SelectedTask == null) return;
        _repo.UpsertTask(SelectedTask);
        _repo.AddTimeline("task", SelectedTask.Id, "edited", "待办已保存");
        StatusText = $"已保存：{SelectedTask.Title}";
    }

    private void ConvertClipToNote()
    {
        if (SelectedClip == null) return;
        var n = new NoteRecord
        {
            Title = "来自剪贴板 " + DateTime.Now.ToString("HH:mm"),
            ContentMd = SelectedClip.Content,
            Kind = RecordKind.Note,
            Tags = "临时"
        };
        _repo.UpsertNote(n);
        LoadRecords();
        StatusText = "剪贴板已转为便签";
    }

    private void PinClip()
    {
        if (SelectedClip == null) return;
        SelectedClip.Pinned = !SelectedClip.Pinned;
        _repo.SetClipPinned(SelectedClip.Id, SelectedClip.Pinned);
        LoadClips();
    }

    private void DeleteClip()
    {
        if (SelectedClip == null) return;
        _repo.DeleteClip(SelectedClip.Id);
        Clips.Remove(SelectedClip);
        SelectedClip = null;
    }

    private void AddAnnotationFromDialog()
    {
        var dlg = new Views.AddAnnotationDialog();
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FilePath))
        {
            var created = Views.AnnotationHelper.CreateOrGet(_repo, dlg.FilePath);
            Section = NavSection.Notes;
            LoadRecords();
            SelectedRecord = Records.FirstOrDefault(r => r.IsFile && r.Annotation?.Id == created.Id);
            StatusText = "已添加文件备注：" + dlg.FilePath;
        }
    }

    private void RunSearch()
    {
        SearchResults.Clear();
        var q = SearchText;
        if (string.IsNullOrWhiteSpace(q)) return;

        // 联合搜索：文件（Everything/内置索引）+ 备注关键词 + 剪贴板 + 待办
        var app = App.Instance;
        IEnumerable<string> files = app.Everything.Available
            ? app.Everything.SearchFiles(q)
            : _repo.SearchFileRefs(q).Select(f => f.Path);

        foreach (var f in files.Take(100))
        {
            var fr = _repo.GetFileRefByPath(f);
            var ann = fr != null ? _repo.GetAnnotation(fr.Id) : null;
            SearchResults.Add(new SearchResult
            {
                Kind = "文件",
                Title = System.IO.Path.GetFileName(f),
                Path = f,
                Summary = ann?.Content ?? "",
                Tags = ann?.Tags ?? "",
                State = ann?.StateLabel ?? ""
            });
        }
        foreach (var n in _repo.GetNotes(q))
            SearchResults.Add(new SearchResult { Kind = "便签", Title = n.Title, Summary = n.ContentMd, Tags = n.Tags });
        foreach (var c in _repo.GetClips(q))
            SearchResults.Add(new SearchResult { Kind = "剪贴板", Title = CryptoService.Decrypt(c.Content, true).Replace("\n", " "), Summary = c.SourceApp });
        foreach (var t in _repo.GetTasks().Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)))
            SearchResults.Add(new SearchResult { Kind = "待办", Title = t.Title, Summary = t.StateLabel });

        Section = NavSection.Search;
        StatusText = $"搜索 \"{q}\"：{SearchResults.Count} 条结果";
    }
}

/// <summary>
/// 统一记录行：便签与文件/文件夹备注共用同一列表与编辑器入口。
/// IsFile=false 时使用 Note；IsFile=true 时使用 Annotation + FileRef。
/// </summary>
public sealed class RecordRow
{
    public bool IsFile { get; set; }
    public NoteRecord? Note { get; set; }
    public Annotation? Annotation { get; set; }
    public FileRef? FileRef { get; set; }

    public string Badge => IsFile ? "📄 文件/文件夹" : "📝 便签";
    public string Title => IsFile ? (FileRef?.Name ?? "(未知对象)") : (Note?.Title ?? "(无标题)");
    public string Subtitle => IsFile ? (FileRef?.Path ?? "") : (Note?.ContentMd ?? "");
    public string StateLabel => IsFile ? (Annotation?.StateLabel ?? "") : "";
    public DateTime UpdatedAt => IsFile
        ? (Annotation?.UpdatedAt ?? DateTime.Now)
        : (Note?.UpdatedAt ?? DateTime.Now);

    /// <summary>有状态标签时用于控制 Pill 显示。</summary>
    public bool HasState => !string.IsNullOrWhiteSpace(StateLabel);
}

public sealed class SearchResult
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Tags { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>文件树节点。</summary>
public sealed class FileNode : ObservableEntity
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public bool HasAnnotation { get; set; }
    public string StateLabel { get; set; } = "";
    public string Tags { get; set; } = "";
    public int TaskCount { get; set; }
    public string TaskBadge => TaskCount > 0 ? $"☑ {TaskCount}" : "";
    public string Icon => IsDir ? "📁" : "📄";
    public System.Collections.ObjectModel.ObservableCollection<FileNode> Children { get; } = new();
}
