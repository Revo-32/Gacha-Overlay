using System.Text.RegularExpressions;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Chat;

public enum ChatTokenKind
{
    Text,
    Mention,
    CustomEmoji,
}

public sealed record ChatToken(
    ChatTokenKind Kind,
    string Text,
    string? Identity = null,
    bool IsSelfMention = false,
    bool IsAnimatedEmoji = false);

public sealed record ChatMediaCandidate(
    string Url,
    string? ContentType,
    int? Width,
    int? Height,
    string? SourceUrl = null);

public sealed record ChatStickerPresentation(
    string StickerId,
    string Name,
    int? FormatType,
    string? AssetUrl);

public sealed record ChatMessagePresentation(
    string MessageId,
    string AuthorName,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<ChatToken> Tokens,
    string PlainText,
    IReadOnlyList<ChatMediaCandidate> Media,
    IReadOnlyList<ChatStickerPresentation> Stickers,
    int AdditionalMediaCount,
    bool HasSelfMention,
    long Generation,
    int Revision)
{
    public DiscordDisplayNameSource AuthorNameSource { get; init; } =
        DiscordDisplayNameSource.Unknown;

    public DiscordMessageFallbackKind FallbackKind { get; init; }
}

public enum ChatPresentationChangeKind
{
    SnapshotAdd,
    Add,
    Update,
    Remove,
}

public sealed record ChatPresentationChange(
    ChatPresentationChangeKind Kind,
    string MessageId,
    int Index,
    ChatMessagePresentation? Message,
    bool RequestMentionPulse);

public sealed partial class ChatPresentationSynchronizer
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _generation = -1;

    public IReadOnlyList<ChatPresentationChange> Synchronize(
        DiscordMessageState state,
        string? authenticatedUserId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = _generation != state.Generation;
        if (snapshot)
        {
            _generation = state.Generation;
        }

        var changes = new List<ChatPresentationChange>();
        var liveIds = state.MainChat.Select(message => message.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var removedId in _entries.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _entries.Remove(removedId);
            changes.Add(new ChatPresentationChange(
                ChatPresentationChangeKind.Remove,
                removedId,
                -1,
                null,
                false));
        }

        for (var index = 0; index < state.MainChat.Count; index++)
        {
            var source = state.MainChat[index];
            var fingerprint = CreateFingerprint(source, authenticatedUserId);
            if (!_entries.TryGetValue(source.MessageId, out var current))
            {
                var presentation = Project(source, authenticatedUserId, state.Generation, 1);
                _entries[source.MessageId] = new Entry(fingerprint, presentation);
                changes.Add(new ChatPresentationChange(
                    snapshot
                        ? ChatPresentationChangeKind.SnapshotAdd
                        : ChatPresentationChangeKind.Add,
                    source.MessageId,
                    index,
                    presentation,
                    !snapshot && presentation.HasSelfMention));
                continue;
            }

            if (!snapshot &&
                string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            var updated = Project(
                source,
                authenticatedUserId,
                state.Generation,
                current.Presentation.Revision + 1);
            _entries[source.MessageId] = new Entry(fingerprint, updated);
            changes.Add(new ChatPresentationChange(
                ChatPresentationChangeKind.Update,
                source.MessageId,
                index,
                updated,
                !snapshot && !current.Presentation.HasSelfMention && updated.HasSelfMention));
        }

        return changes;
    }

    private static ChatMessagePresentation Project(
        NormalizedDiscordMessage message,
        string? authenticatedUserId,
        long generation,
        int revision)
    {
        var mentions = message.Mentions
            .GroupBy(mention => mention.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var emojis = message.CustomEmojis
            .GroupBy(emoji => emoji.EmojiId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var tokens = Tokenize(message.Content, mentions, emojis, authenticatedUserId);
        var media = CreateMediaCandidates(message);
        var stickers = message.Stickers
            .Select(sticker => new ChatStickerPresentation(
                sticker.StickerId,
                sticker.Name,
                sticker.FormatType,
                sticker.AssetUrl))
            .ToArray();
        var author = ResolveAuthor(message);
        return new ChatMessagePresentation(
            message.MessageId,
            author.Name,
            message.CreatedAt,
            tokens,
            CreatePlainText(tokens),
            media,
            stickers,
            Math.Max(0, media.Count - 1),
            tokens.Any(token => token.IsSelfMention),
            generation,
            revision)
        {
            AuthorNameSource = author.Source,
            FallbackKind = message.FallbackKind,
        };
    }

    public static string ResolveAuthorName(NormalizedDiscordMessage message)
        => ResolveAuthor(message).Name;

    public static ResolvedDiscordDisplayName ResolveAuthor(NormalizedDiscordMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.IsNullOrWhiteSpace(message.AuthorGuildNickname))
        {
            var source = message.AuthorDisplayNameSource is
                DiscordDisplayNameSource.GuildNickname or
                DiscordDisplayNameSource.CachedGuildNickname
                    ? message.AuthorDisplayNameSource
                    : DiscordDisplayNameSource.GuildNickname;
            return new ResolvedDiscordDisplayName(message.AuthorGuildNickname!, source);
        }

        if (!string.IsNullOrWhiteSpace(message.AuthorDisplayName))
        {
            return new ResolvedDiscordDisplayName(
                message.AuthorDisplayName!,
                DiscordDisplayNameSource.GlobalDisplayName);
        }

        if (!string.IsNullOrWhiteSpace(message.AuthorUsername))
        {
            return new ResolvedDiscordDisplayName(
                message.AuthorUsername,
                DiscordDisplayNameSource.Username);
        }

        return new ResolvedDiscordDisplayName("Unknown", DiscordDisplayNameSource.Unknown);
    }

    private static IReadOnlyList<ChatToken> Tokenize(
        string content,
        IReadOnlyDictionary<string, DiscordMention> mentions,
        IReadOnlyDictionary<string, DiscordCustomEmoji> emojis,
        string? selfId)
    {
        var tokens = new List<ChatToken>();
        var cursor = 0;
        var normalizedContent = content ?? string.Empty;
        foreach (Match match in DiscordMarkupPattern().Matches(normalizedContent))
        {
            if (match.Index > cursor)
            {
                tokens.Add(new ChatToken(
                    ChatTokenKind.Text,
                    normalizedContent[cursor..match.Index]));
            }

            if (match.Groups[1].Success)
            {
                var userId = match.Groups[1].Value;
                if (mentions.TryGetValue(userId, out var mention))
                {
                    var self = !string.IsNullOrWhiteSpace(selfId) &&
                        string.Equals(userId, selfId, StringComparison.Ordinal);
                    tokens.Add(new ChatToken(
                        ChatTokenKind.Mention,
                        $"@{mention.DisplayName ?? userId}",
                        userId,
                        self));
                }
                else
                {
                    tokens.Add(new ChatToken(ChatTokenKind.Text, match.Value));
                }
            }
            else
            {
                var emojiId = match.Groups[4].Value;
                var name = match.Groups[3].Value;
                var animated = match.Groups[2].Value.Length > 0;
                if (emojis.TryGetValue(emojiId, out var emoji))
                {
                    name = emoji.Name;
                    animated = emoji.Animated;
                }

                tokens.Add(new ChatToken(
                    ChatTokenKind.CustomEmoji,
                    $":{name}:",
                    emojiId,
                    false,
                    animated));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < normalizedContent.Length)
        {
            tokens.Add(new ChatToken(ChatTokenKind.Text, normalizedContent[cursor..]));
        }

        return tokens.Count == 0
            ? new[] { new ChatToken(ChatTokenKind.Text, string.Empty) }
            : tokens;
    }

    private static IReadOnlyList<ChatMediaCandidate> CreateMediaCandidates(
        NormalizedDiscordMessage message)
    {
        var result = new List<ChatMediaCandidate>();
        foreach (var attachment in message.Attachments)
        {
            var url = attachment.ProxyUrl ?? attachment.Url;
            if (url is not null && IsHttps(url) &&
                (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
                 HasImageExtension(url)))
            {
                result.Add(new ChatMediaCandidate(
                    url!,
                    attachment.ContentType,
                    attachment.Width,
                    attachment.Height,
                    attachment.Url));
            }
        }

        foreach (var embed in message.Embeds)
        {
            var url = embed.ImageUrl ?? embed.ThumbnailUrl;
            if (IsHttps(url))
            {
                result.Add(new ChatMediaCandidate(url!, "image/*", null, null, embed.Url));
            }
        }

        return result
            .GroupBy(item => item.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static string CreatePlainText(IEnumerable<ChatToken> tokens) =>
        string.Concat(tokens.Select(token => token.Text));

    private static string CreateFingerprint(
        NormalizedDiscordMessage message,
        string? selfId) => string.Join(
        '\u001f',
        message.MessageId,
        message.AuthorId,
        message.AuthorUsername,
        message.AuthorDisplayName,
        message.AuthorGuildNickname,
        message.AuthorDisplayNameSource,
        message.GuildId,
        message.Content,
        message.FallbackKind,
        message.Forward?.Resolution,
        message.Forward?.SourceKey,
        message.CreatedAt?.ToString("O"),
        message.EditedAt?.ToString("O"),
        selfId,
        string.Join('|', message.CustomEmojis.Select(x => $"{x.EmojiId}:{x.Name}:{x.Animated}")),
        string.Join('|', message.Mentions.Select(x => $"{x.UserId}:{x.DisplayName}")),
        string.Join('|', message.Attachments.Select(x => $"{x.AttachmentId}:{x.Url}:{x.ProxyUrl}:{x.ContentType}")),
        string.Join('|', message.Embeds.Select(x => $"{x.ImageUrl}:{x.ThumbnailUrl}")),
        string.Join('|', message.Stickers.Select(x => $"{x.StickerId}:{x.Name}:{x.FormatType}:{x.AssetUrl}")));

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static bool HasImageExtension(string value)
    {
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : value;
        return new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("<@!?(\\d+)>|<(a?):([A-Za-z0-9_]+):(\\d+)>", RegexOptions.CultureInvariant)]
    private static partial Regex DiscordMarkupPattern();

    private sealed record Entry(string Fingerprint, ChatMessagePresentation Presentation);
}

public sealed record ResolvedDiscordDisplayName(
    string Name,
    DiscordDisplayNameSource Source);
