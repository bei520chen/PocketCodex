namespace PocketConsole.Api.Models;

public sealed record StartWeChatMonitorVo(string ThreadId, int IntervalSeconds = 60);
public sealed record RenameWeChatInstanceVo(string Name);
public sealed record SendWeChatMessageVo(string InstanceId, string ContactName, string Message, bool Confirmed);
public sealed record WeChatContactsRo(string InstanceId, string InstanceName, IReadOnlyList<string> Contacts);
public sealed record SendWeChatMessageRo(bool Sent, string InstanceName, string ContactName, DateTimeOffset SentAt);

public sealed record WeChatMonitorRo(
    bool Running,
    string? ThreadId,
    int IntervalSeconds,
    DateTimeOffset? LastScanAt,
    DateTimeOffset? LastNotificationAt,
    int PendingMessageCount,
    string? LastError,
    IReadOnlyList<WeChatInstanceRo> Instances);

public sealed record WeChatInstanceRo(
    string Id,
    int Number,
    string DisplayName,
    long WindowHandle,
    int ProcessId,
    bool Online,
    bool LoggedIn,
    string WindowState,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastScanAt,
    int LastUnreadCount,
    string? LastError);
