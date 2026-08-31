using System.IO.Pipes;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public sealed class DiscordRpcTransport : IDiscordRpcTransport
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private int _disposed;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_pipe is not null)
        {
            throw new InvalidOperationException("This Discord RPC transport has already been used.");
        }

        for (var index = 0; index < 10; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipeName = $"discord-ipc-{index}";
            var candidate = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
                await candidate.ConnectAsync(attempt.Token).ConfigureAwait(false);
                _pipe = candidate;
                return $@"\\?\pipe\{pipeName}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                candidate.Dispose();
            }
            catch (IOException)
            {
                candidate.Dispose();
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        throw new IOException(
            "Discord Local RPC pipe discord-ipc-0 through discord-ipc-9 could not be opened.");
    }

    public Task<DiscordRpcPacket> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return DiscordRpcPacketCodec.ReadAsync(_pipe!, cancellationToken);
    }

    public async Task WriteAsync(
        int opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DiscordRpcPacketCodec.WriteAsync(
                    _pipe!,
                    opcode,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _pipe?.Dispose();
        _pipe = null;
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new IOException("Discord Local RPC is not connected.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
