using System.Security.Cryptography;
using System.Text;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class WeChatMonitorService(
    WeChatInstanceRegistry registry,
    CodexNotificationDispatcher dispatcher,
    CodexAppServerClient codexClient,
    ILogger<WeChatMonitorService> logger)
{
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private string? _threadId;
    private int _intervalSeconds = 60;
    private DateTimeOffset? _lastScanAt;
    private string? _lastError;

    public async Task<WeChatMonitorRo> StartAsync(string threadId, int intervalSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("请选择 Codex 目标会话。");
        intervalSeconds = Math.Clamp(intervalSeconds, 15, 3600);
        await codexClient.SendAsync("thread/read", new { threadId, includeTurns = false }, cancellationToken);
        lock (_stateLock)
        {
            if (_worker is { IsCompleted: false }) throw new InvalidOperationException("微信监控已在运行。");
            _threadId = threadId.Trim();
            _intervalSeconds = intervalSeconds;
            _lastError = null;
            _cancellation = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);
        }
        await RefreshInstancesAsync(cancellationToken);
        return GetStatus();
    }

    public async Task<WeChatMonitorRo> StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? worker;
        lock (_stateLock)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
            _threadId = null;
        }
        if (cancellation is not null)
        {
            cancellation.Cancel();
            try { if (worker is not null) await worker; }
            catch (OperationCanceledException) { }
            cancellation.Dispose();
        }
        dispatcher.Clear();
        return GetStatus();
    }

    public Task<WeChatMonitorRo> RefreshInstancesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.Refresh();
        return Task.FromResult(GetStatus());
    }

    public async Task<WeChatMonitorRo> RefreshProfileNamesAsync(CancellationToken cancellationToken)
    {
        var foreground = WeChatNative.GetForeground();
        try
        {
            var instances = registry.Refresh().Where(state => state.Online).OrderBy(state => state.Number).ToArray();
            foreach (var instance in instances)
            {
                WindowPlacement? placement = null;
                try
                {
                    placement = WeChatNative.CapturePlacement(instance.Window.Handle);
                    instance.ProfileNameAttempted = true;
                    var detectedName = await WeChatNative.ReadProfileNicknameAsync(instance.Window, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(detectedName)) instance.DetectedName = detectedName;
                }
                finally
                {
                    if (placement is { } value) WeChatNative.RestoreWindow(instance.Window.Handle, value);
                }
            }
            return GetStatus();
        }
        finally
        {
            WeChatNative.RestoreForeground(foreground);
        }
    }

    public WeChatMonitorRo RenameInstance(string id, string? name)
    {
        if (!registry.Rename(id, name)) throw new KeyNotFoundException("微信实例不存在或已经离线。");
        return GetStatus();
    }

    public WeChatMonitorRo GetStatus()
    {
        string? threadId;
        int interval;
        bool running;
        DateTimeOffset? lastScanAt;
        string? lastError;
        lock (_stateLock)
        {
            threadId = _threadId;
            interval = _intervalSeconds;
            running = _worker is { IsCompleted: false };
            lastScanAt = _lastScanAt;
            lastError = _lastError;
        }
        var instances = registry.Snapshot().Select(state => new WeChatInstanceRo(
            state.Id, state.Number, state.DisplayName, state.Window.Handle.ToInt64(), state.Window.ProcessId,
            state.Online, state.LoggedIn, state.Window.Minimized ? "minimized" : "visible", state.LastSeenAt,
            state.LastScanAt, state.LastUnreadCount, state.LastError)).ToArray();
        return new WeChatMonitorRo(running, threadId, interval, lastScanAt, dispatcher.LastNotificationAt, dispatcher.PendingCount, lastError, instances);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextScan = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now >= nextScan)
                {
                    await ScanAsync(cancellationToken);
                    nextScan = DateTimeOffset.Now.AddSeconds(_intervalSeconds);
                }
                var threadId = _threadId;
                if (!string.IsNullOrWhiteSpace(threadId)) await dispatcher.TryFlushAsync(threadId, cancellationToken);
                lock (_stateLock) _lastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                lock (_stateLock) _lastError = exception.Message;
                logger.LogWarning(exception, "微信监控循环失败");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var foreground = WeChatNative.GetForeground();
        try
        {
            var instances = registry.Refresh().Where(state => state.Online).OrderBy(state => state.Number).ToArray();
            foreach (var instance in instances)
            {
                WindowPlacement? placement = null;
                try
                {
                    placement = WeChatNative.CapturePlacement(instance.Window.Handle);
                    await ScanInstanceAsync(instance, cancellationToken);
                    ReportRecovery(instance);
                }
                catch (Exception exception)
                {
                    instance.LastError = exception.Message;
                    ReportFailure(instance, exception.GetType().Name, exception.Message);
                    logger.LogWarning(exception, "微信实例 {InstanceId} 扫描失败", instance.Id);
                }
                finally
                {
                    instance.LastScanAt = DateTimeOffset.Now;
                    if (placement is { } value) WeChatNative.RestoreWindow(instance.Window.Handle, value);
                }
            }
        }
        finally
        {
            WeChatNative.RestoreForeground(foreground);
            lock (_stateLock) _lastScanAt = DateTimeOffset.Now;
        }
    }

    private async Task ScanInstanceAsync(WeChatInstanceState instance, CancellationToken cancellationToken)
    {
        await WeChatNative.PrepareWindowAsync(instance.Window, cancellationToken);
        if (!instance.ProfileNameAttempted && string.IsNullOrWhiteSpace(instance.CustomName))
        {
            instance.ProfileNameAttempted = true;
            var detectedName = await WeChatNative.ReadProfileNicknameAsync(instance.Window, cancellationToken);
            if (!string.IsNullOrWhiteSpace(detectedName)) instance.DetectedName = detectedName;
        }
        using var listImage = WeChatNative.Capture(instance.Window.Handle);
        var paneRight = WeChatNative.FindConversationPaneRight(listImage);
        var listRegion = new System.Drawing.Rectangle(68, 72, Math.Max(1, paneRight - 68), Math.Max(1, listImage.Height - 105));
        var listLines = await WeChatNative.ReadTextAsync(listImage, listRegion, 2, cancellationToken);
        var badges = await WeChatNative.FindUnreadBadgesAsync(listImage, paneRight, cancellationToken);
        var selectedConversationY = WeChatNative.FindSelectedConversationY(listImage, paneRight);
        instance.LoggedIn = listLines.Count > 2 || badges.Count > 0;
        if (!instance.LoggedIn) throw new InvalidOperationException("微信可能尚未登录，或窗口内容不可识别。");

        var conversations = new List<(UnreadBadge Badge, string Name, string Preview)>();
        for (var index = 0; index < badges.Count; index++)
        {
            var badge = badges[index];
            var row = await WeChatNative.ReadConversationRowAsync(listImage, badge, paneRight, cancellationToken);
            var name = WeChatNative.FindConversationName(listLines, badge, paneRight)
                ?? row.Name
                ?? $"未识别会话 {index + 1}";
            var preview = WeChatNative.FindConversationPreview(listLines, badge, paneRight, name)
                ?? (row.Preview is { } value && !value.Equals(name, StringComparison.Ordinal) ? value : null)
                ?? "[新消息，预览无法识别]";
            conversations.Add((badge, name, WeChatNative.CleanConversationPreview(preview)));
        }
        instance.LastUnreadCount = conversations.Sum(item => item.Badge.EstimatedCount);
        if (!instance.BaselineReady)
        {
            foreach (var item in conversations) instance.Conversations[item.Name] = new ConversationBaseline(item.Badge.EstimatedCount, Hash(item.Preview));
            instance.BaselineReady = true;
            instance.LastError = null;
            return;
        }

        var openedConversation = false;
        try
        {
            foreach (var item in conversations)
            {
                var previous = instance.Conversations.GetValueOrDefault(item.Name)?.UnreadCount ?? 0;
                var delta = Math.Max(0, item.Badge.EstimatedCount - previous);
                var previewHash = Hash(item.Preview);
                if (delta == 0) continue;
                IReadOnlyList<string> messages;
                try
                {
                    messages = await WeChatNative.ReadLatestConversationMessagesAsync(
                        instance.Window,
                        paneRight,
                        item.Badge,
                        Math.Max(1, delta),
                        cancellationToken);
                    openedConversation = true;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "微信实例 {InstanceId} 会话完整消息读取失败", instance.Id);
                    messages = [];
                }
                var content = messages.Count > 0
                    ? string.Join("\n", messages)
                    : delta > 1 ? $"{delta} 条新消息，最新：{item.Preview}" : item.Preview;
                dispatcher.Enqueue(new WeChatNotification(instance.DisplayName, item.Name, content, DateTimeOffset.Now));
                instance.Conversations[item.Name] = new ConversationBaseline(0, previewHash);
            }
        }
        finally
        {
            if (openedConversation && selectedConversationY is { } rowY)
            {
                await WeChatNative.SelectConversationAsync(instance.Window, paneRight, rowY, CancellationToken.None);
            }
        }
        var unreadNames = conversations.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in instance.Conversations.Keys.Where(name => !unreadNames.Contains(name)).ToArray())
        {
            var baseline = instance.Conversations[name];
            instance.Conversations[name] = new ConversationBaseline(0, baseline.PreviewHash);
        }
        instance.LastError = null;
    }

    private void ReportFailure(WeChatInstanceState instance, string failureKey, string message)
    {
        if (!instance.ReportedFailures.Add(failureKey)) return;
        dispatcher.Enqueue(new WeChatNotification(instance.DisplayName, "监控状态", $"监控异常：{message}", DateTimeOffset.Now, true));
    }

    private void ReportRecovery(WeChatInstanceState instance)
    {
        if (instance.ReportedFailures.Count == 0) return;
        instance.ReportedFailures.Clear();
        dispatcher.Enqueue(new WeChatNotification(instance.DisplayName, "监控状态", "微信监控已恢复。", DateTimeOffset.Now, true));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
