using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 应用设置。持久化到 %APPDATA%\SuperNote\settings.json。
/// 覆盖需求中的可配置项：剪贴板保留条数/敏感过滤、追踪范围、Everything 路径、热键、分级加密、
/// 以及 P1 新增的 USN / 哈希兜底 / 网络盘 / sidecar / OCR / 云同步 / 时间线 / 副本继承。
/// </summary>
public sealed class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperNote");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    // ---- 通用 ----
    public bool FirstRun { get; set; } = true;
    public string Theme { get; set; } = "System";           // System / Light / Dark
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    // ---- 剪贴板 ----
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardRetention { get; set; } = 500;       // 100~10000
    public bool ClipboardEncrypt { get; set; } = true;       // 敏感数据强制加密
    public List<string> ClipboardExcludedApps { get; set; } = new()
    {
        "KeePass", "1Password", "Bitwarden", "Keeper", "BankOfChina", "ICBC"
    };

    // ---- 文件追踪 ----
    public bool FullDiskTracking { get; set; } = false;
    public bool WatchUsnJournal { get; set; } = true;              // P1：USN Journal 实时订阅
    public bool HashFallbackEnabled { get; set; } = true;          // P1：哈希兜底
    public bool CrossVolumeConfirmEnabled { get; set; } = true;    // P1：跨卷迁移交互式确认
    public bool NetworkPathIndexEnabled { get; set; } = true;      // P1：网络盘 / NAS 内置索引
    public List<string> TrackedRoots { get; set; } = new();
    public int MaxTrackedFiles { get; set; } = 200_000;
    public string InheritPolicyOnCopy { get; set; } = "Ask";  // 兼容旧字段，见 InheritDefault
    public InheritPolicy InheritDefault { get; set; } = InheritPolicy.Ask;   // 需求 3.4.6

    // ---- 搜索 ----
    public string EverythingPath { get; set; } = "";          // es.exe 路径，留空自动探测
    public bool UseEverything { get; set; } = true;
    public bool OcrEnabled { get; set; } = false;             // P1：图片 OCR 参与搜索
    public string OcrLanguage { get; set; } = "zh-Hans-CN";   // Windows.Media.Ocr 语言标签

    // ---- 快捷键 ----
    public string HotkeyQuickNote { get; set; } = "Ctrl+Alt+N";
    public string HotkeyClipboard { get; set; } = "Ctrl+Alt+V";
    public string HotkeyAddAnnotation { get; set; } = "Ctrl+Alt+A";

    // ---- 存储 / sidecar（需求 3.12，P1）----
    public bool SidecarEnabled { get; set; } = false;         // 检测到移动盘 / NAS 时自动生成
    public bool SidecarHidden { get; set; } = true;
    public SidecarFormat SidecarFormat { get; set; } = SidecarFormat.Json;
    public SidecarPlacement SidecarPlacement { get; set; } = SidecarPlacement.SameDirectory;
    public string SidecarCentralDir { get; set; } = "";       // 集中目录，留空用 %APPDATA%\SuperNote\sidecars
    public string SidecarSuffix { get; set; } = ".supernote"; // 同目录命名后缀

    // ---- 云同步（需求 3.12，P1 预留）----
    public bool CloudSyncEnabled { get; set; } = false;
    public CloudProvider CloudProvider { get; set; } = CloudProvider.None;
    public string CloudSyncEndpoint { get; set; } = "";       // WebDAV URL 或 OneDrive 账户标识
    public string CloudSyncUser { get; set; } = "";
    [JsonIgnore]
    public string CloudSyncPassword { get; set; } = "";       // 运行时解密后使用，不落明文 JSON
    public string CloudSyncPasswordEncrypted { get; set; } = "";
    public bool CloudSyncEncrypt { get; set; } = true;        // 端到端加密（可选）

    // ---- 时间线（需求 3.11，可配置）----
    public List<string> TimelineEnabledCategories { get; set; } =
        new(TimelineCategories.DefaultEnabled);
    public int TimelineMaxEntries { get; set; } = 500;

    // ---- 隐私 ----
    public bool AppLockEnabled { get; set; } = false;
    public bool EncryptAnnotations { get; set; } = true;      // 文件路径/备注强制加密

    // ---- P2：关系图谱 / 知识网络 ----
    public bool GraphEnabled { get; set; } = true;
    public int GraphMaxNodes { get; set; } = 400;             // 视图节点上限（性能保护）

    // ---- P2：图片向量搜索（纯本地特征向量，不引入 AI）----
    public bool ImageVectorEnabled { get; set; } = false;
    public double VectorMatchThreshold { get; set; } = 0.85; // 相似度阈值

    // ---- P2：团队协作预留 ----
    public bool CollaborationEnabled { get; set; } = false;
    public string CollaborationProvider { get; set; } = "local";  // local / 自定义

    // ---- P2：插件系统 ----
    public bool PluginsEnabled { get; set; } = true;
    public string PluginsDir { get; set; } = "";              // 留空用 %APPDATA%\SuperNote\plugins

    // ---- P2：后台服务 ----
    public bool InstallBackgroundService { get; set; } = false;   // 可选 Windows 服务（大规模追踪）

    // ---- 桌面悬浮球（可个性化）----
    public bool FloatingBallEnabled { get; set; } = true;        // 桌面常驻悬浮球
    public int FloatingBallCount { get; set; } = 1;              // 球的个数 1~5（含主球 + 卫星球）
    public int FloatingBallDiameter { get; set; } = 56;          // 主球直径 px，40~96
    public double FloatingBallOpacity { get; set; } = 0.95;      // 整球不透明度，0.3~1.0
    public string FloatingBallAccent { get; set; } = "#0078D4";  // 卫星球配色（十六进制）
    public bool FloatingBallUseImage { get; set; } = true;       // 主球使用 3D 贴图（关闭则纯色渐变）
    public bool FloatingBallShowRedDot { get; set; } = true;     // 显示小红点
    public int FloatingBallLongPressMs { get; set; } = 650;      // 长按判定阈值 ms
    public string FloatingBallPosition { get; set; } = "";       // 记忆位置 "Left,Top"

    // ---- 待办提醒 ----
    public bool ReminderEnabled { get; set; } = true;            // 到点弹出待办提醒
    public int ReminderCheckSeconds { get; set; } = 30;         // 后台检查间隔（10~600 秒）
    public int ReminderAdvanceMinutes { get; set; } = 0;        // 提前多少分钟提醒（0=准点）
    public bool ReminderSnoozeEnabled { get; set; } = true;     // 允许「稍后提醒」

    // ---- 窗口固定（展开面板打开的窗口）----
    public bool PinQuickNote { get; set; } = false;   // 快速便签窗口固定
    public bool PinQuickTask { get; set; } = false;   // 快速待办窗口固定
    public bool PinClipboard { get; set; } = false;   // 剪贴板面板窗口固定
    public bool PinFileNote { get; set; } = false;    // 文件/文件夹备注窗口固定

    [JsonIgnore]
    public string DatabasePath => Path.Combine(Dir, "supernote.db");

    [JsonIgnore]
    public string AttachmentDir
    {
        get
        {
            var d = Path.Combine(Dir, "attachments");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    [JsonIgnore]
    public string ResolvedSidecarDir
    {
        get
        {
            var d = string.IsNullOrWhiteSpace(SidecarCentralDir)
                ? Path.Combine(Dir, "sidecars")
                : SidecarCentralDir;
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>插件目录（P2）：留空用 %APPDATA%\SuperNote\plugins。</summary>
    [JsonIgnore]
    public string ResolvedPluginDir
    {
        get
        {
            var d = string.IsNullOrWhiteSpace(PluginsDir)
                ? Path.Combine(Dir, "plugins")
                : PluginsDir;
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    public static SettingsService Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<SettingsService>(json) ?? new SettingsService();
                s.Normalize();
                return s;
            }
        }
        catch { /* 损坏则回落默认 */ }
        return new SettingsService();
    }

    /// <summary>兼容旧设置文件：补齐可能缺失的集合字段。</summary>
    private void Normalize()
    {
        ClipboardExcludedApps ??= new();
        TrackedRoots ??= new();
        if (TimelineEnabledCategories == null || TimelineEnabledCategories.Count == 0)
            TimelineEnabledCategories = new(TimelineCategories.DefaultEnabled);

        // 旧字段 InheritPolicyOnCopy → 新枚举
        if (InheritDefault == InheritPolicy.Ask && !string.IsNullOrEmpty(InheritPolicyOnCopy))
        {
            InheritDefault = InheritPolicyOnCopy switch
            {
                "Always" => InheritPolicy.Always,
                "Never" => InheritPolicy.Never,
                "SameDir" => InheritPolicy.SameDirOnly,
                "SidecarOnly" => InheritPolicy.SidecarOnly,
                _ => InheritPolicy.Ask
            };
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* 忽略写入失败 */ }
    }
}
