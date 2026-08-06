using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PocketConsole.Api.Services;

internal static class WeChatNative
{
    private const uint PwRenderFullContent = 2;
    private const int SwRestore = 9;
    private const int SwMinimize = 6;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint RedrawInvalidate = 0x0001;
    private const uint RedrawUpdateNow = 0x0100;
    private const uint RedrawFrame = 0x0400;
    private const uint RedrawAllChildren = 0x0080;

    internal static IReadOnlyList<NativeWeChatWindow> DiscoverWindows()
    {
        var result = new List<NativeWeChatWindow>();
        foreach (var process in Process.GetProcesses().Where(process => process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("WeChat", StringComparison.OrdinalIgnoreCase)))
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) continue;
            var title = ReadWindowText(handle);
            var className = ReadClassName(handle);
            if (!LooksLikeMainWindow(title, className) || !GetWindowRect(handle, out var rect)) continue;
            var minimized = IsIconic(handle);
            if (!minimized && (rect.Width < 650 || rect.Height < 450)) continue;
            string path;
            DateTime startedAt;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
                startedAt = process.StartTime.ToUniversalTime();
            }
            catch
            {
                path = string.Empty;
                startedAt = DateTime.UnixEpoch;
            }
            result.Add(new NativeWeChatWindow(handle, process.Id, path, startedAt, title, className, minimized, rect));
        }
        return result;
    }

    internal static async Task PrepareWindowAsync(NativeWeChatWindow window, CancellationToken cancellationToken)
    {
        if (IsIconic(window.Handle)) ShowWindow(window.Handle, SwRestore);
        SetForegroundWindow(window.Handle);
        await Task.Delay(250, cancellationToken);
    }

    internal static WindowPlacement CapturePlacement(IntPtr handle)
    {
        var placement = WindowPlacement.Create();
        if (!GetWindowPlacement(handle, ref placement)) throw new InvalidOperationException("无法读取微信窗口状态。");
        return placement;
    }

    internal static Bitmap Capture(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("无法读取微信窗口尺寸。");
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, deviceContext, PwRenderFullContent)) throw new InvalidOperationException("微信窗口后台截图失败。");
        }
        finally { graphics.ReleaseHdc(deviceContext); }
        return bitmap;
    }

    internal static Bitmap CaptureScreen(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("无法读取微信窗口尺寸。");
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    internal static Task<IReadOnlyList<OcrTextLine>> ReadTextAsync(Bitmap bitmap, CancellationToken cancellationToken) => ReadTextAsync(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 1, cancellationToken);

    internal static async Task<IReadOnlyList<OcrTextLine>> ReadTextAsync(Bitmap bitmap, Rectangle region, int scale, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0) return [];
        using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        using var source = scale <= 1 ? (Bitmap)cropped.Clone() : Resize(cropped, cropped.Width * scale, cropped.Height * scale);
        using var memory = new MemoryStream();
        source.Save(memory, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-CN")) ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows 中文 OCR 不可用。");
        var result = await engine.RecognizeAsync(softwareBitmap);
        return result.Lines.Select(line =>
        {
            var boxes = line.Words.Select(word => word.BoundingRect).ToArray();
            var left = boxes.Length == 0 ? 0 : boxes.Min(box => box.X);
            var top = boxes.Length == 0 ? 0 : boxes.Min(box => box.Y);
            var right = boxes.Length == 0 ? 0 : boxes.Max(box => box.X + box.Width);
            var bottom = boxes.Length == 0 ? 0 : boxes.Max(box => box.Y + box.Height);
            return new OcrTextLine(NormalizeText(line.Text), new RectangleF(region.Left + (float)left / scale, region.Top + (float)top / scale, (float)(right - left) / scale, (float)(bottom - top) / scale));
        }).Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
    }

    internal static int FindConversationPaneRight(Bitmap bitmap)
    {
        var start = Math.Max(220, bitmap.Width / 5);
        var end = Math.Min(420, bitmap.Width / 2);
        var sampleYs = Enumerable.Range(0, 9).Select(index => 45 + index * Math.Max(20, (bitmap.Height - 100) / 9)).Where(y => y < bitmap.Height - 20).ToArray();
        for (var x = start; x < end; x++)
        {
            var matches = 0;
            foreach (var y in sampleYs)
            {
                var left = bitmap.GetPixel(Math.Max(0, x - 1), y);
                var right = bitmap.GetPixel(Math.Min(bitmap.Width - 1, x + 1), y);
                if (IsConversationBackground(left) && IsChatBackground(right)) matches++;
            }
            if (matches >= Math.Max(4, sampleYs.Length / 2)) return x;
        }
        return Math.Min(bitmap.Width / 3, 340);
    }

    internal static async Task<IReadOnlyList<UnreadBadge>> FindUnreadBadgesAsync(Bitmap bitmap, int paneRight, CancellationToken cancellationToken)
    {
        var scanLeft = 107;
        var scanRight = Math.Max(scanLeft + 20, paneRight - 8);
        var scanTop = 75;
        var scanBottom = bitmap.Height - 35;
        var visited = new bool[bitmap.Width, bitmap.Height];
        var badges = new List<UnreadBadge>();
        for (var y = scanTop; y < scanBottom; y += 2)
        for (var x = scanLeft; x < scanRight; x += 2)
        {
            if (visited[x, y] || !IsBadgeRed(bitmap.GetPixel(x, y))) continue;
            var queue = new Queue<Point>();
            queue.Enqueue(new Point(x, y));
            visited[x, y] = true;
            var minX = x; var maxX = x; var minY = y; var maxY = y; var pixels = 0;
            while (queue.Count > 0 && pixels < 3000)
            {
                var point = queue.Dequeue();
                pixels++;
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
                foreach (var next in new[] { new Point(point.X + 2, point.Y), new Point(point.X - 2, point.Y), new Point(point.X, point.Y + 2), new Point(point.X, point.Y - 2) })
                {
                    if (next.X < scanLeft || next.X >= scanRight || next.Y < scanTop || next.Y >= scanBottom || visited[next.X, next.Y]) continue;
                    visited[next.X, next.Y] = true;
                    if (IsBadgeRed(bitmap.GetPixel(next.X, next.Y))) queue.Enqueue(next);
                }
            }
            var width = maxX - minX + 2;
            var height = maxY - minY + 2;
            var bounds = new Rectangle(minX, minY, width, height);
            if (pixels >= 8 && width is >= 8 and <= 70 && height is >= 8 and <= 40 && HasWhiteBadgeDigits(bitmap, bounds))
            {
                var count = await ReadBadgeCountAsync(bitmap, bounds, cancellationToken);
                if (count is > 0) badges.Add(new UnreadBadge(bounds, count.Value));
            }
        }
        return badges.OrderBy(badge => badge.Bounds.Top).ToArray();
    }

    internal static string? FindConversationName(IReadOnlyList<OcrTextLine> lines, UnreadBadge badge, int paneRight)
    {
        var centerY = badge.Bounds.Top + badge.Bounds.Height / 2f;
        return lines.Where(line => line.Bounds.Left < paneRight - 52 && Math.Abs(line.Bounds.Top + line.Bounds.Height / 2f - centerY) < 13)
            .Where(line => line.Bounds.Left >= 112)
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left)
            .Select(line => line.Text).FirstOrDefault(IsUsefulConversationName);
    }

    internal static string? FindConversationPreview(IReadOnlyList<OcrTextLine> lines, UnreadBadge badge, int paneRight, string name)
    {
        var centerY = badge.Bounds.Top + badge.Bounds.Height / 2f;
        return lines
            .Where(line => line.Bounds.Left >= 112 && line.Bounds.Right < paneRight - 42)
            .Where(line => line.Bounds.Top + line.Bounds.Height / 2f >= centerY + 13 && line.Bounds.Top + line.Bounds.Height / 2f <= centerY + 36)
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left)
            .Select(line => line.Text)
            .FirstOrDefault(text => IsUsefulPreview(text) && !text.Equals(name, StringComparison.Ordinal));
    }

    internal static async Task<ConversationRowText> ReadConversationRowAsync(Bitmap bitmap, UnreadBadge badge, int paneRight, CancellationToken cancellationToken)
    {
        var centerY = badge.Bounds.Top + badge.Bounds.Height / 2;
        var textRight = Math.Max(150, paneRight - 48);
        var width = Math.Max(1, textRight - 116);
        var name = await ReadBinaryLineAsync(bitmap, new Rectangle(116, Math.Max(72, centerY - 11), width, 24), 170, cancellationToken);
        var preview = await ReadBinaryLineAsync(bitmap, new Rectangle(116, centerY + 12, width, 25), 205, cancellationToken);
        return new ConversationRowText(name, preview);
    }

    internal static int? FindSelectedConversationY(Bitmap bitmap, int paneRight)
    {
        for (var y = 85; y < bitmap.Height - 40; y += 4)
        {
            var matching = 0;
            for (var x = 70; x < paneRight - 10; x += 12)
            {
                var color = bitmap.GetPixel(x, y);
                if (Math.Abs(color.R - color.G) < 8 && Math.Abs(color.G - color.B) < 8 && color.R is >= 205 and <= 238) matching++;
            }
            if (matching >= 8) return y;
        }
        return null;
    }

    internal static async Task<IReadOnlyList<string>> ReadLatestConversationMessagesAsync(
        NativeWeChatWindow window,
        int paneRight,
        UnreadBadge badge,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var rowY = badge.Bounds.Top + badge.Bounds.Height / 2 + 8;
        await ClickWindowPointAsync(window.Handle, Math.Min(paneRight - 35, 175), rowY, cancellationToken);
        await Task.Delay(500, cancellationToken);
        using var image = Capture(window.Handle);
        var chatRegion = new Rectangle(
            paneRight + 18,
            78,
            Math.Max(1, image.Width - paneRight - 36),
            Math.Max(1, image.Height - 235));
        var lines = await ReadTextAsync(image, chatRegion, 3, cancellationToken);
        return ExtractLatestIncomingMessages(lines, paneRight, image.Width, expectedCount);
    }

    internal static async Task SelectConversationAsync(NativeWeChatWindow window, int paneRight, int rowY, CancellationToken cancellationToken)
    {
        await ClickWindowPointAsync(window.Handle, Math.Min(paneRight - 35, 175), rowY, cancellationToken);
        await Task.Delay(250, cancellationToken);
    }

    internal static async Task<string?> ReadProfileNicknameAsync(NativeWeChatWindow window, CancellationToken cancellationToken)
    {
        await PrepareWindowAsync(window, cancellationToken);
        await ClickWindowPointAsync(window.Handle, 31, 63, cancellationToken);
        try
        {
            await Task.Delay(500, cancellationToken);
            using var image = CaptureScreen(window.Handle);
            var cardRegion = new Rectangle(55, 42, Math.Min(305, Math.Max(1, image.Width - 55)), Math.Min(285, Math.Max(1, image.Height - 42)));
            var lines = await ReadTextAsync(image, cardRegion, 4, cancellationToken);
            if (!lines.Any(line => line.Text.Contains("微信号", StringComparison.Ordinal) || line.Text.Contains("朋友圈", StringComparison.Ordinal))) return null;
            return lines
                .Where(line => line.Bounds.Top is >= 52 and <= 115 && line.Bounds.Left >= 125)
                .OrderBy(line => line.Bounds.Top)
                .ThenBy(line => line.Bounds.Left)
                .Select(line => CleanProfileNickname(line.Text))
                .FirstOrDefault(IsUsefulProfileNickname);
        }
        finally
        {
            await ClickWindowPointAsync(window.Handle, 31, 63, CancellationToken.None);
            await Task.Delay(350, CancellationToken.None);
            RedrawWindow(window.Handle, IntPtr.Zero, IntPtr.Zero, RedrawInvalidate | RedrawUpdateNow | RedrawFrame | RedrawAllChildren);
            await Task.Delay(250, CancellationToken.None);
        }
    }

    internal static void RestoreWindow(IntPtr handle, WindowPlacement placement)
    {
        placement.Length = Marshal.SizeOf<WindowPlacement>();
        SetWindowPlacement(handle, ref placement);
        if (placement.ShowCommand is 2 or 6 or 7 or 11) ShowWindowAsync(handle, SwMinimize);
    }
    internal static IntPtr GetForeground() => GetForegroundWindow();
    internal static void RestoreForeground(IntPtr handle) { if (handle != IntPtr.Zero) SetForegroundWindow(handle); }

    private static bool LooksLikeMainWindow(string title, string className) =>
        (title.Equals("微信", StringComparison.OrdinalIgnoreCase) || title.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || title.Equals("Weixin", StringComparison.OrdinalIgnoreCase) || title.Contains("微信", StringComparison.OrdinalIgnoreCase)) &&
        !title.Contains("图片") && !title.Contains("登录") && !title.Contains("TrayIcon", StringComparison.OrdinalIgnoreCase) && !className.Contains("Chrome", StringComparison.OrdinalIgnoreCase);
    private static bool IsBadgeRed(Color color) => color.R >= 205 && color.G <= 95 && color.B <= 95 && color.R - color.G >= 120;
    private static bool HasWhiteBadgeDigits(Bitmap bitmap, Rectangle bounds)
    {
        if (bounds.Width < 11 || bounds.Height < 11) return false;
        var left = Math.Max(0, bounds.Left);
        var right = Math.Min(bitmap.Width, bounds.Right);
        var top = Math.Max(0, bounds.Top);
        var bottom = Math.Min(bitmap.Height, bounds.Bottom);
        var brightPixels = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.R >= 220 && color.G >= 220 && color.B >= 220) brightPixels++;
        }
        return brightPixels >= 8;
    }
    private static async Task<int?> ReadBadgeCountAsync(Bitmap bitmap, Rectangle bounds, CancellationToken cancellationToken)
    {
        const int padding = 4;
        using var mask = new Bitmap(bounds.Width + padding * 2, bounds.Height + padding * 2, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(mask)) graphics.Clear(Color.White);
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var sourceX = bounds.Left + x;
            var sourceY = bounds.Top + y;
            if (sourceX < 0 || sourceX >= bitmap.Width || sourceY < 0 || sourceY >= bitmap.Height) continue;
            var color = bitmap.GetPixel(sourceX, sourceY);
            var difference = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
            if (color.R >= 155 && color.G >= 155 && color.B >= 155 && difference <= 55)
            {
                mask.SetPixel(x + padding, y + padding, Color.Black);
            }
        }
        foreach (var scale in new[] { 8, 12 })
        {
            var lines = await ReadTextAsync(mask, new Rectangle(0, 0, mask.Width, mask.Height), scale, cancellationToken);
            var digits = string.Concat(lines.SelectMany(line => line.Text).Where(char.IsDigit));
            if (int.TryParse(digits, out var count) && count is > 0 and <= 999) return count;
        }
        return EstimateBadgeCount(bounds.Width, bounds.Height);
    }
    private static bool IsConversationBackground(Color color) => color.R is >= 218 and <= 245 && Math.Abs(color.R - color.G) <= 5 && Math.Abs(color.G - color.B) <= 5;
    private static bool IsChatBackground(Color color) => color.R >= 245 && color.G >= 245 && color.B >= 245;
    private static int EstimateBadgeCount(int width, int height) => width <= height + 5 ? 1 : Math.Clamp((width - height / 2) / Math.Max(7, height / 2), 2, 99);
    private static bool IsUsefulConversationName(string text) => text.Length is > 0 and < 48 && text.Any(character => char.IsLetter(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.OtherLetter) && !text.Contains("昨天") && !text.Contains("上午") && !text.Contains("下午");
    private static bool IsUsefulRowText(string text) => text.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(text, "^[\\d:：,，.。·•丨|/\\\\↑↓↗↘←→]+$");
    private static bool IsUsefulPreview(string text) => IsUsefulRowText(text) && text.Length < 120 && text.Any(character => char.IsLetterOrDigit(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.OtherLetter);
    internal static string CleanConversationPreview(string text)
    {
        var value = NormalizeText(text);
        value = System.Text.RegularExpressions.Regex.Replace(value, "^[\\[【(（]?[0-9一二三四五六七八九十百]+(?:条|景|到|则|个)?[\\]】)）]?", string.Empty);
        value = System.Text.RegularExpressions.Regex.Replace(value, "^[:：,，.。·•丨|]+", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? text : value;
    }
    private static IReadOnlyList<string> ExtractLatestIncomingMessages(IReadOnlyList<OcrTextLine> lines, int paneRight, int imageWidth, int expectedCount)
    {
        var incomingRight = paneRight + (imageWidth - paneRight) * 0.6f;
        var candidates = lines
            .Where(line => line.Bounds.Left < incomingRight)
            .Where(line => IsUsefulChatMessage(line.Text))
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToArray();
        var groups = new List<List<OcrTextLine>>();
        foreach (var line in candidates)
        {
            var current = groups.LastOrDefault();
            if (current is null || line.Bounds.Top - current.Max(item => item.Bounds.Bottom) > 28)
            {
                current = [];
                groups.Add(current);
            }
            current.Add(line);
        }
        return groups
            .Select(group => string.Join(" ", group.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).Select(line => line.Text)))
            .Select(CleanConversationPreview)
            .Where(IsUsefulChatMessage)
            .TakeLast(Math.Clamp(expectedCount, 1, 20))
            .ToArray();
    }
    private static bool IsUsefulChatMessage(string text) =>
        IsUsefulPreview(text) &&
        !System.Text.RegularExpressions.Regex.IsMatch(text, "^(?:\\d{1,2}:\\d{2}|昨天|今天|星期.|\\d{1,2}月\\d{1,2}日.*)$") &&
        text is not "发送" and not "发消息" and not "查看更多消息";
    private static bool IsUsefulProfileNickname(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 30 &&
        !text.Contains("微信号", StringComparison.Ordinal) &&
        !text.Contains("地区", StringComparison.Ordinal) &&
        !text.Contains("朋友圈", StringComparison.Ordinal) &&
        !text.Contains("发消息", StringComparison.Ordinal);
    private static string? CleanProfileNickname(string text)
    {
        var value = NormalizeText(text);
        foreach (var label in new[] { "微信号", "地区", "朋友圈", "发消息" })
        {
            var index = value.IndexOf(label, StringComparison.Ordinal);
            if (index >= 0) value = value[..index];
        }
        value = value.Trim('：', ':', '-', '—', '_', '|');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private static string NormalizeText(string text) => string.Join(string.Empty, text.Where(character => !char.IsWhiteSpace(character) && character != '�')).Trim();
    private static Bitmap Binarize(Bitmap source, int threshold)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var gray = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            var value = gray < threshold ? 0 : 255;
            result.SetPixel(x, y, Color.FromArgb(255, value, value, value));
        }
        return result;
    }
    private static async Task<string?> ReadBinaryLineAsync(Bitmap bitmap, Rectangle region, int threshold, CancellationToken cancellationToken)
    {
        region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0) return null;
        using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        using var binary = Binarize(cropped, threshold);
        var lines = await ReadTextAsync(binary, new Rectangle(0, 0, binary.Width, binary.Height), 5, cancellationToken);
        return lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).Select(line => line.Text).FirstOrDefault(IsUsefulRowText);
    }
    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }
    private static string ReadWindowText(IntPtr handle) { var value = new StringBuilder(GetWindowTextLengthW(handle) + 1); GetWindowTextW(handle, value, value.Capacity); return value.ToString(); }
    private static string ReadClassName(IntPtr handle) { var value = new StringBuilder(256); GetClassNameW(handle, value, value.Capacity); return value.ToString(); }
    private static async Task ClickWindowPointAsync(IntPtr handle, int x, int y, CancellationToken cancellationToken)
    {
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("微信窗口已关闭。");
        var previous = GetCursorPos(out var point) ? point : (Point?)null;
        SetCursorPos(rect.Left + x, rect.Top + y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        if (previous is { } cursor) SetCursorPos(cursor.X, cursor.Y);
        await Task.Delay(120, cancellationToken);
    }
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr handle, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr handle, IntPtr updateRect, IntPtr updateRegion, uint flags);
}

internal sealed record NativeWeChatWindow(IntPtr Handle, int ProcessId, string ExecutablePath, DateTime ProcessStartedAt, string Title, string ClassName, bool Minimized, NativeRect Bounds);
internal sealed record OcrTextLine(string Text, RectangleF Bounds);
internal sealed record UnreadBadge(Rectangle Bounds, int EstimatedCount);
internal sealed record ConversationRowText(string? Name, string? Preview);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int ShowCommand;
    public NativePoint MinPosition;
    public NativePoint MaxPosition;
    public NativeRect NormalPosition;
    public static WindowPlacement Create() => new() { Length = Marshal.SizeOf<WindowPlacement>() };
}
