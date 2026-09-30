namespace PocketConsole.Api.Models;

public sealed record HostStatusRo(
    bool Connected,
    string CodexVersion,
    string ExecutablePath,
    DateTimeOffset StartedAt);
