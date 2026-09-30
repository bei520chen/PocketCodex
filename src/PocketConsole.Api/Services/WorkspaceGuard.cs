using Microsoft.Extensions.Options;
using PocketConsole.Api.Options;

namespace PocketConsole.Api.Services;

public sealed class WorkspaceGuard(IOptions<SecurityOptions> options)
{
    private readonly string[] _roots = options.Value.WorkspaceRoots
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Normalize(Environment.ExpandEnvironmentVariables(path)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string Validate(string path)
    {
        if (_roots.Length == 0)
        {
            throw new InvalidOperationException("No workspace roots are configured.");
        }

        var candidate = Normalize(path);
        if (!_roots.Any(root => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("The selected project is outside the configured workspace roots.");
        }

        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException("The selected project directory does not exist.");
        }

        return candidate;
    }

    public string Prepare(string path, bool createDirectory)
    {
        if (_roots.Length == 0) throw new InvalidOperationException("No workspace roots are configured.");

        var candidate = Normalize(path);
        if (!_roots.Any(root => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("The selected project is outside the configured workspace roots.");

        if (!Directory.Exists(candidate))
        {
            if (!createDirectory) throw new DirectoryNotFoundException("The selected project directory does not exist.");
            Directory.CreateDirectory(candidate);
        }

        return candidate;
    }

    public string DefaultRoot => _roots.FirstOrDefault() ?? throw new InvalidOperationException("No workspace roots are configured.");

    public IReadOnlyList<string> GetRoots() => _roots;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
