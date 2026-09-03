using Discord;
using Discord.Rest;
using Discord.WebSocket;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Chat;

internal sealed partial class DiscordChatMessageNormalizer
{
    private const int MaximumReferenceDepth = 1;
    internal const int MaximumBootstrapConcurrency = 4;
    private readonly CanonicalRemoteAuthorResolver _authorResolver;

    public DiscordChatMessageNormalizer(CanonicalRemoteAuthorResolver authorResolver)
    {
        _authorResolver = authorResolver ?? throw new ArgumentNullException(nameof(authorResolver));
    }

    public Task<ChatMessage> NormalizeAsync(
        ulong guildId,
        IMessage message,
        CancellationToken cancellationToken = default) =>
        NormalizeAsync(guildId, message, 0, includeAuthor: true, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> NormalizeManyAsync(
        ulong guildId,
        IReadOnlyList<IMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return Array.Empty<ChatMessage>();
        }

        var indexed = messages.Select((message, index) =>
        {
            ArgumentNullException.ThrowIfNull(message);
            return new IndexedMessage(index, message);
        }).ToArray();
        var partitions = indexed
            .GroupBy(item => item.Message.Author?.Id ?? 0)
            .ToArray();
        var normalized = new ChatMessage[messages.Count];
        await Parallel.ForEachAsync(
                partitions,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaximumBootstrapConcurrency,
                },
                async (partition, token) =>
                {
                    // Preserve observation order for one author so the existing nickname
                    // cache and exact-current-value precedence remain deterministic.
                    foreach (var item in partition.OrderBy(item => item.Index))
                    {
                        normalized[item.Index] = await NormalizeAsync(
                                guildId,
                                item.Message,
                                token)
                            .ConfigureAwait(false);
                    }
                })
            .ConfigureAwait(false);
        return normalized;
    }

    private async Task<ChatMessage> NormalizeAsync(
        ulong guildId,
        IMessage message,
        int depth,
        bool includeAuthor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var flags = (ulong)(message.Flags ?? 0);
        var userMessage = message as IUserMessage;
        ChatMessage? referencedMessage = null;
        if (depth < MaximumReferenceDepth &&
            userMessage?.ReferencedMessage is { } resolved)
        {
            referencedMessage = await NormalizeAsync(
                    guildId,
                    resolved,
                    depth + 1,
                    includeAuthor: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var reference = message.Reference is null
            ? null
            : new ChatMessageReference(
                message.Reference.ReferenceType.IsSpecified
                    ? message.Reference.ReferenceType.Value.ToString()
                    : "Default",
                message.Reference.GuildId.IsSpecified
                    ? message.Reference.GuildId.Value
                    : null,
                message.Reference.ChannelId,
                message.Reference.MessageId.IsSpecified
                    ? message.Reference.MessageId.Value
                    : null,
                referencedMessage);
        ChatAuthor? author = null;
        if (includeAuthor && message.Author is { } observedAuthor)
        {
            author = await _authorResolver.ResolveAsync(
                    guildId,
                    RemoteAuthorObservation.From(observedAuthor),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new ChatMessage(
            message.Id,
            guildId,
            message.Channel.Id,
            message.Type.ToString(),
            (int)message.Type,
            author,
            message.Content ?? string.Empty,
            message.Timestamp,
            message.EditedTimestamp,
            message.IsPinned,
            message.IsTTS,
            message.MentionedEveryone,
            flags,
            ParseCustomEmojis(message.Content),
            message.Attachments.Select(attachment => MapAttachment(attachment, flags)).ToArray(),
            message.Embeds.Select(MapEmbed).ToArray(),
            MapMentions(message),
            message.Stickers.Select(MapSticker).ToArray(),
            userMessage?.ForwardedMessages.Select(snapshot =>
                MapForwardSnapshot(guildId, snapshot)).ToArray() ??
                Array.Empty<ChatForwardSnapshot>(),
            reference,
            message.Components.Select(MapComponent).ToArray(),
            userMessage?.Poll is { } poll ? MapPoll(poll) : null);
    }

    private sealed record IndexedMessage(int Index, IMessage Message);

    private ChatForwardSnapshot MapForwardSnapshot(
        ulong guildId,
        MessageSnapshot snapshot)
    {
        // Discord deliberately excludes authors from immutable forwarded snapshots.
        var message = snapshot.Message;
        var flags = message.Flags is { } value ? (ulong)value : 0;
        return new ChatForwardSnapshot(
            message.Type.ToString(),
            message.Content ?? string.Empty,
            message.Timestamp,
            message.EditedTimestamp,
            message.Attachments.Select(attachment => MapAttachment(attachment, flags)).ToArray(),
            message.Embeds.Select(MapEmbed).ToArray(),
            MapMentions(message),
            message.Stickers.Select(MapSticker).ToArray(),
            message.Components.Select(MapComponent).ToArray());
    }

    private static IReadOnlyList<ChatEmoji> ParseCustomEmojis(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<ChatEmoji>();
        }

        var result = new List<ChatEmoji>();
        for (var start = 0; start < content.Length; start++)
        {
            if (content[start] != '<')
            {
                continue;
            }

            var end = content.IndexOf('>', start + 1);
            if (end < 0)
            {
                break;
            }

            var candidate = content.AsSpan(start + 1, end - start - 1);
            var animated = candidate.StartsWith("a:", StringComparison.Ordinal);
            var prefixLength = animated
                ? 2
                : candidate.StartsWith(":", StringComparison.Ordinal) ? 1 : 0;
            if (prefixLength == 0)
            {
                continue;
            }

            var separator = candidate[prefixLength..].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            separator += prefixLength;
            var name = candidate[prefixLength..separator].ToString();
            if (!ulong.TryParse(candidate[(separator + 1)..], out var id))
            {
                continue;
            }

            result.Add(new ChatEmoji(
                id,
                name,
                animated,
                $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}"));
            start = end;
        }

        return result;
    }

    private static ChatEmoji? MapEmoji(IEmote? emote) => emote switch
    {
        null => null,
        Emote custom => new ChatEmoji(
            custom.Id,
            custom.Name,
            custom.Animated,
            custom.Url),
        _ => new ChatEmoji(null, emote.Name, false),
    };

    private static ChatAttachment MapAttachment(IAttachment attachment, ulong messageFlags)
    {
        const ulong voiceMessageFlag = 1UL << 13;
        return new ChatAttachment(
            TryExtractSnowflake(attachment.Url),
            attachment.Filename,
            attachment.Url,
            attachment.ProxyUrl,
            attachment.Size,
            attachment.ContentType,
            attachment.Width,
            attachment.Height,
            attachment.Description,
            attachment.Title,
            attachment.Ephemeral,
            attachment.Duration,
            attachment.Waveform,
            (messageFlags & voiceMessageFlag) != 0 ||
            attachment.Duration is not null ||
            attachment.Waveform is not null);
    }

    private static ulong TryExtractSnowflake(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return 0;
        }

        ulong result = 0;
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(segment, out var id))
            {
                result = id;
            }
        }

        return result;
    }

    private static ChatEmbed MapEmbed(IEmbed embed) => new(
        embed.Type.ToString(),
        embed.Url,
        embed.Title,
        embed.Description,
        embed.Timestamp,
        embed.Color?.RawValue,
        embed.Image?.Url,
        embed.Thumbnail?.Url,
        embed.Video?.Url,
        embed.Author?.Name,
        embed.Author?.Url,
        embed.Footer?.Text,
        embed.Provider?.Name,
        embed.Fields.Select(field => new ChatEmbedField(
            field.Name,
            field.Value,
            field.Inline)).ToArray());

    private static IReadOnlyList<ChatMention> MapMentions(IMessage message)
    {
        var users = new Dictionary<ulong, string?>();
        var roles = new Dictionary<ulong, string?>();
        var channels = new Dictionary<ulong, string?>();
        if (message is SocketMessage socket)
        {
            foreach (var user in socket.MentionedUsers)
            {
                users[user.Id] = user is SocketGuildUser guildUser
                    ? guildUser.DisplayName
                    : user.GlobalName ?? user.Username;
            }

            foreach (var role in socket.MentionedRoles)
            {
                roles[role.Id] = role.Name;
            }

            foreach (var channel in socket.MentionedChannels)
            {
                channels[channel.Id] = channel.Name;
            }
        }
        else if (message is RestMessage rest)
        {
            // Recent history and REST canonical updates carry names in MentionedUsers,
            // not in the socket cache or interaction-only ResolvedData. Retain that
            // already supplied data without adding member REST requests per mention.
            foreach (var user in rest.MentionedUsers)
            {
                users[user.Id] = string.IsNullOrWhiteSpace(user.GlobalName)
                    ? user.Username
                    : user.GlobalName;
            }
        }

        if (message is IUserMessage { ResolvedData: { } resolved })
        {
            foreach (var user in resolved.Users)
            {
                users[user.Id] = user.GlobalName ?? user.Username;
            }

            foreach (var member in resolved.Members)
            {
                users[member.Id] = member.DisplayName;
            }

            foreach (var role in resolved.Roles)
            {
                roles[role.Id] = role.Name;
            }

            foreach (var channel in resolved.Channels.OfType<IGuildChannel>())
            {
                channels[channel.Id] = channel.Name;
            }
        }

        var mentions = new List<ChatMention>();
        mentions.AddRange(message.MentionedUserIds.Select(id =>
            new ChatMention("user", id, users.GetValueOrDefault(id))));
        mentions.AddRange(message.MentionedRoleIds.Select(id =>
            new ChatMention("role", id, roles.GetValueOrDefault(id))));
        mentions.AddRange(message.MentionedChannelIds.Select(id =>
            new ChatMention("channel", id, channels.GetValueOrDefault(id))));
        return mentions;
    }

    private static ChatSticker MapSticker(IStickerItem sticker)
    {
        return new ChatSticker(
            sticker.Id,
            sticker.Name,
            sticker.Format.ToString(),
            ResolveStickerAssetUrl(sticker.Id, sticker.Format));
    }

    internal static string ResolveStickerAssetUrl(
        ulong stickerId,
        StickerFormatType format) => format switch
        {
            StickerFormatType.Gif =>
                $"https://media.discordapp.net/stickers/{stickerId}.gif?size=256&quality=lossless",
            StickerFormatType.Lottie =>
                $"https://cdn.discordapp.com/stickers/{stickerId}.json",
            _ =>
                $"https://media.discordapp.net/stickers/{stickerId}.png?size=256&quality=lossless",
        };

    private static ChatComponent MapComponent(IMessageComponent component)
    {
        var children = component switch
        {
            ActionRowComponent row => row.Components.Select(MapComponent).ToArray(),
            ContainerComponent container => container.Components.Select(MapComponent).ToArray(),
            SectionComponent section => section.Components
                .Append(section.Accessory)
                .Where(child => child is not null)
                .Select(MapComponent)
                .ToArray(),
            LabelComponent label => new[] { MapComponent(label.Component) },
            MediaGalleryComponent gallery => gallery.Items.Select(item => new ChatComponent(
                "MediaGalleryItem",
                0,
                null,
                null,
                null,
                null,
                item.Description,
                item.Media.Url,
                null,
                null,
                item.IsSpoiler,
                Array.Empty<ChatComponent>(),
                Array.Empty<ChatComponentOption>())).ToArray(),
            _ => Array.Empty<ChatComponent>(),
        };
        var options = component switch
        {
            SelectMenuComponent select => select.Options.Select(option => new ChatComponentOption(
                option.Label,
                option.Value,
                option.Description,
                MapEmoji(option.Emote),
                option.IsDefault)).ToArray(),
            CheckboxGroupComponent checkboxes => checkboxes.Options.Select(option =>
                new ChatComponentOption(
                    option.Label,
                    option.Value,
                    option.Description,
                    null,
                    option.DefaultState)).ToArray(),
            RadioGroupComponent radios => radios.Options.Select(option =>
                new ChatComponentOption(
                    option.Label,
                    option.Value,
                    option.Description,
                    null,
                    option.IsDefault)).ToArray(),
            _ => Array.Empty<ChatComponentOption>(),
        };
        return component switch
        {
            ButtonComponent button => Decorate(Base(
                component,
                button.CustomId,
                button.Label,
                null,
                null,
                button.Url,
                null,
                button.IsDisabled,
                null,
                children,
                options),
                MapEmoji(button.Emote),
                ("style", button.Style.ToString()),
                ("skuId", button.SkuId?.ToString())),
            SelectMenuComponent menu => Decorate(Base(
                component,
                menu.CustomId,
                null,
                null,
                menu.Placeholder,
                null,
                null,
                menu.IsDisabled,
                null,
                children,
                options),
                null,
                ("minValues", menu.MinValues.ToString()),
                ("maxValues", menu.MaxValues.ToString()),
                ("isRequired", menu.IsRequired.ToString()),
                ("channelTypes", string.Join(',', menu.ChannelTypes)),
                ("defaultValues", string.Join(',', menu.DefaultValues.Select(value =>
                    $"{value.Type}:{value.Id}")))),
            TextDisplayComponent text => Base(
                component, null, null, text.Content, null, null, null, null, null,
                children, options),
            ThumbnailComponent thumbnail => Base(
                component, null, null, null, thumbnail.Description, thumbnail.Media.Url, null,
                null, thumbnail.IsSpoiler, children, options),
            FileComponent file => Base(
                component, null, null, null, null, file.File.Url, null,
                null, file.IsSpoiler, children, options),
            ContainerComponent container => Decorate(Base(
                component, null, null, null, null, null, null, null,
                container.IsSpoiler, children, options),
                null,
                ("accentColor", container.AccentColor?.RawValue.ToString())),
            SeparatorComponent separator => Decorate(Base(
                component, null, null, null, null, null,
                null, null, null, children, options),
                null,
                ("isDivider", separator.IsDivider?.ToString()),
                ("spacing", separator.Spacing?.ToString())),
            LabelComponent label => Base(
                component, null, label.Label, null, label.Description, null, null,
                null, null, children, options),
            CheckboxComponent checkbox => Decorate(Base(
                component, checkbox.CustomId, null, null, null, null, null,
                null, null, children, options),
                null,
                ("defaultState", checkbox.DefaultState?.ToString())),
            CheckboxGroupComponent checkboxes => Decorate(Base(
                component, checkboxes.CustomId, null, null, null, null, null,
                null, null, children, options),
                null,
                ("minValues", checkboxes.MinValues?.ToString()),
                ("maxValues", checkboxes.MaxValues?.ToString()),
                ("isRequired", checkboxes.IsRequired?.ToString())),
            RadioGroupComponent radios => Decorate(Base(
                component, radios.CustomId, null, null, null, null, null,
                null, null, children, options),
                null,
                ("isRequired", radios.IsRequired?.ToString())),
            FileUploadComponent upload => Decorate(Base(
                component, upload.CustomId, null, null, null, null, null,
                null, null, children, options),
                null,
                ("minValues", upload.MinValues?.ToString()),
                ("maxValues", upload.MaxValues?.ToString()),
                ("isRequired", upload.IsRequired.ToString())),
            TextInputComponent input => Decorate(Base(
                component, input.CustomId, input.Label, null, input.Placeholder, null,
                input.Value, null, null, children, options),
                null,
                ("style", input.Style.ToString()),
                ("minLength", input.MinLength?.ToString()),
                ("maxLength", input.MaxLength?.ToString()),
                ("required", input.Required?.ToString())),
            UnknownComponent unknown => Base(
                component, null, null, null, null, null, null, null, null,
                children, options, unknown.RawJson),
            _ => Base(component, null, null, null, null, null, null, null, null,
                children, options),
        };
    }

    private static ChatComponent Decorate(
        ChatComponent component,
        ChatEmoji? emoji,
        params (string Key, string? Value)[] attributes) => component with
        {
            Emoji = emoji,
            Attributes = attributes.ToDictionary(
            attribute => attribute.Key,
            attribute => attribute.Value,
            StringComparer.Ordinal),
        };

    private static ChatComponent Base(
        IMessageComponent component,
        string? customId,
        string? label,
        string? content,
        string? description,
        string? url,
        string? value,
        bool? disabled,
        bool? spoiler,
        IReadOnlyList<ChatComponent> children,
        IReadOnlyList<ChatComponentOption> options,
        string? unknown = null) => new(
            component.Type.ToString(),
            component is UnknownComponent raw ? raw.RawType : (int)component.Type,
            component.Id,
            customId,
            label,
            content,
            description,
            url,
            value,
            disabled,
            spoiler,
            children,
            options,
            unknown);

    private static ChatPoll MapPoll(Poll poll)
    {
        var counts = poll.Results?.AnswerCounts.ToDictionary(
            count => count.AnswerId,
            count => count);
        return new ChatPoll(
            poll.Question.Text,
            poll.Answers.Select(answer =>
            {
                PollAnswerCounts? count = null;
                if (counts is not null &&
                    counts.TryGetValue(answer.AnswerId, out var resolvedCount))
                {
                    count = resolvedCount;
                }

                return new ChatPollAnswer(
                    answer.AnswerId,
                    answer.PollMedia.Text,
                    MapEmoji(answer.PollMedia.Emoji),
                    count?.Count,
                    count?.MeVoted);
            }).ToArray(),
            poll.ExpiresAt,
            poll.AllowMultiselect,
            poll.LayoutType.ToString(),
            poll.Results?.IsFinalized);
    }

}
