using System.Security.Cryptography;
using System.Text;

namespace PocketConsole.Api.Services;

public sealed class WeChatInstanceRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, WeChatInstanceState> _instances = [];
    private int _nextNumber = 1;

    internal IReadOnlyList<WeChatInstanceState> Refresh()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            var foundIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in WeChatNative.DiscoverWindows())
            {
                var id = CreateId(window);
                foundIds.Add(id);
                if (!_instances.TryGetValue(id, out var state))
                {
                    state = new WeChatInstanceState(id, _nextNumber++, window);
                    _instances[id] = state;
                }
                else state.UpdateWindow(window);
                state.Online = true;
                state.LastSeenAt = now;
            }

            foreach (var state in _instances.Values.Where(item => !foundIds.Contains(item.Id))) state.Online = false;
            foreach (var stale in _instances.Values.Where(item => !item.Online && item.LastSeenAt < now.AddMinutes(-30)).Select(item => item.Id).ToArray()) _instances.Remove(stale);
            return _instances.Values.OrderBy(item => item.Number).ToArray();
        }
    }

    internal IReadOnlyList<WeChatInstanceState> Snapshot()
    {
        lock (_lock) return _instances.Values.OrderBy(item => item.Number).ToArray();
    }

    internal bool Rename(string id, string? name)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(id, out var state)) return false;
            state.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 30)];
            return true;
        }
    }

    private static string CreateId(NativeWeChatWindow window)
    {
        var value = $"{window.ExecutablePath}|{window.ProcessId}|{window.ProcessStartedAt.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    }
}

internal sealed class WeChatInstanceState(string id, int number, NativeWeChatWindow window)
{
    public string Id { get; } = id;
    public int Number { get; } = number;
    public NativeWeChatWindow Window { get; private set; } = window;
    public bool Online { get; set; } = true;
    public bool LoggedIn { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
    public int LastUnreadCount { get; set; }
    public string? LastError { get; set; }
    public bool BaselineReady { get; set; }
    public Dictionary<string, ConversationBaseline> Conversations { get; } = new(StringComparer.Ordinal);
    // 仅在本次服务运行期间保存“完整聊天标题 -> 可见行坐标”，不写数据库或日志。
    public Dictionary<string, int> VisibleContactRows { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ReportedFailures { get; } = new(StringComparer.Ordinal);
    public string? CustomName { get; set; }
    public string? DetectedName { get; set; }
    public bool ProfileNameAttempted { get; set; }
    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(CustomName) ? DetectedName : CustomName;
            return string.IsNullOrWhiteSpace(name) ? $"微信 {Number}" : $"{name}（微信 {Number}）";
        }
    }
    public void UpdateWindow(NativeWeChatWindow window) => Window = window;
}

internal sealed record ConversationBaseline(int UnreadCount, string PreviewHash);
