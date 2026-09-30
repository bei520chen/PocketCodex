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
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint RedrawInvalidate = 0x0001;
    private const uint RedrawUpdateNow = 0x0100;
    private const uint RedrawFrame = 0x0400;
    private const uint RedrawAllChildren = 0x0080;
    private const byte VkControl = 0x11;
    private const byte VkV = 0x56;
    private const byte VkEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;

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
        // 普通界面文字优先使用本地 PP-OCRv5，模型不可用时再回退到系统 OCR。
        var paddleLines = await PaddleTextRecognizer.TryReadTextAsync(bitmap, region, scale, cancellationToken);
        return paddleLines is { Count: > 0 } ? paddleLines : await ReadLegacyTextAsync(bitmap, region, scale, cancellationToken);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> ReadMessageTextAsync(Bitmap bitmap, Rectangle region, int scale, CancellationToken cancellationToken)
    {
        // 消息正文采用三级降级：PP-OCRv5 -> Windows AI OCR -> Windows.Media.Ocr。
        var paddleLines = await PaddleTextRecognizer.TryReadTextAsync(bitmap, region, scale, cancellationToken);
        if (paddleLines is { Count: > 0 }) return paddleLines;
        var aiLines = await WindowsAiTextRecognizer.TryReadTextAsync(bitmap, region, scale, cancellationToken);
        return aiLines is { Count: > 0 } ? aiLines : await ReadLegacyTextAsync(bitmap, region, scale, cancellationToken);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> ReadLegacyTextAsync(Bitmap bitmap, Rectangle region, int scale, CancellationToken cancellationToken)
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
            return new OcrTextLine(NormalizeText(line.Text), new RectangleF(region.Left + (float)left / scale, region.Top + (float)top / scale, (float)(right - left) / scale, (float)(bottom - top) / scale), 1);
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

    internal static bool HasUnreadIndicator(Bitmap bitmap, int paneRight, int rowY)
    {
        var left = Math.Max(70, paneRight - 48);
        var right = Math.Min(bitmap.Width, paneRight - 6);
        var top = Math.Max(72, rowY - 28);
        var bottom = Math.Min(bitmap.Height, rowY + 28);
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (IsBadgeRed(bitmap.GetPixel(x, y))) return true;
        }
        return false;
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

    internal static async Task<IReadOnlyList<string>> ReadConversationContactsAsync(Bitmap bitmap, int paneRight, CancellationToken cancellationToken)
    {
        var rows = await ReadConversationContactRowsAsync(bitmap, paneRight, cancellationToken);
        return rows.Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static async Task<IReadOnlyList<ConversationContactRow>> ReadConversationContactRowsAsync(Bitmap bitmap, int paneRight, CancellationToken cancellationToken)
    {
        var textLeft = Math.Min(116, Math.Max(78, paneRight - 150));
        var region = new Rectangle(textLeft, 74, Math.Max(1, paneRight - textLeft - 34), Math.Max(1, bitmap.Height - 110));
        var lines = await ReadTextAsync(bitmap, region, 2, cancellationToken);
        var result = new List<ConversationContactRow>();
        // 不使用固定首行和固定行高，兼容普通窗口与高 DPI 长窗口。
        foreach (var nameCenterY in FindConversationRowCenters(bitmap, paneRight))
        {
            var name = lines
                .Where(line => line.Bounds.Left >= textLeft && line.Bounds.Right < paneRight - 30)
                .Where(line => Math.Abs(line.Bounds.Top + line.Bounds.Height / 2f - nameCenterY) <= 14)
                .OrderBy(line => Math.Abs(line.Bounds.Top + line.Bounds.Height / 2f - nameCenterY))
                .ThenBy(line => line.Bounds.Left)
                .Select(line => CleanConversationName(line.Text))
                .FirstOrDefault(IsUsefulContactName);
            if (!IsCompleteConversationName(name))
            {
                var rowName = await ReadContactNameAsync(bitmap, textLeft, paneRight, nameCenterY, cancellationToken);
                name = SelectContactName(name, rowName);
            }
            if (!string.IsNullOrWhiteSpace(name)) result.Add(new ConversationContactRow(name, nameCenterY + 3));
        }
        return result;
    }

    private static IReadOnlyList<int> FindConversationRowCenters(Bitmap bitmap, int paneRight)
    {
        // 头像区域比文字区域更稳定，通过连续的非背景像素带定位每个真实会话行中心。
        var left = Math.Min(82, Math.Max(70, paneRight - 195));
        var right = Math.Min(paneRight - 135, left + 38);
        if (right <= left) return [];
        var activeRows = new List<int>();
        for (var y = 116; y < bitmap.Height - 55; y++)
        {
            var matching = 0;
            for (var x = left; x < right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
                if (maximum - minimum >= 18 || minimum <= 185) matching++;
            }
            if (matching >= 5) activeRows.Add(y);
        }
        var groups = new List<List<int>>();
        foreach (var y in activeRows)
        {
            var current = groups.LastOrDefault();
            if (current is null || y - current[^1] > 3)
            {
                current = [];
                groups.Add(current);
            }
            current.Add(y);
        }
        return groups
            .Where(group => group.Count is >= 18 and <= 48)
            .Select(group => (group[0] + group[^1]) / 2)
            .Where(center => center is >= 125 && center < bitmap.Height - 60)
            .ToArray();
    }

    private static async Task<string?> ReadContactNameAsync(Bitmap bitmap, int textLeft, int paneRight, int nameCenterY, CancellationToken cancellationToken)
    {
        // 左侧小字号名称只作为候选，使用原图、增强图和多个二值阈值投票降低形近字误识别。
        var region = new Rectangle(textLeft, Math.Max(74, nameCenterY - 18), Math.Max(1, paneRight - textLeft - 28), 36);
        region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0) return null;
        using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        using var enhanced = EnhanceText(cropped);
        using var binary155 = Binarize(cropped, 155);
        using var binary180 = Binarize(cropped, 180);
        using var binary205 = Binarize(cropped, 205);
        var candidates = new List<string>();
        foreach (var source in new[] { cropped, enhanced, binary155, binary180, binary205 })
        {
            var lines = await ReadLegacyTextAsync(source, new Rectangle(0, 0, source.Width, source.Height), 6, cancellationToken);
            var text = string.Join(string.Empty, lines
                .OrderBy(line => line.Bounds.Top)
                .ThenBy(line => line.Bounds.Left)
                .Select(line => line.Text));
            text = CleanConversationName(text);
            if (IsUsefulContactName(text)) candidates.Add(text);
        }
        return SelectContactName(candidates.ToArray());
    }

    private static string? SelectContactName(params string?[] candidates)
    {
        var values = candidates
            .Where(IsUsefulContactName)
            .Select(value => CleanConversationName(value!))
            .Where(IsUsefulContactName)
            .ToArray();
        if (values.Length == 0) return null;
        var repeated = values.GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .First();
        if (repeated.Count() >= 2) return repeated.Key;
        return values.Distinct(StringComparer.Ordinal)
            .Select(value => new
            {
                Value = value,
                Agreement = values.Where(other => !other.Equals(value, StringComparison.Ordinal)).DefaultIfEmpty(value).Average(other => TextSimilarity(value, other)),
                Noise = value.Count(character => "丨|·•，,。.!！?？【】[]()（）".Contains(character))
            })
            .OrderByDescending(item => item.Agreement)
            .ThenBy(item => item.Noise)
            .ThenByDescending(item => item.Value.Length)
            .Select(item => item.Value)
            .First();
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

    internal static async Task<ConversationMessageReadResult> ReadLatestConversationMessagesAsync(
        NativeWeChatWindow window,
        int paneRight,
        UnreadBadge badge,
        string expectedConversationName,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var rowY = badge.Bounds.Top + badge.Bounds.Height / 2 + 8;
        string? title = null;
        // 微信可能尚未完成切换或第一次点击未生效，最多重试三次并以顶部标题确认目标会话。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ClickWindowPointAsync(window.Handle, Math.Min(paneRight - 35, 175), rowY, cancellationToken);
            await Task.Delay(350 + attempt * 200, cancellationToken);
            title = await ReadCurrentChatTitleAsync(window, paneRight, cancellationToken);
            if (!string.IsNullOrWhiteSpace(title) && ChatTitleMatchesExpected(title, expectedConversationName)) break;
        }
        if (string.IsNullOrWhiteSpace(title) || !ChatTitleMatchesExpected(title, expectedConversationName))
        {
            throw new InvalidOperationException($"微信聊天标题校验失败，目标为“{expectedConversationName}”，当前识别为“{title ?? "未知"}”。");
        }
        using var image = Capture(window.Handle);
        var chatRegion = new Rectangle(
            paneRight + 18,
            78,
            Math.Max(1, image.Width - paneRight - 36),
            Math.Max(1, image.Height - 235));
        var lines = await ReadTextAsync(image, chatRegion, 4, cancellationToken);
        var messages = await ExtractLatestIncomingMessagesAsync(window.Handle, image, lines, paneRight, expectedCount, cancellationToken);
        return new ConversationMessageReadResult(title, messages);
    }

    internal static async Task SelectConversationAsync(NativeWeChatWindow window, int paneRight, int rowY, CancellationToken cancellationToken)
    {
        await ClickWindowPointAsync(window.Handle, Math.Min(paneRight - 35, 175), rowY, cancellationToken);
        await Task.Delay(250, cancellationToken);
    }

    internal static async Task<string?> ReadCurrentChatTitleAsync(NativeWeChatWindow window, int paneRight, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(250, cancellationToken);
            using var image = attempt < 3 ? Capture(window.Handle) : CaptureScreen(window.Handle);
            var region = new Rectangle(paneRight, 28, Math.Max(1, image.Width - paneRight - 90), 76);
            var lines = await ReadTextAsync(image, region, 4, cancellationToken);
            var title = lines
                .Where(line => line.Bounds.Left >= paneRight + 5 && line.Bounds.Top < 88)
                .OrderBy(line => line.Bounds.Top)
                .ThenBy(line => line.Bounds.Left)
                .Select(line => CleanConversationName(line.Text))
                .FirstOrDefault(IsUsefulContactName);
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        return null;
    }

    internal static bool ChatTitleMatches(string title, string contact)
    {
        static string NormalizeTitle(string value) => System.Text.RegularExpressions.Regex.Replace(
            NormalizeText(value),
            "[（(]\\d+[）)]$",
            string.Empty).Trim('"', '“', '”', '。', '.', '，', ',');
        return NormalizeTitle(title).Equals(NormalizeTitle(contact), StringComparison.Ordinal);
    }

    private static bool ChatTitleMatchesExpected(string title, string expected)
    {
        if (ChatTitleMatches(title, expected)) return true;
        // 左侧列表可能以省略号截断名称，仅允许“明确被截断的前缀”匹配完整顶部标题。
        var value = CleanConversationName(expected);
        var prefix = value.TrimEnd('…', '.');
        return prefix.Length >= 2 && value.Length != prefix.Length && CleanConversationName(title).StartsWith(prefix, StringComparison.Ordinal);
    }

    internal static async Task SendTextMessageAsync(NativeWeChatWindow window, string message, CancellationToken cancellationToken)
    {
        using var image = Capture(window.Handle);
        await ClickWindowPointAsync(window.Handle, Math.Max(320, image.Width - 360), Math.Max(300, image.Height - 105), cancellationToken);
        await SetClipboardTextAsync(message, cancellationToken);
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkV, 0, 0, UIntPtr.Zero);
        keybd_event(VkV, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
        await Task.Delay(180, cancellationToken);
        await ClickWindowPointAsync(window.Handle, Math.Max(320, image.Width - 55), Math.Max(300, image.Height - 43), cancellationToken);
        await Task.Delay(350, cancellationToken);
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
            var lines = await ReadLegacyTextAsync(image, cardRegion, 4, cancellationToken);
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
            var lines = await ReadLegacyTextAsync(mask, new Rectangle(0, 0, mask.Width, mask.Height), scale, cancellationToken);
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
    private static string CleanConversationName(string text)
    {
        var value = NormalizeText(text).Trim('、', '，', ',', '.', '。', '“', '”', '"', '丨', '|');
        value = System.Text.RegularExpressions.Regex.Replace(value, "^(?:[0-9]+|[\\[【(（][0-9]+[\\]】)）])", string.Empty);
        return value.Trim('、', '，', ',', '.', '。', '“', '”', '"', '丨', '|');
    }
    internal static bool IsCompleteConversationName(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.EndsWith("…", StringComparison.Ordinal) &&
        !text.EndsWith("...", StringComparison.Ordinal) &&
        !text.EndsWith("..", StringComparison.Ordinal);
    internal static bool IsSpecialConversationEntry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = CleanConversationName(text);
        // 这些项目是聚合入口或系统会话，点击后无法安全映射到一个普通联系人，因此统一排除。
        return new[]
        {
            "服务号", "公众号", "订阅号消息", "折叠的聊天", "折叠的群聊",
            "微信支付", "腾讯新闻", "文件传输助手"
        }.Any(keyword =>
            value.Contains(keyword, StringComparison.Ordinal) ||
            Math.Abs(value.Length - keyword.Length) <= 1 && TextSimilarity(value, keyword) >= 0.72);
    }
    private static bool IsUsefulContactName(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Length is >= 1 and <= 48 &&
        text.Any(character => char.IsLetterOrDigit(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.OtherLetter) &&
        !System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9:：,，.。·•丨|/\\\\]+$") &&
        text is not "搜索" and not "文件传输助手";
    private static async Task<IReadOnlyList<string>> ExtractLatestIncomingMessagesAsync(
        IntPtr windowHandle,
        Bitmap image,
        IReadOnlyList<OcrTextLine> lines,
        int paneRight,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var incomingRight = paneRight + (image.Width - paneRight) * 0.6f;
        // 收到的消息通常位于聊天区左侧，先排除右侧自己发送的气泡和时间等无效文字。
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
            if (current is null || line.Bounds.Top - current.Max(item => item.Bounds.Bottom) > 10)
            {
                current = [];
                groups.Add(current);
            }
            current.Add(line);
        }
        // OCR 可能把多个相邻气泡合并；依据红色未读数字继续按垂直空隙拆分。
        SplitMessageGroups(groups, Math.Clamp(expectedCount, 1, 20));
        var selectedGroups = groups.TakeLast(Math.Clamp(expectedCount, 1, 20)).ToArray();
        var messages = new List<string>(selectedGroups.Length);
        foreach (var group in selectedGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawText = JoinChatLines(group);
            var bounds = GetMessageGroupBounds(group, paneRight, image.Width, image.Height);
            var copiedText = await TryCopyMessageTextAsync(windowHandle, image, group, cancellationToken);
            if (!string.IsNullOrWhiteSpace(copiedText) && IsUsefulChatMessage(copiedText))
            {
                messages.Add(CleanConversationPreview(copiedText));
                continue;
            }
            var variants = await ReadMessageVariantsAsync(image, bounds, rawText, cancellationToken);
            var message = SelectConsensusText(variants);
            if (IsUsefulChatMessage(message)) messages.Add(message);
        }
        return messages;
    }
    private static void SplitMessageGroups(List<List<OcrTextLine>> groups, int expectedCount)
    {
        // 只在存在可确认空隙时拆分，不为了凑数量强行切开一条真正的多行消息。
        while (groups.Count < expectedCount)
        {
            var split = groups
                .Select((group, groupIndex) => new
                {
                    GroupIndex = groupIndex,
                    SplitIndex = FindBestSplitIndex(group),
                    Score = FindBestSplitScore(group)
                })
                .Where(item => item.SplitIndex > 0)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (split is null) break;
            var group = groups[split.GroupIndex];
            var remainder = group.Skip(split.SplitIndex).ToList();
            group.RemoveRange(split.SplitIndex, group.Count - split.SplitIndex);
            groups.Insert(split.GroupIndex + 1, remainder);
        }
    }
    private static int FindBestSplitIndex(IReadOnlyList<OcrTextLine> group)
    {
        if (group.Count < 2) return -1;
        var bestIndex = 1;
        var bestScore = float.MinValue;
        for (var index = 1; index < group.Count; index++)
        {
            var score = group[index].Bounds.Top - group[index - 1].Bounds.Bottom;
            if (score <= bestScore) continue;
            bestScore = score;
            bestIndex = index;
        }
        return bestScore >= 4 ? bestIndex : -1;
    }
    private static float FindBestSplitScore(IReadOnlyList<OcrTextLine> group)
    {
        var index = FindBestSplitIndex(group);
        return index <= 0 ? float.MinValue : group[index].Bounds.Top - group[index - 1].Bounds.Bottom;
    }
    private static async Task<string?> TryCopyMessageTextAsync(IntPtr windowHandle, Bitmap image, IReadOnlyList<OcrTextLine> group, CancellationToken cancellationToken)
    {
        // 普通文本优先使用微信右键“复制”获取原文；菜单或剪贴板校验失败时返回 null 走 OCR。
        var target = group.OrderByDescending(line => line.Bounds.Width).ThenByDescending(line => line.Bounds.Top).First();
        var clickX = Math.Clamp((int)Math.Round(target.Bounds.Left + target.Bounds.Width / 2), 1, image.Width - 2);
        var clickY = Math.Clamp((int)Math.Round(target.Bounds.Top + target.Bounds.Height / 2), 1, image.Height - 2);
        ClipboardSnapshot? clipboard;
        try { clipboard = await CaptureClipboardAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        var sequence = GetClipboardSequenceNumber();
        try
        {
            await ClickWindowPointAsync(windowHandle, clickX, clickY, MouseEventRightDown, MouseEventRightUp, cancellationToken);
            await Task.Delay(180, cancellationToken);
            using var menuImage = CaptureScreen(windowHandle);
            var menuRegion = new Rectangle(
                Math.Max(0, clickX - 35),
                Math.Max(0, clickY - 45),
                Math.Min(300, menuImage.Width - Math.Max(0, clickX - 35)),
                Math.Min(390, menuImage.Height - Math.Max(0, clickY - 45)));
            var menuLines = await ReadLegacyTextAsync(menuImage, menuRegion, 4, cancellationToken);
            var copyLines = menuLines.Where(line => NormalizeText(line.Text).Equals("复制", StringComparison.Ordinal)).ToArray();
            // 同时识别到其他微信菜单项，才能确认“复制”来自右键菜单而不是聊天正文。
            var hasMenuContext = menuLines.Any(line => new[] { "转发", "收藏", "删除", "引用", "多选", "搜一搜" }.Contains(NormalizeText(line.Text), StringComparer.Ordinal));
            if (copyLines.Length != 1 || !hasMenuContext)
            {
                PressKey(VkEscape);
                return null;
            }
            var copy = copyLines[0].Bounds;
            await ClickWindowPointAsync(windowHandle, (int)Math.Round(copy.Left + copy.Width / 2), (int)Math.Round(copy.Top + copy.Height / 2), cancellationToken);
            await Task.Delay(180, cancellationToken);
            // 序列号未变化说明复制动作没有成功，不能误用用户剪贴板中的旧内容。
            if (GetClipboardSequenceNumber() == sequence) return null;
            var copiedText = await ReadClipboardTextAsync(cancellationToken);
            return !string.IsNullOrWhiteSpace(copiedText) && IsUsefulChatMessage(copiedText) ? copiedText : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            PressKey(VkEscape);
            return null;
        }
        finally
        {
            PressKey(VkEscape);
            try { await RestoreClipboardAsync(clipboard, CancellationToken.None); }
            catch { }
        }
    }
    private static string JoinChatLines(IEnumerable<OcrTextLine> lines) => CleanConversationPreview(string.Join(" ", lines
        .Where(line => IsUsefulChatMessage(line.Text))
        .OrderBy(line => line.Bounds.Top)
        .ThenBy(line => line.Bounds.Left)
        .Select(line => line.Text)));
    private static Rectangle GetMessageGroupBounds(IReadOnlyList<OcrTextLine> group, int paneRight, int imageWidth, int imageHeight)
    {
        var top = Math.Max(78, (int)Math.Floor(group.Min(line => line.Bounds.Top)) - 12);
        var bottom = Math.Min(imageHeight - 225, (int)Math.Ceiling(group.Max(line => line.Bounds.Bottom)) + 14);
        var left = Math.Max(paneRight + 8, (int)Math.Floor(group.Min(line => line.Bounds.Left)) - 14);
        var incomingRight = paneRight + (int)((imageWidth - paneRight) * 0.64f);
        var right = Math.Min(incomingRight, Math.Max(left + 80, (int)Math.Ceiling(group.Max(line => line.Bounds.Right)) + 32));
        return Rectangle.FromLTRB(left, top, right, Math.Max(top + 24, bottom));
    }
    private static async Task<IReadOnlyList<string>> ReadMessageVariantsAsync(
        Bitmap image,
        Rectangle bounds,
        string rawText,
        CancellationToken cancellationToken)
    {
        bounds.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        if (bounds.Width <= 0 || bounds.Height <= 0) return [rawText];
        using var cropped = image.Clone(bounds, PixelFormat.Format32bppArgb);
        using var enhanced = EnhanceText(cropped);
        using var binary175 = Binarize(cropped, 175);
        using var binary205 = Binarize(cropped, 205);
        var variants = new List<string> { rawText };
        foreach (var source in new[] { cropped, enhanced, binary175, binary205 })
        {
            var lines = await ReadTextAsync(source, new Rectangle(0, 0, source.Width, source.Height), 5, cancellationToken);
            var text = JoinChatLines(lines);
            if (IsUsefulChatMessage(text)) variants.Add(text);
        }
        return variants;
    }
    private static string SelectConsensusText(IReadOnlyList<string> variants)
    {
        var candidates = variants
            .Select(CleanConversationPreview)
            .Where(IsUsefulChatMessage)
            .ToArray();
        if (candidates.Length == 0) return "[无法识别的消息]";
        var groups = candidates.GroupBy(candidate => candidate, StringComparer.Ordinal).ToArray();
        var repeated = groups.OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key.Length).First();
        if (repeated.Count() >= 2) return repeated.Key;
        if (groups.Length == 1) return repeated.Key;
        var ranked = groups
            .Select(group => new
            {
                Text = group.Key,
                Agreement = groups.Where(other => !other.Key.Equals(group.Key, StringComparison.Ordinal)).Average(other => TextSimilarity(group.Key, other.Key))
            })
            .OrderByDescending(item => item.Agreement)
            .ThenByDescending(item => item.Text.Length)
            .ToArray();
        var best = ranked[0];
        return best.Agreement >= 0.72 ? best.Text : $"{best.Text} [部分文字可能识别有误]";
    }
    private static double TextSimilarity(string left, string right)
    {
        if (left.Equals(right, StringComparison.Ordinal)) return 1;
        var maximumLength = Math.Max(left.Length, right.Length);
        return maximumLength == 0 ? 1 : 1d - (double)LevenshteinDistance(left, right) / maximumLength;
    }
    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
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
    private static Bitmap EnhanceText(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var gray = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            var contrasted = Math.Clamp((gray - 128) * 2 + 128, 0, 255);
            result.SetPixel(x, y, Color.FromArgb(255, contrasted, contrasted, contrasted));
        }
        return result;
    }
    private static async Task<string?> ReadBinaryLineAsync(Bitmap bitmap, Rectangle region, int threshold, CancellationToken cancellationToken)
    {
        region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0) return null;
        using var cropped = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        using var binary = Binarize(cropped, threshold);
        var lines = await ReadLegacyTextAsync(binary, new Rectangle(0, 0, binary.Width, binary.Height), 5, cancellationToken);
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
        => await ClickWindowPointAsync(handle, x, y, MouseEventLeftDown, MouseEventLeftUp, cancellationToken);
    private static async Task ClickWindowPointAsync(IntPtr handle, int x, int y, uint downFlag, uint upFlag, CancellationToken cancellationToken)
    {
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("微信窗口已关闭。");
        var previous = GetCursorPos(out var point) ? point : (Point?)null;
        SetCursorPos(rect.Left + x, rect.Top + y);
        mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
        mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
        if (previous is { } cursor) SetCursorPos(cursor.X, cursor.Y);
        await Task.Delay(120, cancellationToken);
    }
    private static void PressKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }
    private static Task<ClipboardSnapshot> CaptureClipboardAsync(CancellationToken cancellationToken) => RunStaAsync(() =>
    {
        var data = System.Windows.Forms.Clipboard.GetDataObject();
        return new ClipboardSnapshot(data, data is not null);
    }, cancellationToken);
    private static Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken) => RunStaAsync(() =>
        System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null, cancellationToken);
    private static Task RestoreClipboardAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) => RunStaAsync(() =>
    {
        if (snapshot.HasData && snapshot.Data is not null) System.Windows.Forms.Clipboard.SetDataObject(snapshot.Data, true);
        else System.Windows.Forms.Clipboard.Clear();
        return true;
    }, cancellationToken);
    private static Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
        => RunStaAsync(() =>
        {
            System.Windows.Forms.Clipboard.SetText(text);
            return true;
        }, cancellationToken);
    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
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
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr handle, IntPtr updateRect, IntPtr updateRegion, uint flags);
}

internal sealed record NativeWeChatWindow(IntPtr Handle, int ProcessId, string ExecutablePath, DateTime ProcessStartedAt, string Title, string ClassName, bool Minimized, NativeRect Bounds);
internal sealed record OcrTextLine(string Text, RectangleF Bounds, float Confidence = 1);
internal sealed record UnreadBadge(Rectangle Bounds, int EstimatedCount);
internal sealed record ConversationRowText(string? Name, string? Preview);
internal sealed record ConversationContactRow(string Name, int RowY);
internal sealed record ConversationMessageReadResult(string? Title, IReadOnlyList<string> Messages);
internal sealed record ClipboardSnapshot(System.Windows.Forms.IDataObject? Data, bool HasData);

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
