using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class WeChatReplyService(
    WeChatInstanceRegistry registry,
    WeChatWindowCoordinator coordinator,
    ILogger<WeChatReplyService> logger)
{
    public async Task<WeChatContactsRo> GetContactsAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = GetInstance(instanceId);
        await coordinator.Gate.WaitAsync(cancellationToken);
        var foreground = WeChatNative.GetForeground();
        WindowPlacement? placement = null;
        try
        {
            placement = WeChatNative.CapturePlacement(instance.Window.Handle);
            await WeChatNative.PrepareWindowAsync(instance.Window, cancellationToken);
            using var image = WeChatNative.Capture(instance.Window.Handle);
            var paneRight = WeChatNative.FindConversationPaneRight(image);
            var contacts = await WeChatNative.ReadConversationContactsAsync(image, paneRight, cancellationToken);
            return new WeChatContactsRo(instance.Id, instance.DisplayName, contacts);
        }
        finally
        {
            if (placement is { } value) WeChatNative.RestoreWindow(instance.Window.Handle, value);
            WeChatNative.RestoreForeground(foreground);
            coordinator.Gate.Release();
        }
    }

    public async Task<SendWeChatMessageRo> SendAsync(SendWeChatMessageVo request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) throw new ArgumentException("发送前必须完成确认。");
        var contact = request.ContactName?.Trim();
        var message = request.Message?.Trim();
        if (string.IsNullOrWhiteSpace(contact)) throw new ArgumentException("请选择微信联系人或群聊。");
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请输入回复内容。");
        if (contact.Length > 80) throw new ArgumentException("联系人名称过长。");
        if (message.Length > 1000) throw new ArgumentException("单次回复不能超过 1000 个字符。");

        var instance = GetInstance(request.InstanceId);
        await coordinator.Gate.WaitAsync(cancellationToken);
        var foreground = WeChatNative.GetForeground();
        WindowPlacement? placement = null;
        int? selectedConversationY = null;
        try
        {
            placement = WeChatNative.CapturePlacement(instance.Window.Handle);
            await WeChatNative.PrepareWindowAsync(instance.Window, cancellationToken);
            using var listImage = WeChatNative.Capture(instance.Window.Handle);
            var paneRight = WeChatNative.FindConversationPaneRight(listImage);
            selectedConversationY = WeChatNative.FindSelectedConversationY(listImage, paneRight);
            var contacts = await WeChatNative.ReadConversationContactRowsAsync(listImage, paneRight, cancellationToken);
            var matches = contacts.Where(item => item.Name.Equals(contact, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("当前会话列表中未找到该联系人，请刷新联系人后重试。");
            if (matches.Length > 1) throw new InvalidOperationException("当前列表中存在多个同名会话，已取消发送以避免发错。");

            await WeChatNative.SelectConversationAsync(instance.Window, paneRight, matches[0].RowY, cancellationToken);
            await Task.Delay(350, cancellationToken);
            var title = await WeChatNative.ReadCurrentChatTitleAsync(instance.Window, paneRight, cancellationToken);
            if (string.IsNullOrWhiteSpace(title) || !WeChatNative.ChatTitleMatches(title, contact))
            {
                throw new InvalidOperationException($"微信聊天标题校验失败，当前识别为“{title ?? "未知"}”，已取消发送。");
            }

            await WeChatNative.SendTextMessageAsync(instance.Window, message, cancellationToken);
            logger.LogInformation("微信实例 {InstanceId} 已发送 1 条文本消息", instance.Id);
            return new SendWeChatMessageRo(true, instance.DisplayName, contact, DateTimeOffset.Now);
        }
        finally
        {
            if (selectedConversationY is { } rowY)
            {
                try
                {
                    using var currentImage = WeChatNative.Capture(instance.Window.Handle);
                    var paneRight = WeChatNative.FindConversationPaneRight(currentImage);
                    await WeChatNative.SelectConversationAsync(instance.Window, paneRight, rowY, CancellationToken.None);
                }
                catch { }
            }
            if (placement is { } value) WeChatNative.RestoreWindow(instance.Window.Handle, value);
            WeChatNative.RestoreForeground(foreground);
            coordinator.Gate.Release();
        }
    }

    private WeChatInstanceState GetInstance(string instanceId)
    {
        registry.Refresh();
        return registry.Snapshot().FirstOrDefault(item => item.Id == instanceId && item.Online)
            ?? throw new KeyNotFoundException("微信实例不存在或已经离线。");
    }
}
