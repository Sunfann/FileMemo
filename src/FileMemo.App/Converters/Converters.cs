using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FileMemo.App.Converters;

/// <summary>
/// 文件/文件夹备注「状态」中文文案 → 状态颜色画刷。
/// 状态 → 颜色：草稿=灰、进行中=蓝、待审核=橙、已完成=绿、已归档=紫、废弃=红；
/// 未识别的一律按「草稿」灰色处理。
/// </summary>
public sealed class StateLabelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new SolidColorBrush(ColorFor(value?.ToString()));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static Color ColorFor(string? label) => label switch
    {
        "草稿" => Color.FromRgb(0x9E, 0x9E, 0x9E),
        "进行中" => Color.FromRgb(0x42, 0xA5, 0xF5),
        "待审核" => Color.FromRgb(0xFF, 0xA7, 0x26),
        "已完成" => Color.FromRgb(0x66, 0xBB, 0x6A),
        "已归档" => Color.FromRgb(0xAB, 0x47, 0xBC),
        "废弃" => Color.FromRgb(0xEF, 0x53, 0x50),
        _ => Color.FromRgb(0x9E, 0x9E, 0x9E),
    };
}

/// <summary>字符串非空 → Visible（用于标签、摘要等条件显示）。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>取反的布尔 → Visibility。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举与参数相等 → bool（用于导航选中判断）。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>本地图片路径 → ImageSource（BitmapImage），加载后立即释放文件句柄，避免占用原图。</summary>
public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value?.ToString();
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            path = path.Substring("file:///".Length);
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.Read))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>多个标签字符串（逗号分隔）→ 集合，供 ItemsControl 渲染 Pill。</summary>
public sealed class TagsToListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 十六进制色值字符串（#AARRGGBB）→ 冻结的 SolidColorBrush。
/// 用于主题画廊卡片：预览色绑定自 <see cref="Themes.ThemeDefinition"/> 的硬编码色值，
/// ★ 绝不能用 DynamicResource（那会被当前主题染色，9 张卡片看起来一模一样）。
/// 非法色值降级为透明，绝不抛异常。
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var hex = value?.ToString();
            if (string.IsNullOrWhiteSpace(hex)) return System.Windows.Media.Brushes.Transparent;
            var b = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch
        {
            return System.Windows.Media.Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 文字（参数格式 "真值|假值"，例如 "深色|浅色"）。</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "").Split('|');
        var t = parts.Length > 0 ? parts[0] : "是";
        var f = parts.Length > 1 ? parts[1] : "否";
        return value is true ? t : f;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
