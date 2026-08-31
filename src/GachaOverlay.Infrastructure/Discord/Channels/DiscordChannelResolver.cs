using System.Text.Json;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Channels;

public sealed class DiscordChannelResolver : IDiscordChannelResolver
{
    private readonly IRuntimeMetrics? _metrics;

    public DiscordChannelResolver(IRuntimeMetrics? metrics = null)
    {
        _metrics = metrics;
    }

    public async Task<DiscordTargetChannels> ResolveAsync(
        IDiscordRpcClient rpcClient,
        DiscordTargetOptions options,
        CancellationToken cancellationToken)
    {
        var guildResponse = await rpcClient.CommandAsync(
                "GET_GUILDS",
                new { },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(guildResponse);
        var guilds = ParseGuilds(guildResponse);

        if (!string.IsNullOrWhiteSpace(options.GuildId))
        {
            var configuredGuild = guilds.SingleOrDefault(guild =>
                string.Equals(guild.Id, options.GuildId, StringComparison.Ordinal));
            if (configuredGuild is null)
            {
                throw new DiscordChannelResolutionException(
                    "The configured Discord Guild ID is not available to the authenticated user.");
            }

            return await ResolveInGuildAsync(
                    rpcClient,
                    configuredGuild,
                    options,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DiscordChannelResolutionException(
                    "The target channels were not found in the configured Discord Guild.");
        }

        var candidates = new List<DiscordTargetChannels>();
        foreach (var guild in guilds)
        {
            var candidate = await ResolveInGuildAsync(
                    rpcClient,
                    guild,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new DiscordChannelResolutionException(
                "No Discord Guild contains both exact target channel names."),
            _ => throw new DiscordChannelResolutionException(
                "Multiple Discord Guilds contain the target channels; configure a Guild ID."),
        };
    }

    private async Task<DiscordTargetChannels?> ResolveInGuildAsync(
        IDiscordRpcClient rpcClient,
        GuildDescriptor guild,
        DiscordTargetOptions options,
        CancellationToken cancellationToken)
    {
        _metrics?.Increment(RuntimeMetricNames.RpcGetChannels);
        var channelResponse = await rpcClient.CommandAsync(
                "GET_CHANNELS",
                new { guild_id = guild.Id },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(channelResponse);
        var textChannels = ParseChannels(channelResponse)
            .Where(channel => channel.Type == 0)
            .ToArray();

        if (options.RequireConfiguredMainChannel &&
            string.IsNullOrWhiteSpace(options.MainChannelId))
        {
            throw new DiscordChannelResolutionException(
                "A main Discord Channel must be selected before bootstrap.");
        }

        var main = ResolveChannel(
            textChannels,
            options.MainChannelId,
            options.MainChannelName,
            "main");
        var sales = ResolveChannel(
            textChannels,
            options.SalesChannelId,
            options.SalesChannelName,
            "sales");

        if (main is null || sales is null)
        {
            return null;
        }

        if (string.Equals(main.Id, sales.Id, StringComparison.Ordinal))
        {
            throw new DiscordChannelResolutionException(
                "The main and sales target resolved to the same Discord channel.");
        }

        return new DiscordTargetChannels(
            guild.Id,
            guild.Name,
            main.Id,
            main.Name,
            sales.Id,
            sales.Name);
    }

    private static ChannelDescriptor? ResolveChannel(
        IReadOnlyList<ChannelDescriptor> channels,
        string? configuredId,
        string exactName,
        string role)
    {
        if (!string.IsNullOrWhiteSpace(configuredId))
        {
            return channels.SingleOrDefault(channel =>
                    string.Equals(channel.Id, configuredId, StringComparison.Ordinal))
                ?? throw new DiscordChannelResolutionException(
                    $"The configured {role} Channel ID was not found in its Guild.");
        }

        var matches = channels
            .Where(channel => string.Equals(channel.Name, exactName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new DiscordChannelResolutionException(
                $"The {role} channel name '{exactName}' is ambiguous in one Guild."),
        };
    }

    private static IReadOnlyList<GuildDescriptor> ParseGuilds(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("guilds", out var guilds) ||
            guilds.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GET_GUILDS returned no guild array.");
        }

        return guilds.EnumerateArray()
            .Select(guild => new GuildDescriptor(
                DiscordJson.GetString(guild, "id") ?? string.Empty,
                DiscordJson.GetString(guild, "name") ?? string.Empty))
            .Where(guild => !string.IsNullOrWhiteSpace(guild.Id))
            .ToArray();
    }

    private static IReadOnlyList<ChannelDescriptor> ParseChannels(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GET_CHANNELS returned no channel array.");
        }

        return channels.EnumerateArray()
            .Select(channel => new ChannelDescriptor(
                DiscordJson.GetString(channel, "id") ?? string.Empty,
                DiscordJson.GetString(channel, "name") ?? string.Empty,
                DiscordJson.GetInt32(channel, "type") ?? -1))
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Id))
            .ToArray();
    }

    private sealed record GuildDescriptor(string Id, string Name);

    private sealed record ChannelDescriptor(string Id, string Name, int Type);
}
