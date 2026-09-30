using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class CodexThreadService(CodexAppServerClient client, WorkspaceGuard workspaceGuard, AttachmentService attachmentService)
{
    public async Task<SessionPageRo> ListSessionsAsync(string? search, string? cwd, bool archived, int limit, CancellationToken cancellationToken)
    {
        var result = await client.SendAsync("thread/list", new
        {
            limit,
            sortKey = "updated_at",
            sortDirection = "desc",
            archived,
            cwd = string.IsNullOrWhiteSpace(cwd) ? null : cwd,
            searchTerm = string.IsNullOrWhiteSpace(search) ? null : search
        }, cancellationToken);

        var items = result.GetProperty("data").EnumerateArray().Select(MapSession).ToArray();
        var nextCursor = result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String
            ? cursor.GetString()
            : null;
        return new SessionPageRo(items, nextCursor);
    }

    public Task<SessionDetailRo?> ReadSessionSummaryAsync(string threadId, CancellationToken cancellationToken) => ReadSessionAsync(threadId, false, cancellationToken);

    public Task<SessionDetailRo?> ReadSessionContentAsync(string threadId, CancellationToken cancellationToken) => ReadSessionAsync(threadId, true, cancellationToken);

    public async Task<SessionDetailRo?> ReadSessionAsync(string threadId, CancellationToken cancellationToken) => await ReadSessionAsync(threadId, true, cancellationToken);

    private async Task<SessionDetailRo?> ReadSessionAsync(string threadId, bool includeTurns, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.SendAsync("thread/read", new { threadId, includeTurns }, cancellationToken);
            var thread = result.GetProperty("thread");
            return new SessionDetailRo(MapSession(thread), thread.Clone());
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public async Task<SessionDetailRo> CreateSessionAsync(string? projectPath, string message, IReadOnlyList<string>? attachmentIds, CancellationToken cancellationToken)
    {
        var input = BuildInput(message, attachmentIds);
        var cwd = string.IsNullOrWhiteSpace(projectPath) ? workspaceGuard.DefaultRoot : workspaceGuard.Validate(projectPath);
        var result = await client.SendAsync("thread/start", new
        {
            cwd,
            approvalPolicy = "never",
            sandbox = "workspace-write",
            serviceName = "pocket-console"
        }, cancellationToken);
        var threadId = result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Codex did not return a thread id.");
        await client.SendAsync("turn/start", new
        {
            threadId,
            input
        }, cancellationToken);
        return await ReadSessionAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("The new Codex thread could not be read.");
    }

    public async Task<SessionDetailRo?> SendMessageAsync(string threadId, string message, IReadOnlyList<string>? attachmentIds, CancellationToken cancellationToken)
    {
        var input = BuildInput(message, attachmentIds);
        var session = await ReadSessionAsync(threadId, cancellationToken);
        if (session is null) return null;

        await client.SendAsync("thread/resume", new
        {
            threadId,
            cwd = workspaceGuard.Validate(session.Summary.WorkingDirectory),
            approvalPolicy = "never",
            sandbox = "workspace-write"
        }, cancellationToken);
        await client.SendAsync("turn/start", new
        {
            threadId,
            input
        }, cancellationToken);
        return await ReadSessionAsync(threadId, cancellationToken);
    }

    private object[] BuildInput(string message, IReadOnlyList<string>? attachmentIds)
    {
        var values = new List<object>();
        if (!string.IsNullOrWhiteSpace(message))
            values.Add(new { type = "text", text = message.Trim(), text_elements = Array.Empty<object>() });

        foreach (var id in attachmentIds?.Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
        {
            var attachment = attachmentService.Resolve(id);
            values.Add(attachment.IsImage
                ? new { type = "localImage", path = attachment.Path }
                : new { type = "mention", name = attachment.Name, path = attachment.Path });
        }

        if (values.Count == 0) throw new ArgumentException("请输入消息或选择附件。");
        return values.ToArray();
    }

    public async Task<IReadOnlyList<ProjectRo>> ListProjectsAsync(string? search, CancellationToken cancellationToken)
    {
        var page = await ListSessionsAsync(search, null, false, 100, cancellationToken);
        return page.Items.GroupBy(session => session.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.UpdatedAt).First();
                return new ProjectRo(CreateId(group.Key), latest.ProjectName, group.Key, latest.Branch, group.Count(), latest.UpdatedAt, latest.Title);
            })
            .OrderByDescending(project => project.LastActiveAt)
            .ToArray();
    }

    private static SessionRo MapSession(JsonElement thread)
    {
        var cwd = thread.GetProperty("cwd").GetString() ?? string.Empty;
        var preview = thread.TryGetProperty("preview", out var previewValue) ? previewValue.GetString() ?? string.Empty : string.Empty;
        var name = thread.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
        var branch = thread.TryGetProperty("gitInfo", out var git) && git.ValueKind == JsonValueKind.Object && git.TryGetProperty("branch", out var branchValue) && branchValue.ValueKind == JsonValueKind.String ? branchValue.GetString() : null;
        var source = thread.TryGetProperty("source", out var sourceValue) ? SourceName(sourceValue) : "unknown";
        var status = thread.TryGetProperty("status", out var statusValue) && statusValue.TryGetProperty("type", out var typeValue) ? typeValue.GetString() ?? "unknown" : "unknown";
        var title = string.IsNullOrWhiteSpace(name) ? FirstLine(preview, "未命名会话") : name;

        return new SessionRo(
            thread.GetProperty("id").GetString() ?? string.Empty,
            thread.GetProperty("sessionId").GetString() ?? string.Empty,
            title!, preview, cwd, ProjectName(cwd), status, source, branch,
            thread.TryGetProperty("agentNickname", out var nickname) && nickname.ValueKind == JsonValueKind.String ? nickname.GetString() : null,
            thread.GetProperty("createdAt").GetInt64(),
            thread.GetProperty("updatedAt").GetInt64());
    }

    private static string SourceName(JsonElement source) => source.ValueKind == JsonValueKind.String
        ? source.GetString() ?? "unknown"
        : source.EnumerateObject().FirstOrDefault().Name ?? "unknown";

    private static string FirstLine(string text, string fallback)
    {
        var value = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string ProjectName(string path) => string.IsNullOrWhiteSpace(path)
        ? "未知项目"
        : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string CreateId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
