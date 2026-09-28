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
                if (s != null) return s;
            }
        }
        catch { /* 解析失败 → 默认配置 */ }

        var def = new BallSettings();
        Save(def);
        return def;
    }

    public void Save(BallSettings? settings = null)
    {
        if (settings != null) Current = settings;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options)); }
        catch { }
    }
}
