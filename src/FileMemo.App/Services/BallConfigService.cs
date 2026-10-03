using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>悬浮球配置的 JSON 读写（%LOCALAPPDATA%\SuperNote\ball.json）。</summary>
public sealed class BallConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public BallSettings Current { get; private set; }

    public BallConfigService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperNote");
        try { Directory.CreateDirectory(dir); } catch { }
        FilePath = Path.Combine(dir, "ball.json");
        Current = Load();
    }

    public BallSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<BallSettings>(json, Options);
                if (s != null) { Normalize(s); return s; }
            }
        }
        catch { /* 解析失败 → 默认配置 */ }

        var def = new BallSettings();
        Normalize(def);
        Save(def);
        return def;
    }

    /// <summary>
    /// 旧版本配置归一化：把历史遗留的越界值夹到新规格范围内，
    /// 避免升级后出现「巨大的球」或「一次喷几百颗粒子」。
    /// 缺失的字段由 C# 默认值补齐，无需处理。
    /// </summary>
    private static void Normalize(BallSettings s)
    {
        s.MainBall ??= new MainBallConfig();
        s.RedDot ??= new RedDotConfig();
        s.Particle ??= new ParticleConfig();
        // 卫星：默认挂载动作卫星；若用户配置里已有启用项则尊重其设置。
        s.Satellites ??= new List<SatelliteConfig>();
        bool anySatellite = false;
        foreach (var sat in s.Satellites) if (sat.Enabled) { anySatellite = true; break; }
        if (!anySatellite) s.Satellites = SatelliteConfig.CreateDefaults();

        // 迁移：旧版默认只有 1 颗「打开便签」卫星 → 整体重置为出厂默认（4 个面板动作）。
        // 判定：仅 1 颗启用卫星、Id == "note" 且动作为 OpenRecentNote。
        const int CurrentBallDefaultsVersion = 6;
        if (s.BallDefaultsVersion < 2
            && s.Satellites.Count == 1
            && string.Equals(s.Satellites[0].Id, "note", StringComparison.Ordinal)
            && s.Satellites[0].Action == SatelliteAction.OpenRecentNote)
        {
            s.Satellites = SatelliteConfig.CreateDefaults();
        }
        // 迁移 v4：展开面板的 4 个默认动作「缺哪个补哪个、存在则确保启用」；
        // 旧版右键「切换光环」曾把 RingEnabled / ShowSatellite 关掉并持久化，导致新面板不显示，
        // 升级时统一重新打开展开面板开关。
        // 迁移 v5：动作改为「快速」入口（快速便签 / 快速待办 / 快速文件树备注），
        // 对默认动作的图标与悬停文案做一次刷新（默认动作无用户自定义场景，直接对齐出厂文案）。
        if (s.BallDefaultsVersion < CurrentBallDefaultsVersion)
        {
            foreach (var def in SatelliteConfig.CreateDefaults())
            {
                SatelliteConfig? existing = null;
                foreach (var sat in s.Satellites)
                    if (sat.Action == def.Action) { existing = sat; break; }
                if (existing == null)
                {
                    s.Satellites.Add(def);
                }
                else
                {
                    existing.Enabled = true;
                    if (s.BallDefaultsVersion >= 4) { existing.Label = def.Label; existing.Icon = def.Icon; }
                    if (string.IsNullOrWhiteSpace(existing.Icon)) existing.Icon = def.Icon;
                    if (string.IsNullOrWhiteSpace(existing.Label)) existing.Label = def.Label;
                }
            }
            s.MainBall.ShowSatellite = true;
        }
        s.BallDefaultsVersion = CurrentBallDefaultsVersion;
        // 迁移：早期版本的默认卫星是绿色 #2ECC71，用户明确不要绿色 —— 统一切换为深色玻璃。
        foreach (var sat in s.Satellites)
        {
            if (string.Equals(sat.Color, "#2ECC71", StringComparison.OrdinalIgnoreCase))
                sat.Color = "#151A21";
        }
        // 迁移：默认卫星位置由正下方（90°）改为左上方（225°），与管理控制点一样紧贴主球。
        foreach (var sat in s.Satellites)
        {
            if (sat.AngleDeg.HasValue && Math.Abs(sat.AngleDeg.Value - 90) < 0.001)
                sat.AngleDeg = 225;
        }

        s.MainBall.Size = Math.Clamp(s.MainBall.Size, 38, 56);
        s.MainBall.RingRadius = Math.Clamp(s.MainBall.RingRadius, 30, 96);
        s.MainBall.Opacity = Math.Clamp(s.MainBall.Opacity, 0.3, 1.0);
        s.MainBall.ControlPointSize = Math.Clamp(s.MainBall.ControlPointSize, 20, 40);
        s.MainBall.LongPressExitMs = Math.Clamp(s.MainBall.LongPressExitMs, 1000, 8000);

        s.RedDot.LongPressMs = Math.Clamp(s.RedDot.LongPressMs, 250, 3000);

        s.Particle.Count = Math.Clamp(s.Particle.Count, 48, 120);
        s.Particle.DurationMs = Math.Clamp(s.Particle.DurationMs, 300, 3000);
    }

    public void Save(BallSettings? settings = null)
    {
        if (settings != null) Current = settings;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options)); }
        catch { }
    }
}
