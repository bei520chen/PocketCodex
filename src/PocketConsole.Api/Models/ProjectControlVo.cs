namespace PocketConsole.Api.Models;

public sealed record CreateProjectVo(string Name, string WorkingDirectory, bool CreateDirectory = true);

public sealed record CreateSessionVo(string? ProjectPath, string Message, IReadOnlyList<string>? AttachmentIds = null);
