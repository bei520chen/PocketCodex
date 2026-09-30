using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using PocketConsole.Api.Hubs;
using PocketConsole.Api.Models;
using PocketConsole.Api.Options;

namespace PocketConsole.Api.Services;

public sealed partial class CodexAppServerClient(
    CodexExecutableResolver resolver,
    IOptions<CodexOptions> options,
    IHubContext<CodexHub> hubContext,
    IServiceScopeFactory scopeFactory,
    ILogger<CodexAppServerClient> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private long _requestId;
    private string _userAgent = "unknown";
    private string _executablePath = string.Empty;
    private DateTimeOffset _startedAt;

    public async Task<HostStatusRo> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return new HostStatusRo(true, _userAgent, _executablePath, _startedAt);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return await SendCoreAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonElement> SendCoreAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await WriteAsync(new { method, id, @params = parameters }, cancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await completion.Task.WaitAsync(linked.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _startLock.Dispose();
        _writeLock.Dispose();
    }
}
