using System.Windows.Threading;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 待办提醒服务：按固定间隔轮询「已到期且未完成」的待办，触发 <see cref="TaskDue"/> 事件。
/// 由 App 订阅该事件后弹出托盘气泡 + 提醒窗口。使用 DispatcherTimer 保证回调在 UI 线程。
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly Repository _repo;
    private readonly SettingsService _settings;
    private DispatcherTimer? _timer;

    /// <summary>有待办到点需要提醒（已在 UI 线程触发）。</summary>
    public event Action<TaskItem>? TaskDue;

    public ReminderService(Repository repo, SettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    public bool IsRunning => _timer != null;

    /// <summary>启动轮询（设置变更后可直接再次调用以重启）。</summary>
    public void Start()
    {
        Stop();
        if (!_settings.ReminderEnabled) return;

        var sec = Math.Clamp(_settings.ReminderCheckSeconds, 10, 600);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        Check();   // 启动即检查一次：补发应用未运行期间已到点的提醒
    }

    public void Restart() => Start();

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>立即检查一次所有已到期的提醒。</summary>
    public void Check()
    {
        try
        {
            var advance = Math.Max(0, _settings.ReminderAdvanceMinutes);
            var now = DateTime.Now.AddMinutes(advance);
            var due = _repo.GetTasksDueForReminder(now);
            foreach (var t in due)
            {
                _repo.MarkTaskReminded(t.Id);
                _repo.AddTimeline("task", t.Id, "reminded", "待办提醒触发");
                TaskDue?.Invoke(t);
            }
        }
        catch { /* 提醒失败不得影响主程序 */ }
    }

    public void Dispose() => Stop();
}
