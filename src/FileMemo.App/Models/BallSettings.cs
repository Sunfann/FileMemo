using System.Collections.Generic;

namespace FileMemo.App.Models;

/// <summary>悬浮球系统配置（持久化到 %LOCALAPPDATA%\SuperNote\ball.json）。</summary>
public sealed class BallSettings
{
    public bool Enabled { get; set; } = true;

    public MainBallConfig MainBall { get; set; } = new();

    public List<SatelliteConfig> Satellites { get; set; } = new()
    {
        new SatelliteConfig { Color = "#FF3B30", Action = SatelliteAction.ToggleCollapse, Icon = "\uE8A7" },
        new SatelliteConfig { Color = "#0078D4", Action = SatelliteAction.OpenRecentNote, Icon = "\uE7C3" },
        new SatelliteConfig { Color = "#107C10", Action = SatelliteAction.OpenTasks,     Icon = "\uE9D5" },
        new SatelliteConfig { Color = "#F7630C", Action = SatelliteAction.OpenClips,      Icon = "\uE77F" },
    };

    public RedDotConfig RedDot { get; set; } = new();

    public ParticleConfig Particle { get; set; } = new();

    /// <summary>悬浮球记忆位置 "Left,Top"（DIP）。</summary>
    public string Position { get; set; } = "";
}

public sealed class MainBallConfig
{
    public double Size { get; set; } = 56;          // 直径 px 40~96
    public string Color { get; set; } = "#0A0A0A";  // 主球底色（深色玻璃）
    public string GlowColor { get; set; } = "#0078D4"; // 光晕色
    public bool RingEnabled { get; set; } = true;   // 环绕光环
    public double Opacity { get; set; } = 0.95;     // 0.3~1.0
    public bool SnapToEdge { get; set; } = true;    // 靠近边缘吸附
    public bool UseImage { get; set; } = true;      // 主球使用 3D 贴图（assets/ball.png）
}

public enum SatelliteAction
{
    ToggleCollapse,
    OpenRecentNote,
    OpenTasks,
    OpenClips,
    OpenMainWindow,
    OpenSettings,
    Exit
}

public sealed class SatelliteConfig
{
    public string Color { get; set; } = "#0078D4";
    public string Icon { get; set; } = "";
    public SatelliteAction Action { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RedDotConfig
{
    public bool Enabled { get; set; } = true;
    public int LongPressMs { get; set; } = 1000;    // 长按判定
    public bool PulseOnAlarm { get; set; } = true;  // 脉冲提示
}

public sealed class ParticleConfig
{
    public bool Enabled { get; set; } = true;
    public int Count { get; set; } = 320;
    public int DurationMs { get; set; } = 1200;
    public double Gravity { get; set; } = 0.15;
    public double Damping { get; set; } = 0.98;
}
