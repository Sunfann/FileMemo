using SuperNote.App.Data;
using SuperNote.App.Models;

namespace SuperNote.App.Services;

/// <summary>
/// 协作提供方抽象（需求 3.9 / 7.P2「团队协作预留」）。
/// 后续接入真实后端（自建服务 / WebDAV / 云盘）时，只需实现本接口，
/// 无需改动上层 <see cref="CollaborationService"/> 与数据模型。
/// </summary>
public interface ICollaborationProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<(bool ok, string message)> TestAsync();
    Task<(bool ok, string message)> PublishAsync(CollaborationSession session);
    Task<(bool ok, string message)> PullAsync(CollaborationSession session);
}

/// <summary>默认占位实现：不连任何远端，仅本地可用。</summary>
public sealed class LocalOnlyProvider : ICollaborationProvider
{
    public string Name => "local";
    public bool IsConfigured => false;

    public Task<(bool ok, string message)> TestAsync() =>
        Task.FromResult((true, "本地模式：未配置远端协作服务"));

    public Task<(bool ok, string message)> PublishAsync(CollaborationSession session) =>
        Task.FromResult((false, "本地模式不支持发布，请实现 ICollaborationProvider 后启用"));

    public Task<(bool ok, string message)> PullAsync(CollaborationSession session) =>
        Task.FromResult((false, "本地模式不支持拉取"));
}

/// <summary>
/// 团队协作服务（P2 预留）：管理工作区 / 会话 / 成员元数据，
/// 并把发布 / 拉取委托给当前 <see cref="ICollaborationProvider"/>。
/// </summary>
public sealed class CollaborationService
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;

    public CollaborationService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    public ICollaborationProvider Provider { get; private set; } = new LocalOnlyProvider();

    public bool Enabled => _settings.CollaborationEnabled;

    public List<CollaborationSession> Sessions
    {
        get { try { return _repo.GetCollabSessions(); } catch { return new(); } }
    }

    /// <summary>注入自定义协作提供方（插件 / 未来后端使用）。</summary>
    public void UseProvider(ICollaborationProvider provider) => Provider = provider;

    public CollaborationSession CreateSession(string name, string owner = "我")
    {
        var s = new CollaborationSession
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名工作区" : name,
            WorkspaceId = Guid.NewGuid().ToString("N"),
            Owner = owner,
            Provider = Provider.Name,
            SyncState = "已创建（未同步）",
            Members = new List<CollaborationMember>
            {
                new() { UserId = owner, DisplayName = owner, Role = CollaborationRole.Owner }
            }
        };
        _repo.UpsertCollabSession(s);
        return s;
    }

    public void AddMember(CollaborationSession s, CollaborationMember m)
    {
        if (s.Members.All(x => x.UserId != m.UserId)) s.Members.Add(m);
        s.UpdatedAt = DateTime.Now;
        _repo.UpsertCollabSession(s);
    }

    public void RemoveMember(CollaborationSession s, string userId)
    {
        s.Members.RemoveAll(x => x.UserId == userId);
        s.UpdatedAt = DateTime.Now;
        _repo.UpsertCollabSession(s);
    }

    public async Task<(bool ok, string message)> PublishAsync(CollaborationSession s)
    {
        var (ok, msg) = await Provider.PublishAsync(s);
        s.SyncState = ok ? "已发布 " + DateTime.Now.ToString("MM-dd HH:mm") : "发布失败";
        s.UpdatedAt = DateTime.Now;
        _repo.UpsertCollabSession(s);
        return (ok, msg);
    }

    public async Task<(bool ok, string message)> PullAsync(CollaborationSession s)
    {
        var (ok, msg) = await Provider.PullAsync(s);
        s.SyncState = ok ? "已拉取 " + DateTime.Now.ToString("MM-dd HH:mm") : "拉取失败";
        s.UpdatedAt = DateTime.Now;
        _repo.UpsertCollabSession(s);
        return (ok, msg);
    }

    public void Delete(string id) => _repo.DeleteCollabSession(id);
}
