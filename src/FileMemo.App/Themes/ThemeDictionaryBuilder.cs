using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FileMemo.App.Themes;

/// <summary>
/// 把 <see cref="ThemeDefinition"/> 转换成运行时 <see cref="ResourceDictionary"/>。
///
/// 31 个令牌由模板循环生成 —— 结构上不可能漏键，也不需要为每款主题维护一份 XAML。
/// 生成的字典带一个标记键 <see cref="MarkerKey"/>，供 ThemeManager 在
/// MergedDictionaries 中定位「哪一项是主题色字典」。
/// </summary>
internal static class ThemeDictionaryBuilder
{
    /// <summary>
    /// 标记键：值为主题 Id。
    /// 用它而不是 Source 文件名字符串来定位，既支持内存字典，也不怕改文件名。
    /// 该键不属于 31 个颜色令牌，仅供内部识别。
    /// </summary>
    public const string MarkerKey = "ThemeMarker";

    /// <summary>按定义生成一份完整的颜色字典。</summary>
    public static ResourceDictionary Build(ThemeDefinition t)
    {
        var d = new ResourceDictionary
        {
            // ---- 4 个 Color 令牌 ----
            ["ColorPrimary"] = ParseColor(t.ColorPrimary),
            ["ColorPrimaryHover"] = ParseColor(t.ColorPrimaryHover),
            ["ColorPrimaryPressed"] = ParseColor(t.ColorPrimaryPressed),
            ["ColorPrimarySoft"] = ParseColor(t.ColorPrimarySoft),
        };

        // ---- 25 个 SolidColorBrush 令牌 ----
        foreach (var (key, hex) in BrushTokens(t))
            d[key] = MakeBrush(hex);

        // ---- 2 个阴影（仅透明度随主题）----
        d["ShadowPopup"] = MakeShadow(24, 4, t.ShadowPopupOpacity);
        d["ShadowDialog"] = MakeShadow(28, 8, t.ShadowDialogOpacity);

        // ---- 定位标记 ----
        d[MarkerKey] = t.Id;

        return d;
    }

    /// <summary>返回全部 25 个画刷令牌的 (键, 色值) 序列。</summary>
    private static IEnumerable<(string Key, string Hex)> BrushTokens(ThemeDefinition t)
    {
        yield return ("PrimaryBrush", t.PrimaryBrush);
        yield return ("PrimaryHoverBrush", t.PrimaryHoverBrush);
        yield return ("PrimaryPressedBrush", t.PrimaryPressedBrush);
        yield return ("PrimarySoftBrush", t.PrimarySoftBrush);
        yield return ("TextOnPrimaryBrush", t.TextOnPrimaryBrush);

        yield return ("SuccessBrush", t.SuccessBrush);
        yield return ("WarningBrush", t.WarningBrush);
        yield return ("DangerBrush", t.DangerBrush);
        yield return ("InfoBrush", t.InfoBrush);

        yield return ("SurfaceBrush", t.SurfaceBrush);
        yield return ("SurfaceAltBrush", t.SurfaceAltBrush);
        yield return ("BgHoverBrush", t.BgHoverBrush);
        yield return ("BgSelectedBrush", t.BgSelectedBrush);
        yield return ("BorderBrushSoft", t.BorderBrushSoft);

        yield return ("TextPrimaryBrush", t.TextPrimaryBrush);
        yield return ("TextSecondaryBrush", t.TextSecondaryBrush);
        yield return ("TextTertiaryBrush", t.TextTertiaryBrush);

        yield return ("WindowBackdropFallbackBrush", t.WindowBackdropFallbackBrush);
        yield return ("FloatSurfaceBrush", t.FloatSurfaceBrush);

        yield return ("GraphLegendBrush1", t.GraphLegendBrush1);
        yield return ("GraphLegendBrush2", t.GraphLegendBrush2);
        yield return ("GraphLegendBrush3", t.GraphLegendBrush3);
        yield return ("GraphLegendBrush4", t.GraphLegendBrush4);
        yield return ("GraphCanvasBrush", t.GraphCanvasBrush);
        yield return ("GraphNodeStrokeBrush", t.GraphNodeStrokeBrush);
    }

    // ------------------------------------------------------------------
    //  工具
    // ------------------------------------------------------------------

    private static Color ParseColor(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;

    private static SolidColorBrush MakeBrush(string hex)
    {
        var b = new SolidColorBrush(ParseColor(hex));
        b.Freeze();   // 冻结：跨线程安全 + 渲染性能更好，也避免被意外修改
        return b;
    }

    private static DropShadowEffect MakeShadow(double blur, double depth, double opacity)
        => new()
        {
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            Color = Colors.Black,
            Opacity = opacity,
        };
}
