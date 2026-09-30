using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PocketConsole.Api.Services;

internal static class WindowsAiTextRecognizer
{
    // Windows AI TextRecognizer 仅在受支持的系统和 NPU 上可用，实例按进程复用。
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TextRecognizer? _recognizer;
    private static bool _unavailable;

    internal static async Task<IReadOnlyList<OcrTextLine>?> TryReadTextAsync(Bitmap bitmap, Rectangle region, int scale, CancellationToken cancellationToken)
    {
        // 当前系统不支持或初始化失败时返回 null，由调用方继续使用旧版 Windows OCR。
        if (_unavailable) return null;
        try
        {
            var recognizer = await GetRecognizerAsync(cancellationToken);
            if (recognizer is null) return null;
            region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (region.Width <= 0 || region.Height <= 0) return [];
            using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
            using var source = scale <= 1 ? (Bitmap)cropped.Clone() : Resize(cropped, cropped.Width * scale, cropped.Height * scale);
            using var softwareBitmap = await CreateSoftwareBitmapAsync(source);
            using var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(softwareBitmap);
            var result = await recognizer.RecognizeTextFromImageAsync(imageBuffer);
            return result.Lines.Select(line =>
            {
                var points = new[] { line.BoundingBox.TopLeft, line.BoundingBox.TopRight, line.BoundingBox.BottomLeft, line.BoundingBox.BottomRight };
                var left = points.Min(point => point.X);
                var top = points.Min(point => point.Y);
                var right = points.Max(point => point.X);
                var bottom = points.Max(point => point.Y);
                var confidence = line.Words.Length == 0 ? 0 : line.Words.Average(word => word.MatchConfidence);
                return new OcrTextLine(
                    NormalizeText(line.Text),
                    new RectangleF(
                        region.Left + (float)left / scale,
                        region.Top + (float)top / scale,
                        (float)(right - left) / scale,
                        (float)(bottom - top) / scale),
                    confidence);
            }).Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Confidence >= 0.35f).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _unavailable = true;
            return null;
        }
    }

    private static async Task<TextRecognizer?> GetRecognizerAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null) return _recognizer;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_recognizer is not null) return _recognizer;
            // NotReady 表示组件可能通过 EnsureReadyAsync 准备；其他状态直接视为本机不可用。
            var state = TextRecognizer.GetReadyState();
            if (state != AIFeatureReadyState.Ready)
            {
                if (state != AIFeatureReadyState.NotReady)
                {
                    _unavailable = true;
                    return null;
                }
                var readyResult = await TextRecognizer.EnsureReadyAsync();
                if (readyResult.Status != AIFeatureReadyResultState.Success)
                {
                    _unavailable = true;
                    return null;
                }
            }
            _recognizer = await TextRecognizer.CreateAsync();
            return _recognizer;
        }
        finally { Gate.Release(); }
    }

    private static async Task<SoftwareBitmap> CreateSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
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
