using System.Diagnostics;
using System.Text;

namespace PocketConsole.Api.Services;

public sealed partial class CodexAppServerClient
{
    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) return;
            await StopAsync();
            _executablePath = resolver.Resolve();
            _process = StartProcess(_executablePath);
            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _startedAt = DateTimeOffset.UtcNow;
            _ = ReadLoopAsync(_process);
            _ = LogErrorLoopAsync(_process);
            await InitializeAsync(cancellationToken);
        }
        finally { _startLock.Release(); }
    }

    private static Process StartProcess(string executablePath) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Unable to start Codex App Server.");
}
