using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public sealed class DiscordRpcClient : IDiscordRpcClient
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private readonly IDiscordRpcTransport _transport;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _sessionShutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<Exception?> _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<JsonElement> _dispatchQueue = Channel.CreateBounded<JsonElement>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private Task? _readerTask;
    private Task? _dispatchTask;
    private int _disposed;

    public DiscordRpcClient(IDiscordRpcTransport transport, IAppLogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    public event Action<JsonElement>? DispatchReceived;

    public Task<string> ConnectAsync(CancellationToken cancellationToken) =>
        _transport.ConnectAsync(cancellationToken);

    public async Task<JsonElement> HandshakeAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            client_id = clientId,
        });

        await _transport.WriteAsync(OpHandshake, payload, cancellationToken).ConfigureAwait(false);
        var response = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (response.Opcode == OpClose)
        {
            throw new IOException("Discord closed the Local RPC connection during handshake.");
        }

        if (response.Opcode != OpFrame)
        {
            throw new InvalidDataException(
                $"Unexpected Discord RPC handshake opcode: {response.Opcode}.");
        }

        using var document = JsonDocument.Parse(response.Payload);
        var ready = document.RootElement.Clone();
        if (!ready.TryGetProperty("evt", out var eventElement) ||
            !string.Equals(eventElement.GetString(), "READY", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Discord RPC handshake did not return READY.");
        }

        _dispatchTask = Task.Run(DispatchLoopAsync);
        _readerTask = Task.Run(() => ReaderLoopAsync(_sessionShutdown.Token));
        return ready;
    }

    public Task<JsonElement> CommandAsync(
        string command,
        object arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            command,
            null,
            arguments,
            timeout ?? TimeSpan.FromSeconds(20),
            cancellationToken);

    public Task<JsonElement> SubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            "SUBSCRIBE",
            eventName,
            arguments,
            TimeSpan.FromSeconds(20),
            cancellationToken);

    public Task<JsonElement> UnsubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            "UNSUBSCRIBE",
            eventName,
            arguments,
            TimeSpan.FromSeconds(20),
            cancellationToken);

    public async Task<Exception?> WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        await _disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sessionShutdown.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        FailAll(new OperationCanceledException("Discord RPC client disposed."));
        _disconnected.TrySetResult(null);
        _sessionShutdown.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(
        string command,
        string? eventName,
        object arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var nonce = Guid.NewGuid().ToString("D");
        var request = new Dictionary<string, object?>
        {
            ["cmd"] = command,
            ["nonce"] = nonce,
            ["args"] = arguments,
        };

        if (!string.IsNullOrWhiteSpace(eventName))
        {
            request["evt"] = eventName;
        }

        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(nonce, completion))
        {
            throw new InvalidOperationException("A Discord RPC nonce collision occurred.");
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            await _transport.WriteAsync(OpFrame, payload, cancellationToken).ConfigureAwait(false);

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _sessionShutdown.Token);
            requestTimeout.CancelAfter(timeout);

            try
            {
                return await completion.Task.WaitAsync(requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                !_sessionShutdown.IsCancellationRequested)
            {
                throw new TimeoutException(
                    eventName is null
                        ? $"Discord RPC request timed out: {command}."
                        : $"Discord RPC request timed out: {command}/{eventName}.");
            }
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        Exception? disconnectReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
                switch (packet.Opcode)
                {
                    case OpPing:
                        await _transport.WriteAsync(OpPong, packet.Payload, cancellationToken)
                            .ConfigureAwait(false);
                        continue;

                    case OpClose:
                        throw new IOException("Discord closed the Local RPC connection.");

                    case not OpFrame:
                        continue;
                }

                using var document = JsonDocument.Parse(packet.Payload);
                var root = document.RootElement.Clone();
                if (TryCompletePending(root))
                {
                    continue;
                }

                if (root.TryGetProperty("cmd", out var commandElement) &&
                    string.Equals(commandElement.GetString(), "DISPATCH", StringComparison.Ordinal))
                {
                    await _dispatchQueue.Writer.WriteAsync(root, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                _logger.Warning("RPC", "Received an unsolicited RPC frame without a matching nonce.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            disconnectReason = exception;
            _logger.Warning("RPC", $"Reader stopped: {exception.GetType().Name}.");
        }
        finally
        {
            _dispatchQueue.Writer.TryComplete();
            if (_dispatchTask is not null)
            {
                await _dispatchTask.ConfigureAwait(false);
            }

            FailAll(disconnectReason ?? new IOException("Discord RPC reader stopped."));
            _disconnected.TrySetResult(disconnectReason);
        }
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var payload in _dispatchQueue.Reader.ReadAllAsync())
        {
            Dispatch(payload);
        }
    }

    private bool TryCompletePending(JsonElement root)
    {
        if (!root.TryGetProperty("nonce", out var nonceElement) ||
            nonceElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var nonce = nonceElement.GetString();
        if (nonce is null || !_pending.TryRemove(nonce, out var pending))
        {
            return false;
        }

        pending.TrySetResult(root);
        return true;
    }

    private void Dispatch(JsonElement payload)
    {
        var handlers = DispatchReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<JsonElement> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(payload);
            }
            catch (Exception exception)
            {
                _logger.Error("RPC", "A dispatch subscriber failed.", exception);
            }
        }
    }

    private void FailAll(Exception exception)
    {
        foreach (var entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }
}
