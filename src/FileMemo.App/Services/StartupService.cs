using System.IO;
using Microsoft.Win32;

namespace FileMemo.App.Services;

/// <summary>
/// 开机自启管理（需求“登录 Windows 后自动运行并常驻托盘”）。
///
/// 实现方式：写入 / 删除当前用户注册表项
///   HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
/// 使用 HKCU 而非 HKLM，因此**无需管理员权限**；注销键即关闭自启。
///
/// 启动命令携带 <c>--autostart</c> 参数，供 <see cref="App"/> 判定“本次为开机拉起”，
/// 从而不弹出主窗口、仅常驻托盘与悬浮球。
///
/// 稳定性约定：所有注册表操作都 try/catch 兜底，失败不抛异常（返回 false），
/// 由调用方回滚 UI/设置，避免出现“界面显示已开启、系统实际未生效”的不一致。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileMemo";

    /// <summary>开机自启启动命令中携带的参数，用于进入“静默常驻”模式。</summary>
    public const string AutostartArg = "--autostart";

    /// <summary>注册表中的当前命令（未注册时为 null）。</summary>
    public string? RegisteredCommand { get; private set; }

    /// <summary>是否已在注册表中登记开机自启。</summary>
    public bool IsEnabled { get; private set; }

    public StartupService()
    {
        Refresh();
    }

    /// <summary>从注册表重新读取当前状态。</summary>
    public void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            RegisteredCommand = key?.GetValue(ValueName) as string;
            IsEnabled = !string.IsNullOrWhiteSpace(RegisteredCommand);
        }
        catch
        {
            RegisteredCommand = null;
            IsEnabled = false;
        }
    }

    /// <summary>按设置应用开关：true=启用（写入 Run 项），false=关闭（删除 Run 项）。</summary>
    public bool Apply(bool enabled) => enabled ? Enable() : Disable();

    /// <summary>写入 Run 项，指向当前可执行文件；成功返回 true。</summary>
    public bool Enable()
    {
        try
        {
            var exe = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exe)) return false;

            // 命令形如： "C:\...\FileMemo.exe" --autostart
            var command = $"\"{exe}\" {AutostartArg}";

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            key.SetValue(ValueName, command, RegistryValueKind.String);
            RegisteredCommand = command;
            IsEnabled = true;
            return true;
        }
        catch
        {
            Refresh();
            return false;
        }
    }

    /// <summary>删除 Run 项以关闭自启；成功返回 true（本就不存在也视为成功）。</summary>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            RegisteredCommand = null;
            IsEnabled = false;
            return true;
        }
        catch
        {
            Refresh();
            return false;
        }
    }

    /// <summary>
    /// 自愈：已启用但注册命令未指向当前可执行文件（如程序被移动 / 升级新版目录）时，
    /// 自动重写为当前路径。未启用时不产生任何写入。
    /// </summary>
    public void SyncIfEnabled()
    {
        if (!IsEnabled) return;
        try
        {
            var exe = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exe)) return;
            var expected = $"\"{exe}\" {AutostartArg}";
            if (!string.Equals(RegisteredCommand, expected, StringComparison.OrdinalIgnoreCase))
                Enable();
        }
        catch { /* 自愈失败忽略 */ }
    }

    /// <summary>解析当前进程可执行文件路径；失败返回 null。</summary>
    private static string? ResolveExecutablePath()
    {
        try
        {
            // 发布部署后即为 FileMemo.exe；由 dotnet 宿主调试运行时为主机进程，属预期行为。
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }
        catch { }

        try
        {
            var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(loc) && File.Exists(loc))
                return loc;
        }
        catch { }

        return null;
    }
}
