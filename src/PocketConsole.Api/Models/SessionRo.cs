using System.Text.Json;

namespace PocketConsole.Api.Models;

public sealed record SessionRo(
    string Id,
    string SessionId,
    string Title,
    string Preview,
    string WorkingDirectory,
    string ProjectName,
    string Status,
    string Source,
    string? Branch,
    string? AgentNickname,
    long CreatedAt,
    long UpdatedAt);

public sealed record SessionPageRo(IReadOnlyList<SessionRo> Items, string? NextCursor);

public sealed record SessionDetailRo(SessionRo Summary, JsonElement Thread);

public sealed record SendSessionMessageVo(string Message, IReadOnlyList<string>? AttachmentIds = null);
