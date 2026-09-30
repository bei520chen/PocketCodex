namespace PocketConsole.Api.Entities;

public sealed class TaskEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string? ThreadId { get; set; }
    public string Status { get; set; } = TaskStatuses.Draft;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class TaskStatuses
{
    public const string Draft = "draft";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingApproval = "waitingApproval";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly HashSet<string> All =
    [Draft, Queued, Running, WaitingApproval, Completed, Failed, Cancelled];
}
