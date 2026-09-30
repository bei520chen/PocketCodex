using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace PocketConsole.Api.Services;

public sealed partial class CodexAppServerClient
{
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await SendCoreAsync("initialize", new
        {
            clientInfo = new { name = "pocket-console", title = "Codex Pocket Console", version = "0.1.0" },
            capabilities = new { experimentalApi = false, requestAttestation = false }
        }, cancellationToken);
        _userAgent = result.TryGetProperty("userAgent", out var value) ? value.GetString() ?? "unknown" : "unknown";
        await WriteAsync(new { method = "initialized" }, cancellationToken);
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var input = _input ?? throw new InvalidOperationException("Codex App Server stdin is unavailable.");
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(Process process)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            try { HandleMessages(line); }
            catch (Exception exception) { logger.LogWarning(exception, "Invalid Codex App Server output."); }
        }
        FailPending(new InvalidOperationException("Codex App Server stopped unexpectedly."));
    }

    private void HandleMessages(string line)
    {
        var repairedMessage = line;
        for (var attempt = 0; attempt < 512; attempt++)
        {
            try
            {
                using var document = JsonDocument.Parse(repairedMessage);
                HandleMessage(document.RootElement);
                return;
            }
            catch (JsonException exception) when (TryRepairMissingStringQuote(repairedMessage, exception, out var repaired))
            {
                repairedMessage = repaired;
            }
        }

        throw new JsonException("Codex App Server history output could not be repaired.");
    }

    private static bool TryRepairMissingStringQuote(string message, JsonException exception, out string repaired)
    {
        repaired = message;
        if (!exception.Message.Contains("invalid after a value", StringComparison.OrdinalIgnoreCase)) return false;

        var bytes = Encoding.UTF8.GetBytes(message);
        var bytePosition = (int)Math.Clamp(exception.BytePositionInLine ?? 0, 0, bytes.Length);
        var characterPosition = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, bytePosition));
        var delimiterPosition = message.LastIndexOf(",\"", Math.Min(characterPosition, message.Length - 1), StringComparison.Ordinal);
        if (delimiterPosition <= 0 || message[delimiterPosition - 1] == '"') return false;

        repaired = message.Insert(delimiterPosition, "\"");
        return true;
    }

    private void HandleMessage(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
        {
            if (root.TryGetProperty("method", out var method))
            {
                var methodName = method.GetString() ?? "unknown";
                var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
                _ = hubContext.Clients.All.SendAsync("codex:event", methodName, parameters);
                if (methodName == "turn/completed") _ = SynchronizeTaskAsync(parameters);
            }
            return;
        }
        if (!_pending.TryRemove(id, out var completion)) return;
        if (root.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.ToString()));
        else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
    }

    private async Task LogErrorLoopAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line) logger.LogDebug("Codex: {Line}", line);
    }
}
