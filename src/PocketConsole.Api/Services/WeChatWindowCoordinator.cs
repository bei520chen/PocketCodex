namespace PocketConsole.Api.Services;

public sealed class WeChatWindowCoordinator
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}
