using System.Collections.Concurrent;
using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Channels;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Process;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Connection;

internal sealed class AlwaysRunningDiscordProcessService : IDiscordProcessService
{
    public bool IsDiscordRunning() => true;

    public Task WaitUntilDiscordIsRunningAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class NeverRunningDiscordProcessService : IDiscordProcessService
{
    public bool IsDiscordRunning() => false;

    public Task WaitUntilDiscordIsRunningAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

internal sealed class FakeCredentialProvider : IDiscordCredentialProvider
{
    public bool TryGetCredentials(out DiscordCredentials? credentials)
    {
        credentials = new DiscordCredentials("client-id", "secret", "https://127.0.0.1");
        return true;
    }
}

internal sealed class FakeProtectedCredentialStore : IDiscordProtectedCredentialStore
{
    public string? ClientSecret { get; set; }

    public DiscordOAuthToken? Token { get; set; }

    public bool FailSecretRead { get; set; }

    public bool FailTokenRead { get; set; }

    public ProtectedCredentialStatus ClientSecretStatus => FailSecretRead
        ? ProtectedCredentialStatus.Unreadable
        : ClientSecret is null
            ? ProtectedCredentialStatus.Missing
            : ProtectedCredentialStatus.Available;

    public ProtectedCredentialStatus OAuthTokenStatus => FailTokenRead
        ? ProtectedCredentialStatus.Unreadable
        : Token is null
            ? ProtectedCredentialStatus.Missing
            : ProtectedCredentialStatus.Available;

    public bool TryLoadClientSecret(out string? clientSecret)
    {
        clientSecret = FailSecretRead ? null : ClientSecret;
        return clientSecret is not null;
    }

    public bool SaveClientSecret(string clientSecret)
    {
        ClientSecret = clientSecret;
        FailSecretRead = false;
        return true;
    }

    public bool TryLoadOAuthToken(out DiscordOAuthToken? token)
    {
        token = FailTokenRead ? null : Token;
        return token is not null;
    }

    public bool SaveOAuthToken(DiscordOAuthToken token)
    {
        Token = token;
        FailTokenRead = false;
        return true;
    }

    public void ClearOAuthToken()
    {
        Token = null;
        FailTokenRead = false;
    }
}

internal sealed class FakeAuthenticationService : IDiscordAuthenticationService
{
    public int AuthenticationCount { get; private set; }

    public Task<DiscordAuthenticationResult> AuthenticateAsync(
        IDiscordRpcClient rpcClient,
        DiscordCredentials credentials,
        CancellationToken cancellationToken)
    {
        AuthenticationCount++;
        return Task.FromResult(new DiscordAuthenticationResult("user-id", "User"));
    }
}

internal sealed class FakeChannelResolver : IDiscordChannelResolver
{
    public static DiscordTargetChannels Targets { get; } = new(
        "guild",
        "Guild",
        "main",
        "🏠메인",
        "sales",
        "🚒판매모집");

    public int ResolutionCount { get; private set; }

    public Task<DiscordTargetChannels> ResolveAsync(
        IDiscordRpcClient rpcClient,
        DiscordTargetOptions options,
        CancellationToken cancellationToken)
    {
        ResolutionCount++;
        return Task.FromResult(Targets);
    }
}

internal sealed class FakeRpcClientFactory : IDiscordRpcClientFactory
{
    private readonly ConcurrentQueue<FakeRpcClient> _clients;

    public FakeRpcClientFactory(params FakeRpcClient[] clients)
    {
        _clients = new ConcurrentQueue<FakeRpcClient>(clients);
    }

    public int CreateCount { get; private set; }

    public IDiscordRpcClient Create()
    {
        CreateCount++;
        return _clients.TryDequeue(out var client)
            ? client
            : throw new InvalidOperationException("No fake RPC client remains.");
    }
}

internal sealed class FakeRpcClient : IDiscordRpcClient
{
    private readonly TaskCompletionSource<Exception?> _disconnect =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? ConnectException { get; init; }

    public int SubscriptionCount { get; private set; }

    public int GetChannelCount { get; private set; }

    public int UnsubscriptionCount { get; private set; }

    public ConcurrentQueue<string> RequestedChannelIds { get; } = new();

    public Func<string, CancellationToken, Task<JsonElement>>? GetChannelAsync { get; init; }

    public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? CommandHandler { get; init; }

    public ConcurrentQueue<string> SubscribedChannelIds { get; } = new();

    public ConcurrentQueue<string> UnsubscribedChannelIds { get; } = new();

    public event Action<JsonElement>? DispatchReceived;

    public Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectException is not null)
        {
            return Task.FromException<string>(ConnectException);
        }

        return Task.FromResult(@"\\?\pipe\discord-ipc-test");
    }

    public Task<JsonElement> HandshakeAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(Parse("{\"evt\":\"READY\"}"));

    public Task<JsonElement> CommandAsync(
        string command,
        object arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var serializedArguments = JsonSerializer.SerializeToElement(arguments);
        if (CommandHandler is not null)
        {
            return CommandHandler(command, serializedArguments, cancellationToken);
        }

        if (string.Equals(command, "GET_CHANNEL", StringComparison.Ordinal))
        {
            GetChannelCount++;
            var channelId = serializedArguments.TryGetProperty("channel_id", out var channel)
                ? channel.GetString() ?? string.Empty
                : string.Empty;
            RequestedChannelIds.Enqueue(channelId);
            if (GetChannelAsync is not null)
            {
                return GetChannelAsync(channelId, cancellationToken);
            }

            return Task.FromResult(Parse("{\"data\":{\"messages\":[]}}"));
        }

        return Task.FromResult(Parse("{\"data\":{}}"));
    }

    public Task<JsonElement> SubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        SubscriptionCount++;
        var serializedArguments = JsonSerializer.SerializeToElement(arguments);
        if (serializedArguments.TryGetProperty("channel_id", out var channel))
        {
            SubscribedChannelIds.Enqueue(channel.GetString() ?? string.Empty);
        }

        return Task.FromResult(Parse("{\"data\":{}}"));
    }

    public Task<JsonElement> UnsubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        UnsubscriptionCount++;
        var serializedArguments = JsonSerializer.SerializeToElement(arguments);
        if (serializedArguments.TryGetProperty("channel_id", out var channel))
        {
            UnsubscribedChannelIds.Enqueue(channel.GetString() ?? string.Empty);
        }

        return Task.FromResult(Parse("{\"data\":{}}"));
    }

    public async Task<Exception?> WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        await _disconnect.Task.WaitAsync(cancellationToken);

    public void Disconnect(Exception? exception = null) => _disconnect.TrySetResult(exception);

    public void Publish(JsonElement dispatch) => DispatchReceived?.Invoke(dispatch);

    public ValueTask DisposeAsync()
    {
        _disconnect.TrySetResult(null);
        return ValueTask.CompletedTask;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal sealed class ImmediateReconnectDelayStrategy : IReconnectDelayStrategy
{
    public int CallCount { get; private set; }

    public Task DelayAsync(int consecutiveFailureCount, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}

internal sealed class BlockingReconnectDelayStrategy : IReconnectDelayStrategy
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task DelayAsync(int consecutiveFailureCount, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
            }
        }
    }
}
