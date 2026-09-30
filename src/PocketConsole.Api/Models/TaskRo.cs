namespace PocketConsole.Api.Models;

public sealed record TaskRo(
    Guid Id,
    string Title,
    string Prompt,
    string ProjectPath,
    string? ThreadId,
    string Status,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record CreateTaskVo(string Title, string Prompt, string ProjectPath, string? ThreadId);

public sealed record UpdateTaskVo(string? Title, string? Status, string? ThreadId, string? LastError, string? ProjectPath = null);
