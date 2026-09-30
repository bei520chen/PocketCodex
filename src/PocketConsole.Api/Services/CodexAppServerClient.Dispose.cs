namespace PocketConsole.Api.Services;

public sealed partial class CodexAppServerClient
{
    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values) completion.TrySetException(exception);
        _pending.Clear();
    }

    private async Task StopAsync()
    {
        if (_process is null) return;
        try
        {
            _input?.Close();
            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
        }
        catch (Exception exception) { logger.LogDebug(exception, "Unable to stop Codex App Server cleanly."); }
        _process.Dispose();
        _process = null;
        _input = null;
    }
}
