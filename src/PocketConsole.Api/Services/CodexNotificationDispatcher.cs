using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PocketConsole.Api.Services;

public sealed class CodexNotificationDispatcher(CodexAppServerClient client)
{
    private const int MaxQueueSize = 200;
    private const int MaxPromptLength = 12000;
    private readonly ConcurrentQueue<WeChatNotification> _queue = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _omittedCount;

    public int PendingCount => _queue.Count;
    public DateTimeOffset? LastNotificationAt { get; private set; }

    internal void Enqueue(WeChatNotification notification)
    {
        _queue.Enqueue(notification);
        while (_queue.Count > MaxQueueSize && _queue.TryDequeue(out _)) Interlocked.Increment(ref _omittedCount);
    }

    internal void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _omittedCount, 0);
    }

    internal async Task<bool> TryFlushAsync(string threadId, CancellationToken cancellationToken)
    {
        if (_queue.IsEmpty || !await _flushLock.WaitAsync(0, cancellationToken)) return false;
        try
        {
            if (await IsBusyAsync(threadId, cancellationToken)) return false;
            var values = new List<WeChatNotification>();
            while (_queue.TryDequeue(out var notification)) values.Add(notification);
            if (values.Count == 0) return false;

            var omittedCount = Interlocked.Exchange(ref _omittedCount, 0);
            var prompt = BuildPrompt(values, omittedCount);
            try
            {
                await client.SendAsync("thread/resume", new { threadId }, cancellationToken);
                await client.SendAsync("turn/start", new
                {
                    threadId,
                    input = new[] { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } }
                }, cancellationToken);
                LastNotificationAt = DateTimeOffset.Now;
                return true;
            }
            catch
            {
                foreach (var value in values) _queue.Enqueue(value);
                Interlocked.Add(ref _omittedCount, omittedCount);
                throw;
            }
        }
        finally { _flushLock.Release(); }
    }

    private async Task<bool> IsBusyAsync(string threadId, CancellationToken cancellationToken)
    {
        var result = await client.SendAsync("thread/read", new { threadId, includeTurns = true }, cancellationToken);
        if (!result.TryGetProperty("thread", out var thread) || !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array) return false;
        return turns.EnumerateArray().Any(turn => turn.TryGetProperty("status", out var status) && status.GetString() == "inProgress");
    }

    private static string BuildPrompt(IReadOnlyList<WeChatNotification> values, int omittedCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("这是微信监控器生成的通知。不要调用任何工具，不要执行任务，只把以下内容整理为简短清晰的微信新消息列表。按微信账号和聊天会话分组，保留发送人、时间、正文或消息类型；不要添加回复建议。");
        foreach (var account in values.GroupBy(value => value.AccountName))
        {
            builder.AppendLine().AppendLine(account.Key);
            foreach (var conversation in account.GroupBy(value => value.ConversationName))
            {
                builder.Append("- ").Append(conversation.Key).AppendLine();
                foreach (var value in conversation.OrderBy(item => item.DetectedAt))
                    builder.Append("  - ").Append(value.DetectedAt.ToLocalTime().ToString("HH:mm:ss")).Append(' ').AppendLine(value.Content);
            }
        }
        if (omittedCount > 0) builder.AppendLine().Append("另有 ").Append(omittedCount).AppendLine(" 条较早通知因队列上限被省略。");
        if (builder.Length > MaxPromptLength) return builder.ToString(0, MaxPromptLength) + "\n[内容过长，已截断]";
        return builder.ToString();
    }
}

internal sealed record WeChatNotification(string AccountName, string ConversationName, string Content, DateTimeOffset DetectedAt, bool IsFailure = false);
