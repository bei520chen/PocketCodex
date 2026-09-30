using System.Drawing;
using System.Drawing.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace PocketConsole.Api.Services;

internal static class PaddleTextRecognizer
{
    // RapidOcr 的 ONNX 会话不是为并发调用设计的，所有识别请求串行执行并复用同一组模型。
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RapidOcr? _recognizer;
    private static bool _unavailable;

    internal static async Task<IReadOnlyList<OcrTextLine>?> TryReadTextAsync(Bitmap bitmap, Rectangle region, int scale, CancellationToken cancellationToken)
    {
        // 初始化或推理失败后永久返回 null，让上层自动降级到 Windows OCR，避免每次扫描重复抛异常。
        if (_unavailable) return null;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var recognizer = GetRecognizer();
            if (recognizer is null) return null;
            region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (region.Width <= 0 || region.Height <= 0) return [];
            using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
            using var source = scale <= 1 ? (Bitmap)cropped.Clone() : Resize(cropped, cropped.Width * scale, cropped.Height * scale);
            using var memory = new MemoryStream();
            source.Save(memory, ImageFormat.Png);
            using var image = SKBitmap.Decode(memory.ToArray());
            if (image is null) return null;
            var result = recognizer.Detect(image, RapidOcrOptions.Default);
            // 将放大图上的检测框还原成原微信窗口坐标，供会话行和消息气泡定位使用。
            return result.TextBlocks.Select(block =>
            {
                var points = block.BoxPoints;
                var left = points.Min(point => point.X);
                var top = points.Min(point => point.Y);
                var right = points.Max(point => point.X);
                var bottom = points.Max(point => point.Y);
                var confidence = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : block.BoxScore;
                return new OcrTextLine(
                    NormalizeText(block.Text),
                    new RectangleF(
                        region.Left + (float)left / scale,
                        region.Top + (float)top / scale,
                        (float)(right - left) / scale,
                        (float)(bottom - top) / scale),
                    confidence);
            }).Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Confidence >= 0.55f).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _unavailable = true;
            return null;
        }
        finally { Gate.Release(); }
    }

    private static RapidOcr? GetRecognizer()
    {
        if (_recognizer is not null) return _recognizer;
        // 四个文件必须成套使用，中文识别模型与字典不匹配会产生无意义字符。
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", "RapidOcr");
        var detector = Path.Combine(modelDirectory, "ch_PP-OCRv5_mobile_det.onnx");
        var classifier = Path.Combine(modelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var recognizer = Path.Combine(modelDirectory, "ch_PP-OCRv5_rec_mobile.onnx");
        var dictionary = Path.Combine(modelDirectory, "ppocrv5_dict.txt");
        if (new[] { detector, classifier, recognizer, dictionary }.Any(path => !File.Exists(path)))
        {
            _unavailable = true;
            return null;
        }
        var engine = new RapidOcr();
        engine.InitModels(detector, classifier, recognizer, dictionary);
        _recognizer = engine;
        return _recognizer;
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    private static string NormalizeText(string text) => string.Join(string.Empty, text.Where(character => !char.IsWhiteSpace(character) && character != '�')).Trim();
}
