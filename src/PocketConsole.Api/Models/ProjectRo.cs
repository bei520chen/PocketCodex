namespace PocketConsole.Api.Models;

public sealed record ProjectRo(
    string Id,
    string Name,
    string WorkingDirectory,
    string? Branch,
    int SessionCount,
    long LastActiveAt,
    string LastSessionTitle);
