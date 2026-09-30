namespace PocketConsole.Api.Options;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public string Password { get; set; } = string.Empty;

    public string[] WorkspaceRoots { get; set; } = [];
}
