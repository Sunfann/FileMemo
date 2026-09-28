using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 插件接口（需求 7.P2「插件系统」）。
/// 第三方插件编译为独立 DLL，放进插件目录即被 <see cref="PluginHost"/> 发现并加载。
/// </summary>
public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    void Initialize(PluginContext context);
    void Shutdown();
}

/// <summary>插件运行上下文：只暴露受控能力，避免插件直接触碰应用内部。</summary>
public sealed class PluginContext
{
    public Repository Repo { get; }
    public SettingsService Settings { get; }
    public Action<string> Log { get; }
    /// <summary>请求主程序执行一个命名动作（由宿主注册），返回是否处理成功。</summary>
    public Func<string, string?, bool> Command { get; }

    public PluginContext(Repository repo, SettingsService settings, Action<string> log, Func<string, string?, bool> command)
    {
        Repo = repo;
        Settings = settings;
        Log = log;
        Command = command;
    }
}

/// <summary>已加载插件的运行时句柄。</summary>
public sealed class LoadedPlugin
{
    public PluginInfo Info { get; set; } = new();
    public IPlugin? Instance { get; set; }
    public AssemblyLoadContext? Context { get; set; }
}

/// <summary>
/// 插件宿主（P2）：扫描插件目录 → 用独立 AssemblyLoadContext 加载 → 发现 IPlugin 实现 →
/// 初始化并登记到数据库。单个插件失败不影响其它插件与主程序。
/// </summary>
public sealed class PluginHost
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;
    private readonly Action<string> _log;
    private readonly Func<string, string?, bool> _command;
    private readonly List<LoadedPlugin> _loaded = new();

    public PluginHost(Repository repo, SettingsService settings, Action<string>? log = null,
        Func<string, string?, bool>? command = null)
    {
        _repo = repo;
        _settings = settings;
        _log = log ?? (_ => { });
        _command = command ?? ((_, _) => false);
    }

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    /// <summary>加载插件目录下的全部插件，返回成功加载数量。</summary>
    public int LoadAll()
    {
        var dir = _settings.ResolvedPluginDir;
        int ok = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;

            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                try
                {
                    if (LoadAssembly(dll)) ok++;
                }
                catch (Exception ex)
                {
                    _repo.UpsertPlugin(new PluginInfo
                    {
                        Id = Path.GetFileNameWithoutExtension(dll),
                        Name = Path.GetFileName(dll),
                        AssemblyPath = dll,
                        State = PluginState.Failed,
                        Enabled = false,
                        Error = ex.Message
                    });
                    _log($"[Plugin] 加载失败 {dll}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { _log("[Plugin] 扫描目录失败: " + ex.Message); }
        return ok;
    }

    private bool LoadAssembly(string dllPath)
    {
        var alc = new PluginAssemblyLoadContext(dllPath);
        var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        bool any = false;
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IPlugin).IsAssignableFrom(type)) continue;

            var info = new PluginInfo
            {
                AssemblyPath = dllPath,
                TypeName = type.FullName ?? type.Name,
            };
            try
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                info.Id = plugin.Id;
                info.Name = plugin.Name;
                info.Version = plugin.Version;
                info.Description = plugin.Description;

                var ctx = new PluginContext(_repo, _settings, m => _log($"[{plugin.Name}] {m}"), _command);
                plugin.Initialize(ctx);

                info.State = PluginState.Loaded;
                info.Enabled = true;
                _loaded.Add(new LoadedPlugin { Info = info, Instance = plugin, Context = alc });
                _repo.UpsertPlugin(info);
                _log($"[Plugin] 已加载 {plugin.Name} {plugin.Version}");
                any = true;
            }
            catch (Exception ex)
            {
                info.State = PluginState.Failed;
                info.Error = ex.Message;
                _repo.UpsertPlugin(info);
                _log($"[Plugin] 初始化失败 {type.FullName}: {ex.Message}");
            }
        }
        return any;
    }

    /// <summary>禁用（不在本次会话加载）某插件：登记状态，不加载。</summary>
    public void Disable(string pluginId)
    {
        try
        {
            var p = _repo.GetPlugins().FirstOrDefault(x => x.Id == pluginId);
            if (p == null) return;
            p.Enabled = false;
            p.State = PluginState.Disabled;
            _repo.UpsertPlugin(p);
            _repo.AddTimeline("system", pluginId, "plugin", "禁用插件：" + p.Name);
        }
        catch { }
    }

    public void Shutdown()
    {
        foreach (var lp in _loaded)
        {
            try { lp.Instance?.Shutdown(); } catch { }
            try { lp.Context?.Unload(); } catch { }
        }
        _loaded.Clear();
    }

    /// <summary>隔离的加载上下文：插件依赖与主程序依赖互不干扰。</summary>
    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginAssemblyLoadContext(string mainAssemblyPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 优先从插件自身目录解析依赖；主程序已加载的程序集回退到默认上下文
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }
}
