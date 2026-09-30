namespace FileMemo.App.Themes;

/// <summary>
/// 一款主题的完整定义：31 个颜色令牌 + 画廊预览所需的元数据。
///
/// 为什么用 C# 类而不是 9 个 XAML 文件：
///   · 31 键 × 每款主题，手写 XAML 会有大量重复样板，且极易漏键；
///   · 用本类 + <see cref="ThemeDictionaryBuilder"/> 循环生成，结构上不可能漏；
///   · 画廊预览色可以直接复用同一份定义，无需额外维护；
///   · 运行时字典用统一的「标记键」定位，比匹配 9 个文件名字符串稳健得多。
///
/// 所有色值字符串一律为 <c>#AARRGGBB</c> 格式。
/// </summary>
public sealed class ThemeDefinition
{
    // ------------------------------------------------------------------
    //  身份
    // ------------------------------------------------------------------

    /// <summary>存储用 Id，全小写，例如 "dracula"。写进 settings.json 的 Theme 字段。</summary>
    public string Id { get; init; } = "";

    /// <summary>中文显示名，例如 "德古拉"。</summary>
    public string NameZh { get; init; } = "";

    /// <summary>英文显示名，例如 "Dracula"。</summary>
    public string NameEn { get; init; } = "";

    /// <summary>是否深色主题。★ DWM 原生标题栏、Mica 背板、窗口底色都依赖它。</summary>
    public bool IsDark { get; init; }

    // ------------------------------------------------------------------
    //  画廊预览色（4 个代表色）
    //  注意：设置页的卡片色块用的是这里的硬编码值，
    //        绝不能用 DynamicResource，否则预览会随当前主题变化而失真。
    // ------------------------------------------------------------------

    /// <summary>预览色 1：建议取主色。</summary>
    public string Preview1 { get; init; } = "";

    /// <summary>预览色 2：建议取表面色。</summary>
    public string Preview2 { get; init; } = "";

    /// <summary>预览色 3：建议取主文字色。</summary>
    public string Preview3 { get; init; } = "";

    /// <summary>预览色 4：建议取一个鲜明的强调色。</summary>
    public string Preview4 { get; init; } = "";

    // ------------------------------------------------------------------
    //  Color 类型令牌（4 个）
    // ------------------------------------------------------------------

    public string ColorPrimary { get; init; } = "";
    public string ColorPrimaryHover { get; init; } = "";
    public string ColorPrimaryPressed { get; init; } = "";
    public string ColorPrimarySoft { get; init; } = "";

    // ------------------------------------------------------------------
    //  SolidColorBrush 类型令牌（25 个）
    // ------------------------------------------------------------------

    public string PrimaryBrush { get; init; } = "";
    public string PrimaryHoverBrush { get; init; } = "";
    public string PrimaryPressedBrush { get; init; } = "";
    public string PrimarySoftBrush { get; init; } = "";

    /// <summary>主色按钮上的文字 / 图标色。深色主题主色偏亮 → 用深字；浅色主题主色偏深 → 用白字。</summary>
    public string TextOnPrimaryBrush { get; init; } = "";

    public string SuccessBrush { get; init; } = "";
    public string WarningBrush { get; init; } = "";
    public string DangerBrush { get; init; } = "";
    public string InfoBrush { get; init; } = "";

    public string SurfaceBrush { get; init; } = "";
    public string SurfaceAltBrush { get; init; } = "";
    public string BgHoverBrush { get; init; } = "";
    public string BgSelectedBrush { get; init; } = "";
    public string BorderBrushSoft { get; init; } = "";

    public string TextPrimaryBrush { get; init; } = "";
    public string TextSecondaryBrush { get; init; } = "";
    public string TextTertiaryBrush { get; init; } = "";

    public string WindowBackdropFallbackBrush { get; init; } = "";
    public string FloatSurfaceBrush { get; init; } = "";

    public string GraphLegendBrush1 { get; init; } = "";
    public string GraphLegendBrush2 { get; init; } = "";
    public string GraphLegendBrush3 { get; init; } = "";
    public string GraphLegendBrush4 { get; init; } = "";
    public string GraphCanvasBrush { get; init; } = "";
    public string GraphNodeStrokeBrush { get; init; } = "";

    // ------------------------------------------------------------------
    //  阴影（BlurRadius / ShadowDepth / Direction / Color 全部固定，
    //        仅透明度随主题：深色主题需加深，否则黑色阴影不可见）
    // ------------------------------------------------------------------

    public double ShadowPopupOpacity { get; init; } = 0.2;
    public double ShadowDialogOpacity { get; init; } = 0.25;
}
