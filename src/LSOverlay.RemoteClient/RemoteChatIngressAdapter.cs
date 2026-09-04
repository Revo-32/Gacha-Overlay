using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Providers;
using LSOverlay.Protocol;

namespace LSOverlay.RemoteClient;

/// <summary>
/// Maps an authenticated remote channel stream into the transport-neutral application store.
/// </summary>
public sealed class RemoteChatIngressAdapter : IDisposable
{
    private const string UnusedSalesChannelId = "remote-chat-unused-sales";

    private readonly IOverlayMessageIngress _ingress;
    private readonly ILSOverlayRemoteClient _client;
    private readonly long _ingressGeneration;
    private bool _initialized;
    private bool _disposed;

    public RemoteChatIngressAdapter(
        IOverlayMessageIngress ingress,
        ILSOverlayRemoteClient client,
        long ingressGeneration,
        string? authenticatedUserId)
    {
        if (ingressGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ingressGeneration));
        }

        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ingressGeneration = ingressGeneration;
        if (!string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            _ingress.SetAuthenticatedUser(authenticatedUserId);
        }
        _client.ChatChannelReady += OnChannelReady;
        _client.ChatMutationReceived += OnMutation;
    }

    public void SetAuthenticatedUser(string authenticatedUserId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        _ingress.SetAuthenticatedUser(authenticatedUserId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.ChatChannelReady -= OnChannelReady;
        _client.ChatMutationReceived -= OnMutation;
    }

    public bool ApplyBootstrap(ChatBootstrapResponse bootstrap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bootstrap);
        var targets = Targets(bootstrap.Channel);
        var snapshot = bootstrap.RecentMessages.Select(MapPatch).ToArray();
        if (!_initialized)
        {
            if (!_ingress.StartBootstrap(_ingressGeneration, targets))
            {
                return false;
            }

            _initialized = _ingress.CompleteBootstrap(
                _ingressGeneration,
                snapshot,
                Array.Empty<DiscordMessagePatch>());
            return _initialized;
        }

        return _ingress.ReplaceMain(_ingressGeneration, targets, snapshot);
    }

    private void OnChannelReady(ChatBootstrapResponse bootstrap) => ApplyBootstrap(bootstrap);

    private void OnMutation(ChatMutationEnvelope envelope)
    {
        if (!_initialized)
        {
            return;
        }

        var mutation = envelope.EventType switch
        {
            OverlayTransportProtocol.ChatMessageCreate when envelope.Message is not null =>
                DiscordMessageMutation.Create(MapPatch(envelope.Message)),
            OverlayTransportProtocol.ChatMessageUpdate when envelope.Message is not null =>
                DiscordMessageMutation.Update(MapPatch(envelope.Message)),
            OverlayTransportProtocol.ChatMessageDelete =>
                DiscordMessageMutation.Delete(
                    envelope.MessageId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    envelope.ChannelId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
            _ => null,
        };
        if (mutation is not null)
        {
            _ingress.ReceiveLive(_ingressGeneration, mutation);
        }
    }

    public static NormalizedDiscordMessage MapNormalizedMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var store = new DiscordMessageStore();
        if (store.Apply(DiscordMessageMutation.Create(MapPatch(message))) !=
                MessageStoreMutationResult.Applied ||
            !store.TryGet(message.MessageId.ToString(
                System.Globalization.CultureInfo.InvariantCulture), out var normalized) ||
            normalized is null)
        {
            throw new InvalidDataException("Remote Discord message is missing required identity fields.");
        }

        return normalized;
    }

    private static DiscordTargetChannels Targets(ChatChannelDescriptor channel) => new(
        channel.GuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Remote Discord Guild",
        channel.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        channel.Name,
        UnusedSalesChannelId,
        "Unused");

    private static DiscordMessagePatch MapPatch(ChatMessage message)
    {
        var author = message.Author;
        return new DiscordMessagePatch(message.MessageId.ToString(
            System.Globalization.CultureInfo.InvariantCulture))
        {
            ChannelId = OptionalValue<string>.From(message.ChannelId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            GuildId = OptionalValue<string>.From(message.GuildId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            AuthorId = OptionalValue<string>.From((author?.UserId ?? 0).ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            AuthorUsername = OptionalValue<string>.From(author?.Username ?? string.Empty),
            AuthorDisplayName = OptionalValue<string?>.From(author?.DisplayName),
            AuthorGuildNickname = OptionalValue<string?>.From(author?.GuildNickname),
            AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
                !string.IsNullOrWhiteSpace(author?.GuildNickname)
                    ? DiscordDisplayNameSource.GuildNickname
                    : !string.IsNullOrWhiteSpace(author?.DisplayName)
                        ? DiscordDisplayNameSource.GlobalDisplayName
                        : DiscordDisplayNameSource.Username),
            AuthorGuildNicknameObservationSource =
                OptionalValue<DiscordDisplayNameSource>.From(
                    !string.IsNullOrWhiteSpace(author?.GuildNickname)
                        ? DiscordDisplayNameSource.GuildNickname
                        : DiscordDisplayNameSource.Unknown),
            Content = OptionalValue<string>.From(message.Content),
            CreatedAt = OptionalValue<DateTimeOffset?>.From(message.CreatedAt),
            EditedAt = OptionalValue<DateTimeOffset?>.From(message.EditedAt),
            CustomEmojis = OptionalValue<IReadOnlyList<DiscordCustomEmoji>>.From(
                message.CustomEmojis.Select(MapEmoji).ToArray()),
            Attachments = OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>>.From(
                message.Attachments.Select(MapAttachment).ToArray()),
            Embeds = OptionalValue<IReadOnlyList<DiscordEmbedMetadata>>.From(
                message.Embeds.Select(MapEmbed).ToArray()),
            Mentions = OptionalValue<IReadOnlyList<DiscordMention>>.From(
                message.Mentions.Select(MapMention).ToArray()),
            Stickers = OptionalValue<IReadOnlyList<DiscordStickerMetadata>>.From(
                message.Stickers.Select(MapSticker).ToArray()),
            Forward = OptionalValue<DiscordForwardMetadata?>.From(
                message.ForwardedSnapshots.Count == 0
                    ? null
                    : new DiscordForwardMetadata(
                        DiscordForwardResolutionMode.Snapshot,
                        null,
                        message.ForwardedSnapshots.Any(snapshot => snapshot.Stickers.Count > 0))),
            FallbackKind = OptionalValue<DiscordMessageFallbackKind>.From(
                DiscordMessageFallbackKind.None),
            RemoteMetadata = OptionalValue<DiscordRemoteMessageMetadata?>.From(
                MapRemoteMetadata(message)),
            AuthorStyle = OptionalValue<DiscordAuthorStyle?>.From(
                MapAuthorStyle(author?.RoleStyle)),
            Reactions = OptionalValue<IReadOnlyList<DiscordMessageReaction>>.From(
                message.Reactions.Select(MapReaction).ToArray()),
        };
    }

    private static DiscordRemoteMessageMetadata MapRemoteMetadata(ChatMessage message) => new(
        message.MessageType,
        message.RawMessageType,
        message.Flags,
        message.IsPinned,
        message.IsTts,
        message.MentionedEveryone,
        message.Author?.IsBot ?? false,
        message.Author?.IsWebhook ?? false,
        message.Reference is null || !IsUserVisibleReply(message.Reference)
            ? null
            : new DiscordReplyMetadata(
                message.Reference.Kind,
                message.Reference.GuildId?.ToString(),
                message.Reference.ChannelId?.ToString(),
                message.Reference.MessageId?.ToString())
            {
                ResolvedAuthorName = ResolveAuthorName(message.Reference.ResolvedMessage?.Author),
                ResolvedContent = message.Reference.ResolvedMessage?.Content,
            },
        message.ForwardedSnapshots.Select(snapshot => new DiscordForwardSnapshotMetadata(
            snapshot.MessageType,
            snapshot.Content,
            snapshot.CreatedAt,
            snapshot.EditedAt,
            snapshot.Attachments.Select(MapAttachment).ToArray(),
            snapshot.Embeds.Select(MapEmbed).ToArray(),
            snapshot.Mentions.Select(MapMention).ToArray(),
            snapshot.Stickers.Select(MapSticker).ToArray(),
            snapshot.Components.Select(MapComponent).ToArray())).ToArray(),
        message.Components.Select(MapComponent).ToArray(),
        message.Poll is null
            ? null
            : new DiscordPollMetadata(
                message.Poll.Question,
                message.Poll.Answers.Select(answer => new DiscordPollAnswerMetadata(
                    answer.AnswerId,
                    answer.Text,
                    answer.Emoji is null ? null : MapEmoji(answer.Emoji),
                    answer.VoteCount,
                    answer.SelfVoted)).ToArray(),
                message.Poll.ExpiresAt,
                message.Poll.AllowMultiselect,
                message.Poll.Layout,
                message.Poll.IsFinalized));

    private static string? ResolveAuthorName(ChatAuthor? author) =>
        author is null
            ? null
            : !string.IsNullOrWhiteSpace(author.GuildNickname)
                ? author.GuildNickname
                : !string.IsNullOrWhiteSpace(author.DisplayName)
                    ? author.DisplayName
                    : author.Username;

    private static bool IsUserVisibleReply(ChatMessageReference reference) =>
        !string.Equals(reference.Kind, "Forward", StringComparison.OrdinalIgnoreCase);

    private static DiscordAttachmentMetadata MapAttachment(ChatAttachment attachment) => new(
        attachment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        attachment.FileName,
        attachment.Url,
        attachment.ProxyUrl,
        attachment.Size,
        attachment.Width,
        attachment.Height,
        attachment.ContentType,
        attachment.Description,
        attachment.Title,
        attachment.IsEphemeral,
        attachment.DurationSeconds,
        attachment.WaveformBase64,
        attachment.IsVoiceMessage);

    private static DiscordEmbedMetadata MapEmbed(ChatEmbed embed) => new(
        embed.Type,
        embed.Url,
        embed.Title,
        embed.ImageUrl,
        embed.ThumbnailUrl,
        embed.Description,
        embed.Timestamp,
        embed.Color,
        embed.VideoUrl,
        embed.AuthorName,
        embed.AuthorUrl,
        embed.FooterText,
        embed.ProviderName,
        embed.Fields.Select(field => new DiscordEmbedFieldMetadata(
            field.Name,
            field.Value,
            field.IsInline)).ToArray());

    private static DiscordMention MapMention(ChatMention mention) => new(
        mention.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        mention.DisplayName)
    {
        Kind = mention.Kind,
    };

    private static DiscordStickerMetadata MapSticker(ChatSticker sticker) => new(
        sticker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sticker.Name,
        ResolveStickerFormatType(sticker.Format),
        sticker.AssetUrl)
    {
        RemoteFormat = sticker.Format,
    };

    private static int? ResolveStickerFormatType(string? format) => format?.Trim() switch
    {
        { } value when value.Equals("Png", StringComparison.OrdinalIgnoreCase) => 1,
        { } value when value.Equals("Apng", StringComparison.OrdinalIgnoreCase) => 2,
        { } value when value.Equals("Lottie", StringComparison.OrdinalIgnoreCase) => 3,
        { } value when value.Equals("Gif", StringComparison.OrdinalIgnoreCase) => 4,
        _ => null,
    };

    private static DiscordCustomEmoji MapEmoji(ChatEmoji emoji) => new(
        emoji.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        emoji.Name,
        emoji.IsAnimated);

    private static DiscordAuthorStyle? MapAuthorStyle(ChatAuthorStyle? style) =>
        style is null
            ? null
            : new DiscordAuthorStyle(
                style.ColorRoleId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                style.Color,
                style.IconRoleId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                style.Icon is null
                    ? null
                    : new DiscordRoleIcon(
                        style.Icon.Kind,
                        style.Icon.Value,
                        style.Icon.Url));

    private static DiscordMessageReaction MapReaction(ChatReaction reaction) => new(
        MapEmoji(reaction.Emoji),
        reaction.Count);

    private static DiscordComponentMetadata MapComponent(ChatComponent component) => new(
        component.Type,
        component.RawType,
        component.Id,
        component.CustomId,
        component.Label,
        component.Content,
        component.Description,
        component.Url,
        component.Value,
        component.IsDisabled,
        component.IsSpoiler,
        component.Children.Select(MapComponent).ToArray(),
        component.Options.Select(option => new DiscordComponentOptionMetadata(
            option.Label,
            option.Value,
            option.Description,
            option.Emoji is null ? null : MapEmoji(option.Emoji),
            option.IsDefault)).ToArray(),
        component.UnknownPayload)
    {
        Emoji = component.Emoji is null ? null : MapEmoji(component.Emoji),
        Attributes = component.Attributes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal),
    };
}
