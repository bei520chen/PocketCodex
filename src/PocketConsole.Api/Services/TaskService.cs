using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PocketConsole.Api.Data;
using PocketConsole.Api.Entities;
using PocketConsole.Api.Hubs;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class TaskService(PocketConsoleDbContext dbContext, IHubContext<CodexHub> hubContext, WorkspaceGuard workspaceGuard)
{
    public async Task<IReadOnlyList<TaskRo>> ListAsync(CancellationToken cancellationToken)
    {
        var entities = await dbContext.Tasks.AsNoTracking().ToListAsync(cancellationToken);
        return entities.OrderByDescending(entity => entity.UpdatedAt).Select(Map).ToArray();
    }

    public async Task<TaskRo?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<TaskRo> CreateAsync(CreateTaskVo vo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vo.Title) || string.IsNullOrWhiteSpace(vo.Prompt))
            throw new ArgumentException("Title and prompt are required.");

        var now = DateTimeOffset.UtcNow;
        var entity = new TaskEntity
        {
            Id = Guid.NewGuid(),
            Title = vo.Title.Trim(),
            Prompt = vo.Prompt.Trim(),
            ProjectPath = string.IsNullOrWhiteSpace(vo.ProjectPath) ? string.Empty : workspaceGuard.Validate(vo.ProjectPath),
            ThreadId = string.IsNullOrWhiteSpace(vo.ThreadId) ? null : vo.ThreadId.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.Tasks.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        var result = Map(entity);
        await hubContext.Clients.All.SendAsync("task:created", result, cancellationToken);
        return result;
    }

    public async Task<TaskRo?> UpdateAsync(Guid id, UpdateTaskVo vo, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return null;
        if (!string.IsNullOrWhiteSpace(vo.Title)) entity.Title = vo.Title.Trim();
        if (vo.ThreadId is not null) entity.ThreadId = string.IsNullOrWhiteSpace(vo.ThreadId) ? null : vo.ThreadId.Trim();
        if (vo.LastError is not null) entity.LastError = string.IsNullOrWhiteSpace(vo.LastError) ? null : vo.LastError.Trim();
        if (vo.ProjectPath is not null) entity.ProjectPath = string.IsNullOrWhiteSpace(vo.ProjectPath) ? string.Empty : workspaceGuard.Validate(vo.ProjectPath);
        if (!string.IsNullOrWhiteSpace(vo.Status)) ApplyStatus(entity, vo.Status);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        var result = Map(entity);
        await hubContext.Clients.All.SendAsync("task:updated", result, cancellationToken);
        return result;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return false;
        dbContext.Tasks.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.All.SendAsync("task:deleted", id, cancellationToken);
        return true;
    }

    public async Task PublishAsync(TaskEntity entity, CancellationToken cancellationToken) =>
        await hubContext.Clients.All.SendAsync("task:updated", Map(entity), cancellationToken);

    private static void ApplyStatus(TaskEntity entity, string status)
    {
        if (!TaskStatuses.All.Contains(status)) throw new ArgumentException("Unsupported task status.");
        entity.Status = status;
        if (status == TaskStatuses.Running) entity.StartedAt ??= DateTimeOffset.UtcNow;
        if (status is TaskStatuses.Completed or TaskStatuses.Failed or TaskStatuses.Cancelled) entity.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static TaskRo Map(TaskEntity entity) => new(entity.Id, entity.Title, entity.Prompt, entity.ProjectPath, entity.ThreadId, entity.Status, entity.LastError, entity.CreatedAt, entity.UpdatedAt, entity.StartedAt, entity.CompletedAt);
}
