namespace PocketConsole.Api.Options;

public sealed class CodexOptions
{
    public const string SectionName = "Codex";
    public string ExecutablePath { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
}
