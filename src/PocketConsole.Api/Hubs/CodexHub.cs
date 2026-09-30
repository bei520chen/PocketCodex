using Microsoft.AspNetCore.SignalR;

namespace PocketConsole.Api.Hubs;

public sealed class CodexHub : Hub
{
    public Task Ping() => Clients.Caller.SendAsync("host:pong", DateTimeOffset.UtcNow);
}
