using System;
using System.Linq;
using System.Windows;
using FileMemo.App.Themes;
using Microsoft.Win32;

namespace FileMemo.App.Services;

/// <summary>
/// 主题管理器：负责内置主题的切换与热应用。
///
/// 实现要点：
/// 1. 颜色令牌全部由 <see cref="ThemeDictionaryBuilder"/> 按 <see cref="ThemeCatalog"/> 中的定义生成，
///    App.xaml 的第 2 项只放一份「默认主题兜底」（Themes/Theme.Initial.xaml），
///    启动时由本类整体替换成用户所选主题。
/// 2. 因为所有颜色引用都用 DynamicResource，替换字典后**已打开的窗口会自动刷新**，
///    无需重建窗口。只有「命令式」的东西（DWM 属性、代码里 new 出来的 Brush）需要
///    订阅 <see cref="ThemeChanged"/> 手动更新。
/// 3. 字典定位靠标记键 <see cref="ThemeDictionaryBuilder.MarkerKey"/>，
///    而不是 Source 文件名字符串 —— 更稳健，且支持内存字典。
/// </summary>
public static class ThemeManager
{
    /// <summary>当前生效的主题定义（永不为 null；非法 Id 已在解析阶段回退到默认）。</summary>
    public static ThemeDefinition Current { get; private set; } = ThemeCatalog.Default;

    /// <summary>当前主题 Id（即写入 settings.json 的值）。</summary>
    public static string CurrentId => Current.Id;

    /// <summary>
    /// 当前主题是否为深色。
    /// 供 DWM 原生标题栏、Win11 Mica 背板、窗口底色回退判断使用。
    /// </summary>
    public static bool IsDark => Current.IsDark;

    /// <summary>
    /// 主题实际发生变化时触发（供 DWM / 命令式画刷订阅）。
    /// 注意：只要主题 Id 变化就触发 —— 同为深色的 Dracula→Mocha 也需要重绘图表画布。
    /// </summary>
    public static event Action? ThemeChanged;

    private static bool _initialized;

    // ------------------------------------------------------------------
    //  对外 API
    // ------------------------------------------------------------------

    /// <summary>
    /// 应用启动时调用一次，传入 settings.json 里保存的 Theme 字符串
    /// （可能是新格式的主题 Id，也可能是旧格式的 "System" / "Light" / "Dark"）。
    /// </summary>
    public static void Initialize(string? id)
    {
        _initialized = true;
        Apply(ThemeCatalog.ById(ParseThemeId(id)) ?? ThemeCatalog.Default);
    }

    /// <summary>设置面板切换主题时调用：立即生效（持久化由调用方负责）。未知 Id 静默忽略。</summary>
    public static void SetTheme(string id)
    {
        var def = ThemeCatalog.ById(id);
        if (def is null) return;
        Apply(def);
    }

    /// <summary>
    /// 把任意字符串解析成合法的主题 Id。
    /// 兼容旧版本 settings.json 的 "System" / "Light" / "Dark" 三个旧值。
    /// </summary>
    public static string ParseThemeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ThemeCatalog.Default.Id;
        var s = raw.Trim();

        // 1) 命中已知主题 Id（大小写不敏感）
        var hit = ThemeCatalog.All.FirstOrDefault(t =>
            string.Equals(t.Id, s, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit.Id;

        // 2) 旧值映射：老用户升级后不丢外观偏好
        return s switch
        {
            "Dark" => "mocha",                                  // 旧深色 → 摩卡
            "Light" => "latte",                                 // 旧浅色 → 拿铁
            "System" => IsSystemDark() ? "mocha" : "latte",     // 旧跟随系统 → 按系统明暗选
            _ => ThemeCatalog.Default.Id,                       // 非法值 → 默认
        };
    }

    /// <summary>
    /// 读取系统「应用模式」是否为深色。
    /// 注册表读不到或异常时返回 false（浅色），绝不抛。
    /// 仅在解析旧值 "System" 时使用。
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = 浅色, 0 = 深色；值不存在时按浅色处理
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  内部实现
    // ------------------------------------------------------------------

    /// <summary>替换颜色字典并广播变化。</summary>
    private static void Apply(ThemeDefinition def)
    {
        bool changed = !_initialized || def.Id != Current.Id;
        _initialized = true;
        Current = def;

        var app = Application.Current;
        if (app is not null)
        {
            try
            {
                ReplaceColorDictionary(app, def);
            }
            catch
            {
                // 资源字典替换失败不应导致应用崩溃 —— 退化为当前外观
            }
        }

        if (changed)
        {
            try { ThemeChanged?.Invoke(); } catch { /* 订阅方异常不影响主题切换 */ }
        }
    }

    /// <summary>
    /// 把 MergedDictionaries 里那一项主题色字典换成目标主题。
    /// 用标记键定位（而非 Source 文件名字符串）—— 更稳健。
    /// </summary>
    private static void ReplaceColorDictionary(Application app, ThemeDefinition def)
    {
        var dicts = app.Resources.MergedDictionaries;

        for (int i = 0; i < dicts.Count; i++)
        {
            if (!dicts[i].Contains(ThemeDictionaryBuilder.MarkerKey)) continue;

            // 已经是目标主题，无需替换
            if ((string?)dicts[i][ThemeDictionaryBuilder.MarkerKey] == def.Id) return;

            dicts[i] = ThemeDictionaryBuilder.Build(def);
            return;
        }

        // 兜底：App.xaml 里没找到带标记的字典（首次运行 / 被外部清空）→ 插到 Tokens.xaml 之后
        dicts.Insert(1, ThemeDictionaryBuilder.Build(def));
    }
}
