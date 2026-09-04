namespace GachaOverlay.Core.Discord.Messages;

public enum MessageStoreMutationResult
{
    Applied,
    Removed,
    Ignored,
}

public sealed class DiscordMessageStore
{
    private readonly Dictionary<string, NormalizedDiscordMessage> _messages;
    private readonly int? _retentionLimit;

    public DiscordMessageStore(
        int? retentionLimit = null,
        IEnumerable<NormalizedDiscordMessage>? seed = null)
    {
        if (retentionLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionLimit));
        }

        _retentionLimit = retentionLimit;
        _messages = seed?.ToDictionary(message => message.MessageId, StringComparer.Ordinal)
            ?? new Dictionary<string, NormalizedDiscordMessage>(StringComparer.Ordinal);
        TrimToRetentionLimit();
    }

    public int Count => _messages.Count;

    public MessageStoreMutationResult Apply(DiscordMessageMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        if (mutation.Kind == DiscordMessageMutationKind.Delete)
        {
            return _messages.Remove(mutation.MessageId)
                ? MessageStoreMutationResult.Removed
                : MessageStoreMutationResult.Ignored;
        }

        var patch = mutation.Patch
            ?? throw new InvalidOperationException("A create or update mutation requires a patch.");

        if (_messages.TryGetValue(mutation.MessageId, out var existing))
        {
            _messages[mutation.MessageId] = Merge(existing, patch);
            TrimToRetentionLimit();
            return MessageStoreMutationResult.Applied;
        }

        if (!TryCreate(patch, out var created))
        {
            return MessageStoreMutationResult.Ignored;
        }

        _messages.Add(created.MessageId, created);
        TrimToRetentionLimit();
        return MessageStoreMutationResult.Applied;
    }

    public bool TryGet(string messageId, out NormalizedDiscordMessage? message) =>
        _messages.TryGetValue(messageId, out message);

    public IReadOnlyList<NormalizedDiscordMessage> GetOrderedSnapshot() =>
        _messages.Values
            .OrderBy(message => message, DiscordMessageOrdering.Instance)
            .ToArray();

    public int RefreshGuildNickname(
        string guildId,
        string authorId,
        string nickname,
        DiscordDisplayNameSource observationSource = DiscordDisplayNameSource.GuildNickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);

        var refreshed = 0;
        foreach (var pair in _messages.ToArray())
        {
            var message = pair.Value;
            if (!string.Equals(message.GuildId, guildId, StringComparison.Ordinal) ||
                !string.Equals(message.AuthorId, authorId, StringComparison.Ordinal) ||
                (string.Equals(message.AuthorGuildNickname, nickname, StringComparison.Ordinal) &&
                    message.AuthorDisplayNameSource is DiscordDisplayNameSource.GuildNickname or
                        DiscordDisplayNameSource.CachedGuildNickname))
            {
                continue;
            }

            _messages[pair.Key] = message with
            {
                AuthorGuildNickname = nickname,
                AuthorDisplayNameSource = DiscordDisplayNameSource.CachedGuildNickname,
                AuthorGuildNicknameObservationSource = observationSource,
            };
            refreshed++;
        }

        return refreshed;
    }

    private static bool TryCreate(
        DiscordMessagePatch patch,
        out NormalizedDiscordMessage message)
    {
        message = null!;

        if (!patch.ChannelId.HasValue ||
            string.IsNullOrWhiteSpace(patch.ChannelId.Value) ||
            !patch.AuthorId.HasValue ||
            string.IsNullOrWhiteSpace(patch.AuthorId.Value))
        {
            return false;
        }

        message = new NormalizedDiscordMessage(
            patch.MessageId,
            patch.ChannelId.Value,
            patch.AuthorId.Value,
            patch.AuthorUsername.HasValue ? patch.AuthorUsername.Value : string.Empty,
            patch.AuthorDisplayName.HasValue ? patch.AuthorDisplayName.Value : null,
            patch.Content.HasValue ? patch.Content.Value : string.Empty,
            patch.CreatedAt.HasValue ? patch.CreatedAt.Value : null,
            patch.EditedAt.HasValue ? patch.EditedAt.Value : null,
            Copy(patch.CustomEmojis, Array.Empty<DiscordCustomEmoji>()),
            Copy(patch.Attachments, Array.Empty<DiscordAttachmentMetadata>()),
            Copy(patch.Embeds, Array.Empty<DiscordEmbedMetadata>()),
            Copy(patch.Mentions, Array.Empty<DiscordMention>()))
        {
            GuildId = patch.GuildId.HasValue ? patch.GuildId.Value : string.Empty,
            AuthorGuildNickname = patch.AuthorGuildNickname.HasValue
                ? patch.AuthorGuildNickname.Value
                : null,
            AuthorDisplayNameSource = patch.AuthorDisplayNameSource.HasValue
                ? patch.AuthorDisplayNameSource.Value
                : DiscordDisplayNameSource.Unknown,
            AuthorGuildNicknameObservationSource =
                patch.AuthorGuildNicknameObservationSource.HasValue
                    ? patch.AuthorGuildNicknameObservationSource.Value
                    : DiscordDisplayNameSource.Unknown,
            Stickers = Copy(patch.Stickers, Array.Empty<DiscordStickerMetadata>()),
            Forward = patch.Forward.HasValue ? patch.Forward.Value : null,
            FallbackKind = patch.FallbackKind.HasValue
                ? patch.FallbackKind.Value
                : DiscordMessageFallbackKind.None,
            RemoteMetadata = patch.RemoteMetadata.HasValue
                ? patch.RemoteMetadata.Value
                : null,
            AuthorStyle = patch.AuthorStyle.HasValue ? patch.AuthorStyle.Value : null,
            Reactions = Copy(patch.Reactions, Array.Empty<DiscordMessageReaction>()),
        };
        return true;
    }

    private static NormalizedDiscordMessage Merge(
        NormalizedDiscordMessage existing,
        DiscordMessagePatch patch) =>
        existing with
        {
            ChannelId = patch.ChannelId.HasValue ? patch.ChannelId.Value : existing.ChannelId,
            GuildId = patch.GuildId.HasValue ? patch.GuildId.Value : existing.GuildId,
            AuthorId = patch.AuthorId.HasValue ? patch.AuthorId.Value : existing.AuthorId,
            AuthorUsername = patch.AuthorUsername.HasValue
                ? patch.AuthorUsername.Value
                : existing.AuthorUsername,
            AuthorDisplayName = patch.AuthorDisplayName.HasValue
                ? patch.AuthorDisplayName.Value
                : existing.AuthorDisplayName,
            AuthorGuildNickname = patch.AuthorGuildNickname.HasValue
                ? patch.AuthorGuildNickname.Value
                : existing.AuthorGuildNickname,
            AuthorDisplayNameSource = patch.AuthorDisplayNameSource.HasValue
                ? patch.AuthorDisplayNameSource.Value
                : existing.AuthorDisplayNameSource,
            AuthorGuildNicknameObservationSource =
                patch.AuthorGuildNicknameObservationSource.HasValue
                    ? patch.AuthorGuildNicknameObservationSource.Value
                    : existing.AuthorGuildNicknameObservationSource,
            Content = patch.Content.HasValue ? patch.Content.Value : existing.Content,
            // Discord message creation time is part of the stable message identity.
            // MESSAGE_UPDATE may repeat timestamp fields, but it must never move an
            // existing item in the ordered/retained chat history.
            CreatedAt = existing.CreatedAt,
            EditedAt = patch.EditedAt.HasValue ? patch.EditedAt.Value : existing.EditedAt,
            CustomEmojis = patch.CustomEmojis.HasValue
                ? patch.CustomEmojis.Value.ToArray()
                : existing.CustomEmojis,
            Attachments = patch.Attachments.HasValue
                ? patch.Attachments.Value.ToArray()
                : existing.Attachments,
            Embeds = patch.Embeds.HasValue
                ? patch.Embeds.Value.ToArray()
                : existing.Embeds,
            Mentions = patch.Mentions.HasValue
                ? patch.Mentions.Value.ToArray()
                : existing.Mentions,
            Stickers = patch.Stickers.HasValue
                ? patch.Stickers.Value.ToArray()
                : existing.Stickers,
            Forward = patch.Forward.HasValue ? patch.Forward.Value : existing.Forward,
            FallbackKind = patch.FallbackKind.HasValue
                ? patch.FallbackKind.Value
                : existing.FallbackKind,
            RemoteMetadata = patch.RemoteMetadata.HasValue
                ? patch.RemoteMetadata.Value
                : existing.RemoteMetadata,
            AuthorStyle = patch.AuthorStyle.HasValue
                ? patch.AuthorStyle.Value
                : existing.AuthorStyle,
            Reactions = patch.Reactions.HasValue
                ? patch.Reactions.Value.ToArray()
                : existing.Reactions,
        };

    private static IReadOnlyList<T> Copy<T>(
        OptionalValue<IReadOnlyList<T>> optional,
        IReadOnlyList<T> fallback) =>
        optional.HasValue ? optional.Value.ToArray() : fallback;

    private void TrimToRetentionLimit()
    {
        if (_retentionLimit is null || _messages.Count <= _retentionLimit.Value)
        {
            return;
        }

        var removeCount = _messages.Count - _retentionLimit.Value;
        var oldestIds = _messages.Values
            .OrderBy(message => message, DiscordMessageOrdering.Instance)
            .Take(removeCount)
            .Select(message => message.MessageId)
            .ToArray();

        foreach (var messageId in oldestIds)
        {
            _messages.Remove(messageId);
        }
    }

    private sealed class DiscordMessageOrdering : IComparer<NormalizedDiscordMessage>
    {
        public static DiscordMessageOrdering Instance { get; } = new();

        public int Compare(NormalizedDiscordMessage? left, NormalizedDiscordMessage? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            if (left.CreatedAt.HasValue != right.CreatedAt.HasValue)
            {
                return left.CreatedAt.HasValue ? 1 : -1;
            }

            if (left.CreatedAt.HasValue)
            {
                var timestampComparison = left.CreatedAt.Value.CompareTo(right.CreatedAt!.Value);
                if (timestampComparison != 0)
                {
                    return timestampComparison;
                }
            }

            if (ulong.TryParse(left.MessageId, out var leftSnowflake) &&
                ulong.TryParse(right.MessageId, out var rightSnowflake))
            {
                return leftSnowflake.CompareTo(rightSnowflake);
            }

            return string.Compare(left.MessageId, right.MessageId, StringComparison.Ordinal);
        }
    }
}
