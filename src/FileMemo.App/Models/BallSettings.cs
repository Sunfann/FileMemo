using System.Collections.Generic;

namespace FileMemo.App.Models;

/// <summary>悬浮球系统配置（持久化到 %LOCALAPPDATA%\SuperNote\ball.json）。</summary>
public sealed class BallSettings
{
    public bool Enabled { get; set; } = true;

    public MainBallConfig MainBall { get; set; } = new();

    /// <summary>
    /// 展开面板动作列表：每个启用项 = 面板上的一个图标按钮（单击直接执行动作）。
    /// 出厂默认 4 个：快速便签 / 打开剪切板 / 快速待办 / 快速文件树备注。
    /// </summary>
    public List<SatelliteConfig> Satellites { get; set; } = new();

    /// <summary>管理控制点配置。</summary>
    public RedDotConfig RedDot { get; set; } = new();

    public ParticleConfig Particle { get; set; } = new();

    /// <summary>悬浮球记忆位置 "Left,Top"（DIP）。</summary>
    public string Position { get; set; } = "";

    /// <summary>
    /// 默认面板动作配置版本号。历史旧配置会在加载时自动补齐 / 启用出厂默认的
    /// 4 个面板动作（便签 / 剪切板 / 待办 / 文件文件夹备注）并打开展开面板开关。
    /// 当前默认版本 = 4。
    /// </summary>
    public int BallDefaultsVersion { get; set; } = 0;
}

/// <summary>
/// 主球（Floating Intelligence Orb）配置。
/// 视觉规格：直径 46px（38~56）、深色玻璃、半透明、呼吸动画。
/// </summary>
public sealed class MainBallConfig
{
    public double Size { get; set; } = 46;               // 直径 px，38 ~ 56
    public string Color { get; set; } = "#0A0A0C";       // 深色玻璃底色
    public string GlowColor { get; set; } = "#B8C4D0";   // 冷白玻璃光晕（Hover / 呼吸）
    public bool RingEnabled { get; set; } = true;        // 光环（卫星轨道）开关 —— 主球的光环可关闭
    public double Opacity { get; set; } = 0.94;          // 0.3 ~ 1.0
    /// <summary>贴边吸附收拢时的不透明度（近乎透明；0.02 ~ 1.0，默认 0.10）。</summary>
    public double DockedOpacity { get; set; } = 0.10;
    /// <summary>呼出（滑出 / 悬停）时的不透明度（半透明；0.05 ~ 1.0，默认 0.72）。</summary>
    public double RevealedOpacity { get; set; } = 0.72;
    public bool SnapToEdge { get; set; } = true;         // 靠近边缘吸附
    public bool UseImage { get; set; } = false;          // 使用 Assets/ball.png 贴图（新规格默认纯玻璃球）

    // ---- 光环 / 卫星轨道 ----
    /// <summary>
    /// 光环（卫星轨道）半径 px（30 ~ 96）——卫星直接贴着主球，不绘制可见轨道线。
    /// 主球半径 23 + 卫星半径 7 = 30，取 30 使卫星边缘与主球边缘相切（零缝隙）。
    /// </summary>
    public double RingRadius { get; set; } = 30;
    /// <summary>光环配色（保留字段；当前不绘制可见轨道线）。</summary>
    public string RingColor { get; set; } = "#33FFFFFF";

    // ---- 新规格新增 ----
    /// <summary>常态呼吸动画（scale 1.0 ↔ 1.05，周期 2.5s）。</summary>
    public bool Breathing { get; set; } = true;
    /// <summary>显示球右侧的管理控制点。</summary>
    public bool ShowControlPoint { get; set; } = true;
    /// <summary>显示展开面板（悬停悬浮图标时滑出动作图标，单击执行动作）。</summary>
    public bool ShowSatellite { get; set; } = true;
    /// <summary>管理控制点直径 px（20 ~ 40）。</summary>
    public double ControlPointSize { get; set; } = 28;
    /// <summary>长按主球触发「粒子消散并退出」的时长 ms（默认 3000）。</summary>
    public int LongPressExitMs { get; set; } = 3000;
    /// <summary>启动时播放 1.2s 出现动画（fade in + scale + bounce）。</summary>
    public bool PlayIntroAnimation { get; set; } = true;
}

public enum SatelliteAction
{
    ToggleCollapse,
    OpenRecentNote,
    OpenTasks,
    OpenClips,
    OpenMainWindow,
    OpenSettings,
    /// <summary>打开「文件 / 文件夹备注」页（主窗口文件树分区）。</summary>
    OpenFileNotes,
    Exit
}

/// <summary>
/// 卫星配置：主球光环轨道上的一颗「星球」。
///
/// 设计契约：
/// · 卫星是动作，不是菜单 —— 每颗卫星直接执行一个动作，不弹二级菜单。
/// · 卫星沿光环分布 —— 光环是轨道，卫星是星球，视觉逻辑自洽。
/// · 颜色 / 图标 / 动作 / 数量全部开放 —— 主球唯一（产品身份），卫星可增减（用户个性）。
/// </summary>
public sealed class SatelliteConfig
{
    /// <summary>稳定标识（用于默认配置匹配与未来设置页识别）。</summary>
    public string Id { get; set; } = "";

    /// <summary>悬停提示文案（留空则回退到动作默认名）。</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// 图标：优先当作 Icons.xaml 里的矢量资源键（如 "IconNote" / "IconFolder"）；
    /// 若不是有效键，则按任意文字 / emoji 渲染（如 "📝"）。
    /// </summary>
    public string Icon { get; set; } = "";

    /// <summary>卫星配色（十六进制）。</summary>
    public string Color { get; set; } = "#4C8BF5";

    /// <summary>点击直接执行的动作。</summary>
    public SatelliteAction Action { get; set; } = SatelliteAction.OpenRecentNote;

    /// <summary>是否挂载（关闭后该卫星不显示、不占位）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>在光环上的角度（度；0 = 3 点钟方向，顺时针为正）。null = 自动均分。</summary>
    public double? AngleDeg { get; set; } = null;

    /// <summary>相对基准直径的倍率（0.6 ~ 1.6）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// 出厂默认：四个面板动作 —— 快速便签 / 打开剪切板 / 快速待办 / 快速文件树备注。
    /// 展开面板以纯图标样式、一行从右到左排列；点击直接执行对应动作。
    /// </summary>
    public static List<SatelliteConfig> CreateDefaults() => new()
    {
        new SatelliteConfig
        {
            Id = "note",
            Label = "打开便签",
            Icon = "panel_note.png",
            Color = "#151A21",
            Action = SatelliteAction.OpenRecentNote,
            Enabled = true,
            AngleDeg = 225,
            Scale = 1.0
        },
        new SatelliteConfig
        {
            Id = "clips",
            Label = "打开剪切板",
            Icon = "panel_clipboard.png",
            Color = "#151A21",
            Action = SatelliteAction.OpenClips,
            Enabled = true,
            AngleDeg = 270,
            Scale = 1.0
        },
        new SatelliteConfig
        {
            Id = "tasks",
            Label = "打开待办",
            Icon = "panel_task.png",
            Color = "#151A21",
            Action = SatelliteAction.OpenTasks,
            Enabled = true,
            AngleDeg = 315,
            Scale = 1.0
        },
        new SatelliteConfig
        {
            Id = "filenotes",
            Label = "打开文件文件夹备注",
            Icon = "panel_fileref.png",
            Color = "#151A21",
            Action = SatelliteAction.OpenFileNotes,
            Enabled = true,
            AngleDeg = 0,
            Scale = 1.0
        }
    };
}

/// <summary>管理控制点配置（球右侧的小控制点）。</summary>
public sealed class RedDotConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>长按判定：按下多久触发「一键收纳 / 释放」。</summary>
    public int LongPressMs { get; set; } = 1000;
    public bool PulseOnAlarm { get; set; } = true;
}

public sealed class ParticleConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>退出消散的粒子数（规格 60~80，默认 72）。</summary>
    public int Count { get; set; } = 72;
    /// <summary>单颗粒子的总存活时长 ms（规格 600）。</summary>
    public int DurationMs { get; set; } = 600;
    /// <summary>重力（规格为纯径向扩散，默认 0）。</summary>
    public double Gravity { get; set; } = 0.0;
    /// <summary>阻尼系数，0.94 近似 ease-out（速度逐帧衰减）。</summary>
    public double Damping { get; set; } = 0.94;
}
