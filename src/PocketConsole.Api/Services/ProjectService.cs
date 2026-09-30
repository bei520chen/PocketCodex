using Microsoft.EntityFrameworkCore;
using PocketConsole.Api.Data;
using PocketConsole.Api.Entities;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class ProjectService(
    PocketConsoleDbContext dbContext,
    CodexThreadService threadService,
    WorkspaceGuard workspaceGuard)
{
    public async Task<IReadOnlyList<ProjectRo>> ListAsync(string? search, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectRo> discovered;
        try { discovered = await threadService.ListProjectsAsync(search, cancellationToken); }
        catch { discovered = []; }

        var saved = await dbContext.Projects.AsNoTracking().ToListAsync(cancellationToken);
        var values = discovered.ToDictionary(project => project.WorkingDirectory, StringComparer.OrdinalIgnoreCase);

        foreach (var entity in saved)
        {
            if (values.TryGetValue(entity.WorkingDirectory, out var existing))
            {
                values[entity.WorkingDirectory] = existing with { Id = entity.Id.ToString("N"), Name = entity.Name };
                continue;
            }

            if (!Matches(entity, search)) continue;
            values[entity.WorkingDirectory] = new ProjectRo(
                entity.Id.ToString("N"), entity.Name, entity.WorkingDirectory, null, 0,
                entity.UpdatedAt.ToUnixTimeSeconds(), "暂无会话");
        }

        return values.Values.OrderByDescending(project => project.LastActiveAt).ToArray();
    }

    public async Task<ProjectRo> CreateAsync(CreateProjectVo vo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vo.Name)) throw new ArgumentException("项目名称不能为空。");
        if (string.IsNullOrWhiteSpace(vo.WorkingDirectory)) throw new ArgumentException("项目目录不能为空。");

        var directory = workspaceGuard.Prepare(vo.WorkingDirectory, vo.CreateDirectory);
        var existing = await dbContext.Projects.FirstOrDefaultAsync(
            project => project.WorkingDirectory == directory, cancellationToken);
        if (existing is not null) throw new InvalidOperationException("该项目目录已经存在。");

        var now = DateTimeOffset.UtcNow;
        var entity = new ProjectEntity
        {
            Id = Guid.NewGuid(),
            Name = vo.Name.Trim(),
            WorkingDirectory = directory,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.Projects.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ProjectRo(entity.Id.ToString("N"), entity.Name, directory, null, 0, now.ToUnixTimeSeconds(), "暂无会话");
    }

    private static bool Matches(ProjectEntity entity, string? search) =>
        string.IsNullOrWhiteSpace(search) ||
        entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entity.WorkingDirectory.Contains(search, StringComparison.OrdinalIgnoreCase);
}
