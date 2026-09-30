using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PocketConsole.Api.Data;
using PocketConsole.Api.Entities;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class TaskExecutionService(
    PocketConsoleDbContext dbContext,
    CodexAppServerClient codexClient,
    TaskService taskService,
    WorkspaceGuard workspaceGuard)
{
    public async Task<TaskRo?> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return null;
        if (string.IsNullOrWhiteSpace(entity.ProjectPath))
            throw new InvalidOperationException("请先为任务选择项目后再开始执行。");

        try
        {
            if (string.IsNullOrWhiteSpace(entity.ThreadId))
            {
                var threadResult = await codexClient.SendAsync("thread/start", new
                {
                    cwd = workspaceGuard.Validate(entity.ProjectPath),
                    approvalPolicy = "never",
                    sandbox = "workspace-write",
                    serviceName = "pocket-console"
                }, cancellationToken);
                entity.ThreadId = threadResult.GetProperty("thread").GetProperty("id").GetString();
            }

            entity.Status = TaskStatuses.Running;
            entity.StartedAt ??= DateTimeOffset.UtcNow;
            entity.CompletedAt = null;
            entity.LastError = null;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await taskService.PublishAsync(entity, cancellationToken);

            await ResumeAndStartTurnAsync(entity.ThreadId!, entity.ProjectPath, entity.Prompt, cancellationToken);
            return await taskService.GetAsync(entity.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            entity.Status = TaskStatuses.Failed;
            entity.LastError = exception.Message;
            entity.CompletedAt = DateTimeOffset.UtcNow;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await taskService.PublishAsync(entity, cancellationToken);
            throw;
        }
    }

    public async Task<TaskRo?> SendMessageAsync(Guid id, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Message is required.");
        var entity = await dbContext.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return null;
        if (string.IsNullOrWhiteSpace(entity.ThreadId)) throw new InvalidOperationException("Task has no Codex thread. Start it first.");

        entity.Status = TaskStatuses.Running;
        entity.CompletedAt = null;
        entity.LastError = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await taskService.PublishAsync(entity, cancellationToken);
        await ResumeAndStartTurnAsync(entity.ThreadId, entity.ProjectPath, message.Trim(), cancellationToken);
        return await taskService.GetAsync(entity.Id, cancellationToken);
    }

    public async Task<TaskRo?> InterruptAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return null;
        if (string.IsNullOrWhiteSpace(entity.ThreadId)) return await taskService.GetAsync(id, cancellationToken);

        var read = await codexClient.SendAsync("thread/read", new { threadId = entity.ThreadId, includeTurns = true }, cancellationToken);
        var turns = read.GetProperty("thread").GetProperty("turns").EnumerateArray();
        var active = turns.LastOrDefault(turn => turn.TryGetProperty("status", out var status) && status.GetString() == "inProgress");
        if (active.ValueKind == JsonValueKind.Object)
        {
            await codexClient.SendAsync("turn/interrupt", new { threadId = entity.ThreadId, turnId = active.GetProperty("id").GetString() }, cancellationToken);
        }

        return await taskService.UpdateAsync(id, new UpdateTaskVo(null, TaskStatuses.Cancelled, null, null), cancellationToken);
    }

    private async Task ResumeAndStartTurnAsync(string threadId, string projectPath, string message, CancellationToken cancellationToken)
    {
        await codexClient.SendAsync("thread/resume", new
        {
            threadId,
            cwd = workspaceGuard.Validate(projectPath),
            approvalPolicy = "never",
            sandbox = "workspace-write"
        }, cancellationToken);
        await codexClient.SendAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text = message, text_elements = Array.Empty<object>() } }
        }, cancellationToken);
    }

}
