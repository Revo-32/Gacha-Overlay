using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;
using LSOverlay.Backend.Transport;

namespace LSOverlay.Backend.Chat;

internal sealed record ChatMemberSnapshot(
    ulong UserId,
    IReadOnlyCollection<ulong> RoleIds);

internal sealed record ChatChannelSnapshot(
    ChatChannelDescriptor Descriptor,
    IReadOnlyCollection<ChatPermissionOverwrite> Overwrites);

internal sealed record ChatGuildSnapshot(
    ulong GuildId,
    IReadOnlyCollection<ChatRolePermission> Roles,
    ChatMemberSnapshot User,
    ChatMemberSnapshot Bot,
    IReadOnlyCollection<ChatChannelSnapshot> Channels);

internal enum ChatSourceStatus
{
    Available,
    NotMember,
    NotFound,
    Unavailable,
}

internal sealed record ChatGuildSourceResult(
    ChatSourceStatus Status,
    ChatGuildSnapshot? Guild);

internal sealed record ChatMessagesSourceResult(
    ChatSourceStatus Status,
    IReadOnlyList<IMessage> Messages);

internal sealed record ChatMessageSourceResult(
    ChatSourceStatus Status,
    IMessage? Message);

internal interface IChatDiscordSource
{
    Task<ChatGuildSourceResult> GetGuildAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken);

    Task<ChatMessagesSourceResult> GetRecentMessagesAsync(
        ulong channelId,
        int limit,
        CancellationToken cancellationToken);

    Task<ChatMessageSourceResult> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNetChatSource : IChatDiscordSource
{
    private readonly DiscordSocketClient _client;

    public DiscordNetChatSource(DiscordSocketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ChatGuildSourceResult> GetGuildAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rest = _client.Rest;
            // HTTP can be listening while Discord is still logging in after a
            // Backend restart. Do not start requests or treat this as revocation.
            var botIdentity = rest.CurrentUser;
            if (botIdentity is null)
            {
                return new ChatGuildSourceResult(ChatSourceStatus.Unavailable, null);
            }

            var guildTask = StagingConnectionDiagnostic.MeasureAsync("rest.guild", rest.GetGuildAsync(identity.GuildId));
            var userTask = StagingConnectionDiagnostic.MeasureAsync("rest.member", rest.GetGuildUserAsync(identity.GuildId, identity.DiscordUserId));
            var botTask = StagingConnectionDiagnostic.MeasureAsync("rest.bot", rest.GetGuildUserAsync(identity.GuildId, botIdentity.Id));
            await Task.WhenAll(guildTask, userTask, botTask)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var guild = await guildTask.ConfigureAwait(false);
            var user = await userTask.ConfigureAwait(false);
            var bot = await botTask.ConfigureAwait(false);
            if (guild is null || user is null || bot is null)
            {
                return new ChatGuildSourceResult(ChatSourceStatus.NotMember, null);
            }

            var channels = await StagingConnectionDiagnostic.MeasureAsync("rest.channels", guild.GetChannelsAsync())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var snapshots = channels
                .Where(channel => channel.ChannelType is ChannelType.Text or ChannelType.News)
                .Select(channel => new ChatChannelSnapshot(
                    new ChatChannelDescriptor(
                        guild.Id,
                        channel.Id,
                        channel.Name,
                        channel.Position,
                        channel.ChannelType == ChannelType.News),
                    channel.PermissionOverwrites.Select(Map).ToArray()))
                .ToArray();
            return new ChatGuildSourceResult(
                ChatSourceStatus.Available,
                new ChatGuildSnapshot(
                    guild.Id,
                    guild.Roles.Select(role => new ChatRolePermission(
                        role.Id,
                        role.Permissions.RawValue)).ToArray(),
                    new ChatMemberSnapshot(user.Id, user.RoleIds.ToArray()),
                    new ChatMemberSnapshot(bot.Id, bot.RoleIds.ToArray()),
                    snapshots));
        }
        catch (HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ChatGuildSourceResult(ChatSourceStatus.NotMember, null);
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new ChatGuildSourceResult(ChatSourceStatus.Unavailable, null);
        }
    }

    public async Task<ChatMessagesSourceResult> GetRecentMessagesAsync(
        ulong channelId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        try
        {
            var messageChannel = await GetMessageChannelAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            if (messageChannel is null)
            {
                return new ChatMessagesSourceResult(ChatSourceStatus.NotFound, Array.Empty<IMessage>());
            }

            var pages = messageChannel.GetMessagesAsync(limit);
            var messages = await pages.FlattenAsync().WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ChatMessagesSourceResult(
                ChatSourceStatus.Available,
                messages.OrderBy(message => message.Timestamp)
                    .ThenBy(message => message.Id)
                    .TakeLast(limit)
                    .ToArray());
        }
        catch (HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ChatMessagesSourceResult(ChatSourceStatus.NotFound, Array.Empty<IMessage>());
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new ChatMessagesSourceResult(ChatSourceStatus.Unavailable, Array.Empty<IMessage>());
        }
    }

    public async Task<ChatMessageSourceResult> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var messageChannel = await GetMessageChannelAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            if (messageChannel is null)
            {
                return new ChatMessageSourceResult(ChatSourceStatus.NotFound, null);
            }

            var message = await messageChannel.GetMessageAsync(messageId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return message is null
                ? new ChatMessageSourceResult(ChatSourceStatus.NotFound, null)
                : new ChatMessageSourceResult(ChatSourceStatus.Available, message);
        }
        catch (HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ChatMessageSourceResult(ChatSourceStatus.NotFound, null);
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new ChatMessageSourceResult(ChatSourceStatus.Unavailable, null);
        }
    }

    private static ChatPermissionOverwrite Map(Overwrite overwrite) => new(
        overwrite.TargetId,
        overwrite.TargetType == PermissionTarget.Role
            ? ChatPermissionTarget.Role
            : ChatPermissionTarget.Member,
        overwrite.Permissions.AllowValue,
        overwrite.Permissions.DenyValue);

    private async Task<IMessageChannel?> GetMessageChannelAsync(
        ulong channelId,
        CancellationToken cancellationToken)
    {
        if (_client.GetChannel(channelId) is IMessageChannel cached)
        {
            return cached;
        }

        return await _client.Rest.GetChannelAsync(channelId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false) as IMessageChannel;
    }

    private static bool IsTemporary(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpException or TimeoutException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
