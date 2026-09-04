using Discord;
using Discord.Net;
using Discord.WebSocket;
using GachaOverlay.Core.Gta;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Gta;

internal enum GtaEventSourceStatus
{
    Available,
    ChannelUnavailable,
    ViewChannelRequired,
    ReadHistoryRequired,
    TemporarilyUnavailable,
}

internal sealed record GtaEventHydrationSourceResult(
    GtaEventSourceStatus Status,
    IReadOnlyList<CanonicalEventDocument> Documents);

internal sealed record GtaEventMessageSourceResult(
    GtaEventSourceStatus Status,
    CanonicalEventDocument? Document);

internal interface IGtaEventDiscordSource
{
    CanonicalEventDocument Build(IMessage message);

    Task<GtaEventHydrationSourceResult> GetRecentAsync(int limit, CancellationToken cancellationToken);

    Task<GtaEventMessageSourceResult> GetMessageAsync(ulong messageId, CancellationToken cancellationToken);
}

internal sealed class DiscordNetGtaEventSource : IGtaEventDiscordSource
{
    private readonly DiscordSocketClient _client;
    private readonly Configuration.BackendConfiguration _configuration;
    private readonly CanonicalEventDocumentBuilder _builder;

    public DiscordNetGtaEventSource(
        DiscordSocketClient client,
        Configuration.BackendConfiguration configuration,
        CanonicalEventDocumentBuilder builder)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public CanonicalEventDocument Build(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var embeds = message.Embeds.Select(MapEmbed).ToArray();
        var forwarded = (message as IUserMessage)?.ForwardedMessages.Select(snapshot =>
            new GtaEventForwardInput(
                snapshot.Message.Content,
                snapshot.Message.Embeds.Select(MapEmbed).ToArray())).ToArray() ??
            Array.Empty<GtaEventForwardInput>();
        var publisher = forwarded.SelectMany(item => item.Embeds)
            .Concat(embeds)
            .SelectMany(embed => new[] { embed.ProviderName, embed.AuthorName })
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return _builder.Build(new GtaEventSourceInput(
            message.Id,
            message.Channel.Id,
            message.Timestamp,
            message.EditedTimestamp,
            message.Content,
            embeds,
            forwarded,
            publisher,
            null));
    }

    public async Task<GtaEventHydrationSourceResult> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var channelResult = ResolveChannel();
        if (channelResult.Status != GtaEventSourceStatus.Available || channelResult.Channel is null)
        {
            return new GtaEventHydrationSourceResult(channelResult.Status, Array.Empty<CanonicalEventDocument>());
        }

        try
        {
            var messages = await channelResult.Channel.GetMessagesAsync(limit).FlattenAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new GtaEventHydrationSourceResult(
                GtaEventSourceStatus.Available,
                messages.OrderByDescending(message => message.Timestamp)
                    .ThenByDescending(message => message.Id)
                    .Take(limit)
                    .Select(Build)
                    .ToArray());
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new GtaEventHydrationSourceResult(
                GtaEventSourceStatus.TemporarilyUnavailable,
                Array.Empty<CanonicalEventDocument>());
        }
    }

    public async Task<GtaEventMessageSourceResult> GetMessageAsync(
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var channelResult = ResolveChannel();
        if (channelResult.Status != GtaEventSourceStatus.Available || channelResult.Channel is null)
        {
            return new GtaEventMessageSourceResult(channelResult.Status, null);
        }

        try
        {
            var message = await channelResult.Channel.GetMessageAsync(messageId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return message is null
                ? new GtaEventMessageSourceResult(GtaEventSourceStatus.ChannelUnavailable, null)
                : new GtaEventMessageSourceResult(GtaEventSourceStatus.Available, Build(message));
        }
        catch (HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new GtaEventMessageSourceResult(GtaEventSourceStatus.ChannelUnavailable, null);
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new GtaEventMessageSourceResult(GtaEventSourceStatus.TemporarilyUnavailable, null);
        }
    }

    private (GtaEventSourceStatus Status, IMessageChannel? Channel) ResolveChannel()
    {
        var guild = _client.GetGuild(_configuration.TargetGuildId);
        var channel = guild?.GetTextChannel(GtaCompanionProtocolPolicy.ProductionEventChannelId);
        if (guild is null || channel is null)
        {
            return (GtaEventSourceStatus.ChannelUnavailable, null);
        }

        var permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel)
        {
            return (GtaEventSourceStatus.ViewChannelRequired, null);
        }

        if (!permissions.ReadMessageHistory)
        {
            return (GtaEventSourceStatus.ReadHistoryRequired, null);
        }

        return (GtaEventSourceStatus.Available, channel);
    }

    private static GtaEventEmbedInput MapEmbed(IEmbed embed) => new(
        embed.Title,
        embed.Description,
        embed.Fields.Select(field => new GtaEventEmbedFieldInput(field.Name, field.Value)).ToArray(),
        embed.Provider?.Name,
        embed.Author?.Name);

    private static bool IsTemporary(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpException or TimeoutException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
