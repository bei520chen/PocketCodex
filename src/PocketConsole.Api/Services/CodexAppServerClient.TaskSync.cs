using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PocketConsole.Api.Data;
using PocketConsole.Api.Entities;

namespace PocketConsole.Api.Services;

public sealed partial class CodexAppServerClient
{
    private async Task SynchronizeTaskAsync(JsonElement parameters)
    {
        try
        {
            var threadId = parameters.GetProperty("threadId").GetString();
            if (string.IsNullOrWhiteSpace(threadId)) return;
            var turn = parameters.GetProperty("turn");
            var turnStatus = turn.GetProperty("status").GetString();

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PocketConsoleDbContext>();
            var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
            var matches = await dbContext.Tasks.Where(item => item.ThreadId == threadId).ToListAsync();
            var entity = matches.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (entity is null) return;

            entity.Status = turnStatus switch
            {
                "completed" => TaskStatuses.Completed,
                "interrupted" => TaskStatuses.Cancelled,
                "failed" => TaskStatuses.Failed,
                _ => entity.Status
            };
            entity.CompletedAt = DateTimeOffset.UtcNow;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            if (turnStatus == "failed" && turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                entity.LastError = error.TryGetProperty("message", out var message) ? message.GetString() : error.ToString();
            }
            await dbContext.SaveChangesAsync();
            await taskService.PublishAsync(entity, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to synchronize task from Codex turn completion.");
        }
    }
}
