namespace PocketConsole.Api.Models;

public sealed record AttachmentRo(
    string Id,
    string Name,
    string ContentType,
    long Size,
    bool IsImage);
