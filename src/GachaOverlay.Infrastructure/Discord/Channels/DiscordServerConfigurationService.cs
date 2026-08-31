using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Process;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Channels;

public sealed class DiscordServerConfigurationService : IDiscordServerConfigurationService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly IDiscordProcessService _processService;
    private readonly IDiscordCredentialProvider _credentialProvider;
    private readonly IDiscordRpcClientFactory _clientFactory;
    private readonly IDiscordAuthenticationService _authenticationService;
    private readonly IAppLogger _logger;
    private Task<DiscordServerDiscoverySnapshot>? _inFlight;
    private DiscordServerDiscoverySnapshot? _cache;
    private DateTimeOffset _cacheTime;
    private long _epoch;
    private long _requestRevision;

    public DiscordServerConfigurationService(
        IDiscordProcessService processService,
        IDiscordCredentialProvider credentialProvider,
        IDiscordRpcClientFactory clientFactory,
        IDiscordAuthenticationService authenticationService,
        IAppLogger logger)
    {
        _processService = processService;
        _credentialProvider = credentialProvider;
        _clientFactory = clientFactory;
        _authenticationService = authenticationService;
        _logger = logger;
    }

    public Task<DiscordServerDiscoverySnapshot> DiscoverAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_inFlight is not null)
            {
                return _inFlight.WaitAsync(cancellationToken);
            }

            if (!forceRefresh && _cache is not null &&
                DateTimeOffset.UtcNow - _cacheTime < CacheDuration)
            {
                return Task.FromResult(_cache);
            }

            var epoch = _epoch;
            var revision = Interlocked.Increment(ref _requestRevision);
            _inFlight = DiscoverCoreAsync(epoch, revision);
            return _inFlight.WaitAsync(cancellationToken);
        }
    }

    public async Task<bool> ValidateMainChannelAsync(
        DiscordMainChannelOption channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var discovery = await DiscoverAsync(false, cancellationToken).ConfigureAwait(false);
        if (discovery.State != DiscordServerDiscoveryState.Ready ||
            !discovery.MainChannels.Any(candidate => string.Equals(
                candidate.ChannelId,
                channel.ChannelId,
                StringComparison.Ordinal)))
        {
            return false;
        }

        return await WithAuthenticatedClientAsync(
                async (client, token) =>
                {
                    var response = await client.CommandAsync(
                            "GET_CHANNEL",
                            new { channel_id = channel.ChannelId },
                            cancellationToken: token)
                        .ConfigureAwait(false);
                    DiscordRpcProtocol.EnsureSuccess(response);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _epoch++;
            _cache = null;
            _cacheTime = default;
        }
    }

    private async Task<DiscordServerDiscoverySnapshot> DiscoverCoreAsync(
        long epoch,
        long revision)
    {
        await Task.Yield();
        DiscordServerDiscoverySnapshot result;
        try
        {
            if (!_processService.IsDiscordRunning())
            {
                result = DiscordServerDiscoverySnapshot.Unavailable(
                    DiscordServerDiscoveryState.DiscordNotRunning,
                    revision);
            }
            else if (!_credentialProvider.TryGetCredentials(out var credentials) || credentials is null)
            {
                result = DiscordServerDiscoverySnapshot.Unavailable(
                    DiscordServerDiscoveryState.CredentialsMissing,
                    revision);
            }
            else
            {
                result = await WithAuthenticatedClientAsync(
                        async (client, token) => await ReadServerAsync(client, revision, token)
                            .ConfigureAwait(false),
                        CancellationToken.None,
                        credentials)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(
                "SERVER",
                $"Discord server discovery failed ({exception.GetType().Name}).");
            result = DiscordServerDiscoverySnapshot.Unavailable(
                DiscordServerDiscoveryState.Failed,
                revision);
        }

        lock (_sync)
        {
            _inFlight = null;
            if (epoch == _epoch)
            {
                _cache = result;
                _cacheTime = DateTimeOffset.UtcNow;
            }
            else
            {
                result = result with { IsStale = true };
            }
        }

        return result;
    }

    private async Task<DiscordServerDiscoverySnapshot> ReadServerAsync(
        IDiscordRpcClient client,
        long revision,
        CancellationToken cancellationToken)
    {
        var guildResponse = await client.CommandAsync(
                "GET_GUILDS",
                new { },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(guildResponse);
        var guild = ParseGuilds(guildResponse).SingleOrDefault(candidate => string.Equals(
            candidate.Id,
            ProductionServerProfile.GuildId,
            StringComparison.Ordinal));
        if (guild is null)
        {
            return DiscordServerDiscoverySnapshot.Unavailable(
                DiscordServerDiscoveryState.TargetGuildMissing,
                revision);
        }

        var channelResponse = await client.CommandAsync(
                "GET_CHANNELS",
                new { guild_id = ProductionServerProfile.GuildId },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(channelResponse);
        var channels = ParseChannels(channelResponse);
        var sales = channels.SingleOrDefault(candidate => string.Equals(
            candidate.Id,
            ProductionServerProfile.SalesChannelId,
            StringComparison.Ordinal));
        var main = channels
            .Where(candidate => candidate.Type == 0 && !string.Equals(
                candidate.Id,
                ProductionServerProfile.SalesChannelId,
                StringComparison.Ordinal))
            .Select(candidate => new DiscordMainChannelOption(candidate.Id, candidate.Name))
            .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new DiscordServerDiscoverySnapshot(
            DiscordServerDiscoveryState.Ready,
            guild.Name,
            sales?.Name,
            main,
            revision);
    }

    private async Task<T> WithAuthenticatedClientAsync<T>(
        Func<IDiscordRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        DiscordCredentials? suppliedCredentials = null)
    {
        var credentials = suppliedCredentials;
        if (credentials is null &&
            (!_credentialProvider.TryGetCredentials(out credentials) || credentials is null))
        {
            throw new InvalidOperationException("Discord credentials are not configured.");
        }

        await using var client = _clientFactory.Create();
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await client.HandshakeAsync(credentials.ClientId, cancellationToken).ConfigureAwait(false);
        await _authenticationService.AuthenticateAsync(client, credentials, cancellationToken)
            .ConfigureAwait(false);
        return await operation(client, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<Descriptor> ParseGuilds(JsonElement response) =>
        ParseArray(response, "guilds")
            .Select(item => new Descriptor(
                DiscordJson.GetString(item, "id") ?? string.Empty,
                DiscordJson.GetString(item, "name") ?? string.Empty,
                -1))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();

    private static IReadOnlyList<Descriptor> ParseChannels(JsonElement response) =>
        ParseArray(response, "channels")
            .Select(item => new Descriptor(
                DiscordJson.GetString(item, "id") ?? string.Empty,
                DiscordJson.GetString(item, "name") ?? string.Empty,
                DiscordJson.GetInt32(item, "type") ?? -1))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();

    private static IEnumerable<JsonElement> ParseArray(JsonElement response, string propertyName)
    {
        if (!response.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Discord response returned no {propertyName} array.");
        }

        return array.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private sealed record Descriptor(string Id, string Name, int Type);
}
