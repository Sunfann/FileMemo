using System;
using System.Collections.Generic;
using System.Linq;

namespace FileMemo.App.Themes;

/// <summary>
/// 内置主题登记表：11 款精选配色（含复刻 DeskBox 风格的 DeskBox 深色 / 浅色）。
///
/// 命名约定（各主题通用）：
///   · PrimaryHover  = 主色提亮约 8%（浅色主题略压暗）
///   · PrimaryPressed = 主色压暗约 12%
///   · PrimarySoft    = 主色 + 0x24(≈14%) alpha 的浅底
///   · 浅色主题：BgHover=#0F000000，BorderSoft=#1A000000，阴影 0.16 / 0.20
///   · 深色主题：BgHover=#0FFFFFFF，BorderSoft=#19FFFFFF，阴影 0.45 / 0.55
///   · WindowBackdropFallback 取 Surface；FloatSurface 取 Surface + 0xE6 alpha
///
/// ★ 对比度约束（WCAG AA）：
///   TextOnPrimaryBrush 对 PrimaryBrush ≥ 4.5:1
///   TextPrimaryBrush   对 SurfaceBrush / SurfaceAltBrush ≥ 4.5:1
///   浅色主题若沿用「accent 原色」作主按钮底色，白字通常只有 3.x —— 必须把
///   Primary 系列压深到约 5:1 余量再配白字（参见 Solarized 的取舍说明）。
/// 
public static class ThemeCatalog
{
    /// <summary>全部内置主题（按此顺序在设置页画廊中展示）。</summary>
    // ★ 必须延迟初始化：本成员在源文件中位于各主题静态字段（Dracula…Gruvbox）之前，
    //   而 C# 静态初始化严格按文本顺序执行。若写成字段初始化器，构造本列表时
    //   那些主题字段尚未赋值，结果会是 9 个 null —— 表现为设置页主题画廊空白、
    //   点击卡片无法切换（ThemeManager 拿到 null 定义后静默失败）。
    //   改为首次访问时才构造，即可保证读到已初始化完成的主题字段。
    public static IReadOnlyList<ThemeDefinition> All => _all ??=
        new[]
        {
            DeskBoxDark, DeskBoxLight,
            Dracula, Nord, Mocha, Latte, RosePine, TokyoNight, OneDark, Solarized, Gruvbox,
        };

    private static ThemeDefinition[]? _all;

    /// <summary>新用户默认主题（同时是非法 Id 的回退目标）。</summary>
    public static ThemeDefinition Default => DeskBoxDark;

    /// <summary>按 Id 查找（大小写不敏感）；找不到返回 null。</summary>
    public static ThemeDefinition? ById(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    // ==================================================================
    //  ①  Dracula 德古拉 —— 经典暗色，紫粉色调，高对比度
    // ==================================================================
    public static readonly ThemeDefinition Dracula = new()
    {
        Id = "dracula", NameZh = "德古拉", NameEn = "Dracula", IsDark = true,
        Preview1 = "#FFBD93F9", Preview2 = "#FF282A36", Preview3 = "#FFF8F8F2", Preview4 = "#FFFF79C6",

        ColorPrimary = "#FFBD93F9", ColorPrimaryHover = "#FFCAA8FA",
        ColorPrimaryPressed = "#FF9D6FE6", ColorPrimarySoft = "#24BD93F9",

        PrimaryBrush = "#FFBD93F9", PrimaryHoverBrush = "#FFCAA8FA",
        PrimaryPressedBrush = "#FF9D6FE6", PrimarySoftBrush = "#24BD93F9",
        TextOnPrimaryBrush = "#FF21222C",

        SuccessBrush = "#FF50FA7B", WarningBrush = "#FFF1FA8C",
        DangerBrush = "#FFFF5555", InfoBrush = "#FF8BE9FD",

        SurfaceBrush = "#FF282A36", SurfaceAltBrush = "#FF21222C",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1FBD93F9", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFF8F8F2", TextSecondaryBrush = "#FFC7C9D9", TextTertiaryBrush = "#FF6272A4",

        WindowBackdropFallbackBrush = "#FF282A36", FloatSurfaceBrush = "#E6282A36",

        GraphLegendBrush1 = "#FF8BE9FD", GraphLegendBrush2 = "#FF50FA7B",
        GraphLegendBrush3 = "#FFBD93F9", GraphLegendBrush4 = "#FFFFB86C",
        GraphCanvasBrush = "#FF21222C", GraphNodeStrokeBrush = "#FF44475A",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ②  Nord 北极 —— 冷色调北欧风，柔和护眼
    // ==================================================================
    public static readonly ThemeDefinition Nord = new()
    {
        Id = "nord", NameZh = "北极", NameEn = "Nord", IsDark = true,
        Preview1 = "#FF88C0D0", Preview2 = "#FF2E3440", Preview3 = "#FFECEFF4", Preview4 = "#FFB48EAD",

        ColorPrimary = "#FF88C0D0", ColorPrimaryHover = "#FF9ACAD8",
        ColorPrimaryPressed = "#FF6FA8BB", ColorPrimarySoft = "#2488C0D0",

        PrimaryBrush = "#FF88C0D0", PrimaryHoverBrush = "#FF9ACAD8",
        PrimaryPressedBrush = "#FF6FA8BB", PrimarySoftBrush = "#2488C0D0",
        TextOnPrimaryBrush = "#FF2E3440",

        SuccessBrush = "#FFA3BE8C", WarningBrush = "#FFEBCB8B",
        DangerBrush = "#FFBF616A", InfoBrush = "#FF81A1C1",

        SurfaceBrush = "#FF2E3440", SurfaceAltBrush = "#FF3B4252",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1F88C0D0", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFECEFF4", TextSecondaryBrush = "#FFD8DEE9", TextTertiaryBrush = "#FF7B88A1",

        WindowBackdropFallbackBrush = "#FF2E3440", FloatSurfaceBrush = "#E62E3440",

        GraphLegendBrush1 = "#FF88C0D0", GraphLegendBrush2 = "#FFA3BE8C",
        GraphLegendBrush3 = "#FFB48EAD", GraphLegendBrush4 = "#FFEBCB8B",
        GraphCanvasBrush = "#FF272C36", GraphNodeStrokeBrush = "#FF434C5E",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ③  Mocha 摩卡 —— Catppuccin 风格，温暖粉彩深色（默认主题）
    // ==================================================================
    public static readonly ThemeDefinition Mocha = new()
    {
        Id = "mocha", NameZh = "摩卡", NameEn = "Mocha", IsDark = true,
        Preview1 = "#FF89B4FA", Preview2 = "#FF1E1E2E", Preview3 = "#FFCDD6F4", Preview4 = "#FFCBA6F7",

        ColorPrimary = "#FF89B4FA", ColorPrimaryHover = "#FFA3C5FB",
        ColorPrimaryPressed = "#FF6E9AE8", ColorPrimarySoft = "#2489B4FA",

        PrimaryBrush = "#FF89B4FA", PrimaryHoverBrush = "#FFA3C5FB",
        PrimaryPressedBrush = "#FF6E9AE8", PrimarySoftBrush = "#2489B4FA",
        TextOnPrimaryBrush = "#FF1E1E2E",

        SuccessBrush = "#FFA6E3A1", WarningBrush = "#FFF9E2AF",
        DangerBrush = "#FFF38BA8", InfoBrush = "#FF89DCEB",

        SurfaceBrush = "#FF1E1E2E", SurfaceAltBrush = "#FF181825",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1F89B4FA", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFCDD6F4", TextSecondaryBrush = "#FFBAC2DE", TextTertiaryBrush = "#FF7F849C",

        WindowBackdropFallbackBrush = "#FF1E1E2E", FloatSurfaceBrush = "#E61E1E2E",

        GraphLegendBrush1 = "#FF89B4FA", GraphLegendBrush2 = "#FFA6E3A1",
        GraphLegendBrush3 = "#FFCBA6F7", GraphLegendBrush4 = "#FFF9E2AF",
        GraphCanvasBrush = "#FF181825", GraphNodeStrokeBrush = "#FF313244",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ④  Latte 拿铁 —— Catppuccin 风格，明亮粉彩浅色
    // ==================================================================
    public static readonly ThemeDefinition Latte = new()
    {
        Id = "latte", NameZh = "拿铁", NameEn = "Latte", IsDark = false,
        Preview1 = "#FF1E66F5", Preview2 = "#FFEFF1F5", Preview3 = "#FF4C4F69", Preview4 = "#FF8839EF",

        ColorPrimary = "#FF1E66F5", ColorPrimaryHover = "#FF1A5AD9",
        ColorPrimaryPressed = "#FF1547B8", ColorPrimarySoft = "#1F1E66F5",

        PrimaryBrush = "#FF1E66F5", PrimaryHoverBrush = "#FF1A5AD9",
        PrimaryPressedBrush = "#FF1547B8", PrimarySoftBrush = "#1F1E66F5",
        TextOnPrimaryBrush = "#FFFFFFFF",

        SuccessBrush = "#FF40A02B", WarningBrush = "#FFDF8E1D",
        DangerBrush = "#FFD20F39", InfoBrush = "#FF1E66F5",

        SurfaceBrush = "#FFEFF1F5", SurfaceAltBrush = "#FFE6E9EF",
        BgHoverBrush = "#0F000000", BgSelectedBrush = "#1A000000", BorderBrushSoft = "#1A000000",

        TextPrimaryBrush = "#FF4C4F69", TextSecondaryBrush = "#FF5C5F77", TextTertiaryBrush = "#FF8C8FA3",

        WindowBackdropFallbackBrush = "#FFEFF1F5", FloatSurfaceBrush = "#E6EFF1F5",

        GraphLegendBrush1 = "#FF1E66F5", GraphLegendBrush2 = "#FF40A02B",
        GraphLegendBrush3 = "#FF8839EF", GraphLegendBrush4 = "#FFFE640B",
        GraphCanvasBrush = "#FFE6E9EF", GraphNodeStrokeBrush = "#FFFFFFFF",

        ShadowPopupOpacity = 0.16, ShadowDialogOpacity = 0.20,
    };

    // ==================================================================
    //  ⑤  Rosé Pine 玫瑰松 —— 优雅浪漫，柔和玫瑰色调
    // ==================================================================
    public static readonly ThemeDefinition RosePine = new()
    {
        Id = "rosepine", NameZh = "玫瑰松", NameEn = "Rosé Pine", IsDark = true,
        Preview1 = "#FFC4A7E7", Preview2 = "#FF191724", Preview3 = "#FFE0DEF4", Preview4 = "#FFEB6F92",

        ColorPrimary = "#FFC4A7E7", ColorPrimaryHover = "#FFD3BCEE",
        ColorPrimaryPressed = "#FFA98BD4", ColorPrimarySoft = "#24C4A7E7",

        PrimaryBrush = "#FFC4A7E7", PrimaryHoverBrush = "#FFD3BCEE",
        PrimaryPressedBrush = "#FFA98BD4", PrimarySoftBrush = "#24C4A7E7",
        TextOnPrimaryBrush = "#FF191724",

        SuccessBrush = "#FF9CCFD8", WarningBrush = "#FFF6C177",
        DangerBrush = "#FFEB6F92", InfoBrush = "#FF31748F",

        SurfaceBrush = "#FF191724", SurfaceAltBrush = "#FF1F1D2E",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1FC4A7E7", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFE0DEF4", TextSecondaryBrush = "#FF908CAA", TextTertiaryBrush = "#FF6E6A86",

        WindowBackdropFallbackBrush = "#FF191724", FloatSurfaceBrush = "#E6191724",

        GraphLegendBrush1 = "#FFC4A7E7", GraphLegendBrush2 = "#FF9CCFD8",
        GraphLegendBrush3 = "#FFEBBCBA", GraphLegendBrush4 = "#FFF6C177",
        GraphCanvasBrush = "#FF14121F", GraphNodeStrokeBrush = "#FF403D52",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ⑥  Tokyo Night 东京夜 —— 现代都市感，蓝紫霓虹风格
    // ==================================================================
    public static readonly ThemeDefinition TokyoNight = new()
    {
        Id = "tokyonight", NameZh = "东京夜", NameEn = "Tokyo Night", IsDark = true,
        Preview1 = "#FF7AA2F7", Preview2 = "#FF1A1B26", Preview3 = "#FFC0CAF5", Preview4 = "#FFBB9AF7",

        ColorPrimary = "#FF7AA2F7", ColorPrimaryHover = "#FF96B6F9",
        ColorPrimaryPressed = "#FF5F8AE6", ColorPrimarySoft = "#247AA2F7",

        PrimaryBrush = "#FF7AA2F7", PrimaryHoverBrush = "#FF96B6F9",
        PrimaryPressedBrush = "#FF5F8AE6", PrimarySoftBrush = "#247AA2F7",
        TextOnPrimaryBrush = "#FF1A1B26",

        SuccessBrush = "#FF9ECE6A", WarningBrush = "#FFE0AF68",
        DangerBrush = "#FFF7768E", InfoBrush = "#FF7DCFFF",

        SurfaceBrush = "#FF1A1B26", SurfaceAltBrush = "#FF16161E",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1F7AA2F7", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFC0CAF5", TextSecondaryBrush = "#FFA9B1D6", TextTertiaryBrush = "#FF565F89",

        WindowBackdropFallbackBrush = "#FF1A1B26", FloatSurfaceBrush = "#E61A1B26",

        GraphLegendBrush1 = "#FF7AA2F7", GraphLegendBrush2 = "#FF9ECE6A",
        GraphLegendBrush3 = "#FFBB9AF7", GraphLegendBrush4 = "#FFFF9E64",
        GraphCanvasBrush = "#FF16161E", GraphNodeStrokeBrush = "#FF292E42",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ⑦  One Dark 孤夜蓝 —— Atom 经典暗蓝配色
    // ==================================================================
    public static readonly ThemeDefinition OneDark = new()
    {
        Id = "onedark", NameZh = "孤夜蓝", NameEn = "One Dark", IsDark = true,
        Preview1 = "#FF61AFEF", Preview2 = "#FF282C34", Preview3 = "#FFABB2BF", Preview4 = "#FFC678DD",

        ColorPrimary = "#FF61AFEF", ColorPrimaryHover = "#FF7FBEF3",
        ColorPrimaryPressed = "#FF4F97D6", ColorPrimarySoft = "#2461AFEF",

        PrimaryBrush = "#FF61AFEF", PrimaryHoverBrush = "#FF7FBEF3",
        PrimaryPressedBrush = "#FF4F97D6", PrimarySoftBrush = "#2461AFEF",
        TextOnPrimaryBrush = "#FF21252B",

        SuccessBrush = "#FF98C379", WarningBrush = "#FFE5C07B",
        DangerBrush = "#FFE06C75", InfoBrush = "#FF56B6C2",

        SurfaceBrush = "#FF282C34", SurfaceAltBrush = "#FF21252B",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1F61AFEF", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFABB2BF", TextSecondaryBrush = "#FF8B929E", TextTertiaryBrush = "#FF5C6370",

        WindowBackdropFallbackBrush = "#FF282C34", FloatSurfaceBrush = "#E6282C34",

        GraphLegendBrush1 = "#FF61AFEF", GraphLegendBrush2 = "#FF98C379",
        GraphLegendBrush3 = "#FFC678DD", GraphLegendBrush4 = "#FFE5C07B",
        GraphCanvasBrush = "#FF21252B", GraphNodeStrokeBrush = "#FF3A404A",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ⑧  Solarized Light 羊皮纸 —— 古董护眼米黄
    // ==================================================================
    public static readonly ThemeDefinition Solarized = new()
    {
        Id = "solarized", NameZh = "羊皮纸", NameEn = "Solarized Light", IsDark = false,
        Preview1 = "#FF1C6DA6", Preview2 = "#FFFDF6E3", Preview3 = "#FF4A5C63", Preview4 = "#FFCB4B16",

        ColorPrimary = "#FF1C6DA6", ColorPrimaryHover = "#FF1A6298",
        ColorPrimaryPressed = "#FF155683", ColorPrimarySoft = "#1F1C6DA6",

        PrimaryBrush = "#FF1C6DA6", PrimaryHoverBrush = "#FF1A6298",
        PrimaryPressedBrush = "#FF155683", PrimarySoftBrush = "#1F1C6DA6",
        TextOnPrimaryBrush = "#FFFFFFFF",

        SuccessBrush = "#FF859900", WarningBrush = "#FFB58900",
        DangerBrush = "#FFDC322F", InfoBrush = "#FF1C6DA6",

        SurfaceBrush = "#FFFDF6E3", SurfaceAltBrush = "#FFEEE8D5",
        BgHoverBrush = "#0F000000", BgSelectedBrush = "#1A000000", BorderBrushSoft = "#1A000000",

        TextPrimaryBrush = "#FF4A5C63", TextSecondaryBrush = "#FF5C6F76", TextTertiaryBrush = "#FF7E8C92",

        WindowBackdropFallbackBrush = "#FFFDF6E3", FloatSurfaceBrush = "#E6FDF6E3",

        GraphLegendBrush1 = "#FF1C6DA6", GraphLegendBrush2 = "#FF859900",
        GraphLegendBrush3 = "#FFD33682", GraphLegendBrush4 = "#FFCB4B16",
        GraphCanvasBrush = "#FFEEE8D5", GraphNodeStrokeBrush = "#FFFFFFFF",

        ShadowPopupOpacity = 0.16, ShadowDialogOpacity = 0.20,
    };

    // ==================================================================
    //  ⑨  Gruvbox 复古褐 —— 复古暖褐，怀旧质感
    // ==================================================================
    public static readonly ThemeDefinition Gruvbox = new()
    {
        Id = "gruvbox", NameZh = "复古褐", NameEn = "Gruvbox", IsDark = true,
        Preview1 = "#FFFABD2F", Preview2 = "#FF282828", Preview3 = "#FFEBDBB2", Preview4 = "#FFFE8019",

        ColorPrimary = "#FFFABD2F", ColorPrimaryHover = "#FFFBCB5B",
        ColorPrimaryPressed = "#FFE0A81F", ColorPrimarySoft = "#24FABD2F",

        PrimaryBrush = "#FFFABD2F", PrimaryHoverBrush = "#FFFBCB5B",
        PrimaryPressedBrush = "#FFE0A81F", PrimarySoftBrush = "#24FABD2F",
        TextOnPrimaryBrush = "#FF282828",

        SuccessBrush = "#FFB8BB26", WarningBrush = "#FFFABD2F",
        DangerBrush = "#FFFB4934", InfoBrush = "#FF83A598",

        SurfaceBrush = "#FF282828", SurfaceAltBrush = "#FF3C3836",
        BgHoverBrush = "#0FFFFFFF", BgSelectedBrush = "#1FFABD2F", BorderBrushSoft = "#19FFFFFF",

        TextPrimaryBrush = "#FFEBDBB2", TextSecondaryBrush = "#FFD5C4A1", TextTertiaryBrush = "#FF928374",

        WindowBackdropFallbackBrush = "#FF282828", FloatSurfaceBrush = "#E6282828",

        GraphLegendBrush1 = "#FF83A598", GraphLegendBrush2 = "#FFB8BB26",
        GraphLegendBrush3 = "#FFD3869B", GraphLegendBrush4 = "#FFFE8019",
        GraphCanvasBrush = "#FF1D2021", GraphNodeStrokeBrush = "#FF504945",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ⑩  DeskBox Dark 桌面整理 —— 复刻 DeskBox 规范（中性灰阶 + 系统强调蓝）
    //     来源：docs/design.md 深色主题表 + 功能色（Accent #0078D4 / Critical #C42B1C）
    // ==================================================================
    public static readonly ThemeDefinition DeskBoxDark = new()
    {
        Id = "deskboxdark", NameZh = "DeskBox 深色", NameEn = "DeskBox Dark", IsDark = true,
        Preview1 = "#FF0078D4", Preview2 = "#FF1F1F1F", Preview3 = "#FFF5F5F5", Preview4 = "#FFC42B1C",

        ColorPrimary = "#FF0078D4", ColorPrimaryHover = "#FF1A86E0",
        ColorPrimaryPressed = "#FF0067B8", ColorPrimarySoft = "#240078D4",

        PrimaryBrush = "#FF0078D4", PrimaryHoverBrush = "#FF1A86E0",
        PrimaryPressedBrush = "#FF0067B8", PrimarySoftBrush = "#240078D4",
        TextOnPrimaryBrush = "#FFFFFFFF",

        SuccessBrush = "#FF6CCB5F", WarningBrush = "#FFFCE100",
        DangerBrush = "#FFC42B1C", InfoBrush = "#FF0078D4",

        SurfaceBrush = "#FF1F1F1F", SurfaceAltBrush = "#FF252525",
        BgHoverBrush = "#12FFFFFF", BgSelectedBrush = "#1F0078D4", BorderBrushSoft = "#20FFFFFF",

        TextPrimaryBrush = "#FFF5F5F5", TextSecondaryBrush = "#FFA5A5A5", TextTertiaryBrush = "#FF767676",

        WindowBackdropFallbackBrush = "#FF1F1F1F", FloatSurfaceBrush = "#E61F1F1F",

        GraphLegendBrush1 = "#FF0078D4", GraphLegendBrush2 = "#FF6CCB5F",
        GraphLegendBrush3 = "#FFFCE100", GraphLegendBrush4 = "#FFC42B1C",
        GraphCanvasBrush = "#FF191919", GraphNodeStrokeBrush = "#FF3C3C3C",

        ShadowPopupOpacity = 0.45, ShadowDialogOpacity = 0.55,
    };

    // ==================================================================
    //  ⑪  DeskBox Light 桌面整理 —— 复刻 DeskBox 规范（浅色）
    //     来源：docs/design.md 浅色主题表；文本对比度满足 WCAG AA（≥4.5:1）
    // ==================================================================
    public static readonly ThemeDefinition DeskBoxLight = new()
    {
        Id = "deskboxlight", NameZh = "DeskBox 浅色", NameEn = "DeskBox Light", IsDark = false,
        Preview1 = "#FF0078D4", Preview2 = "#FFF3F3F3", Preview3 = "#FF1A1A1A", Preview4 = "#FFC42B1C",

        ColorPrimary = "#FF0078D4", ColorPrimaryHover = "#FF1A86E0",
        ColorPrimaryPressed = "#FF0067B8", ColorPrimarySoft = "#1F0078D4",

        PrimaryBrush = "#FF0078D4", PrimaryHoverBrush = "#FF1A86E0",
        PrimaryPressedBrush = "#FF0067B8", PrimarySoftBrush = "#1F0078D4",
        TextOnPrimaryBrush = "#FFFFFFFF",

        SuccessBrush = "#FF0F7B0F", WarningBrush = "#FF9D5D00",
        DangerBrush = "#FFC42B1C", InfoBrush = "#FF0078D4",

        SurfaceBrush = "#FFF3F3F3", SurfaceAltBrush = "#FFEBEBEB",
        BgHoverBrush = "#0F000000", BgSelectedBrush = "#1A000000", BorderBrushSoft = "#1A000000",

        TextPrimaryBrush = "#FF1A1A1A", TextSecondaryBrush = "#FF5A5A5A", TextTertiaryBrush = "#FF8A8A8A",

        WindowBackdropFallbackBrush = "#FFF3F3F3", FloatSurfaceBrush = "#E6F3F3F3",

        GraphLegendBrush1 = "#FF0078D4", GraphLegendBrush2 = "#FF0F7B0F",
        GraphLegendBrush3 = "#FF9D5D00", GraphLegendBrush4 = "#FFC42B1C",
        GraphCanvasBrush = "#FFEDEDED", GraphNodeStrokeBrush = "#FFD0D0D0",

        ShadowPopupOpacity = 0.16, ShadowDialogOpacity = 0.20,
    };
}
