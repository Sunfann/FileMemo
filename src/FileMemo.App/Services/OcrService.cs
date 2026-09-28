using System.IO;
using System.Runtime.Versioning;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 图片 OCR（需求 3.2 / 3.7，P1）：把剪贴板图片、备注插图转成可搜索文本。
///
/// 依赖 Windows.Media.Ocr（Win10 1809+ 内置 OCR 引擎）。工程 TFM 为
/// net8.0-windows10.0.19041.0，SupportedOSPlatformVersion 保持 10.0.17763.0，
/// 因此旧系统上调用会被 <see cref="IsAvailable"/> 拦截并静默降级。
/// </summary>
public sealed class OcrService
{
    private readonly SettingsService _settings;

    public OcrService(SettingsService settings) => _settings = settings;

    /// <summary>当前环境是否具备 OCR 能力（引擎可用 + 用户已开启）。</summary>
    public bool IsAvailable
    {
        get
        {
            if (!_settings.OcrEnabled) return false;
            try { return OcrEngineAvailable(); } catch { return false; }
        }
    }

    private static bool OcrEngineAvailable()
    {
#if WINDOWS_OCR
        var lang = new Windows.Globalization.Language("zh-Hans-CN");
        return Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang) != null
            || Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() != null;
#else
        return false;
#endif
    }

    /// <summary>识别单张图片文本；失败返回 Success=false，不抛异常。</summary>
    public async Task<OcrResult> RecognizeAsync(string imagePath)
    {
        var result = new OcrResult { Language = _settings.OcrLanguage };
#if WINDOWS_OCR
        if (!_settings.OcrEnabled)
        {
            result.Error = "OCR 未开启";
            return result;
        }
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            result.Error = "图片不存在";
            return result;
        }

        try
        {
            using var stream = File.OpenRead(imagePath);
            var raStream = stream.AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(raStream);
            var bitmap = await decoder.GetSoftwareBitmapAsync();

            var engine =
                Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(_settings.OcrLanguage))
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();

            if (engine == null)
            {
                result.Error = "系统未安装对应 OCR 语言包";
                return result;
            }

            var ocr = await engine.RecognizeAsync(bitmap);
            result.Text = ocr.Text ?? "";
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
#else
        result.Error = "本构建未启用 Windows OCR（需在 Windows 上编译）";
        await Task.CompletedTask;
#endif
        return result;
    }

    /// <summary>对剪贴板图片附件批量 OCR（由剪贴板服务在落盘后调用）。</summary>
    public async Task<string> RecognizeToTextAsync(string imagePath)
    {
        var r = await RecognizeAsync(imagePath);
        return r.Success ? r.Text : "";
    }
}
