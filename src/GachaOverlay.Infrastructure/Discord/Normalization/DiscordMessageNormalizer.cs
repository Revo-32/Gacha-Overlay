using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Normalization;

public sealed partial class DiscordMessageNormalizer : IDiscordMessageNormalizer
{
    private const int MaxNameSourceDiagnostics = 128;
    private readonly IAppLogger _logger;
    private readonly object _nameSourceDiagnosticSync = new();
    private readonly HashSet<string> _nameSourceDiagnosticKeys = new(StringComparer.Ordinal);

    public DiscordMessageNormalizer(IAppLogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<DiscordMessagePatch> NormalizeSnapshot(
        JsonElement getChannelResponse,
        string channelId,
        string? guildIdHint = null)
    {
        if (!getChannelResponse.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GET_CHANNEL returned no messages array.");
        }

        var guildId = DiscordJson.GetString(data, "guild_id") ?? guildIdHint;
        var result = new List<DiscordMessagePatch>();
        var skipped = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (TryNormalizeMessage(message, channelId, guildId, "Snapshot", out var patch))
            {
                result.Add(patch);
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            _logger.Warning("RPC", $"Snapshot normalization skipped {skipped} malformed message(s).");
        }

        return result;
    }

    public bool TryNormalizeDispatch(
        JsonElement dispatch,
        out DiscordMessageMutation? mutation,
        out string eventName,
        string? guildIdHint = null)
    {
        mutation = null;
        eventName = DiscordJson.GetString(dispatch, "evt") ?? string.Empty;
        if (!dispatch.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var channelId = DiscordJson.GetString(data, "channel_id");
        var guildId = DiscordJson.GetString(data, "guild_id") ?? guildIdHint;
        if (string.Equals(eventName, "MESSAGE_DELETE", StringComparison.Ordinal))
        {
            var hasDeleteMessage = data.TryGetProperty("message", out var deleteMessage);
            var messageId = hasDeleteMessage
                ? DiscordJson.GetString(deleteMessage, "id")
                    ?? DiscordJson.GetString(data, "message_id")
                : DiscordJson.GetString(data, "message_id");
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            channelId ??= hasDeleteMessage
                ? DiscordJson.GetString(deleteMessage, "channel_id")
                : null;
            mutation = DiscordMessageMutation.Delete(messageId, channelId);
            return true;
        }

        var kind = eventName switch
        {
            "MESSAGE_CREATE" => DiscordMessageMutationKind.Create,
            "MESSAGE_UPDATE" => DiscordMessageMutationKind.Update,
            _ => (DiscordMessageMutationKind?)null,
        };
        if (kind is null ||
            !data.TryGetProperty("message", out var message) ||
            !TryNormalizeMessage(
                message,
                channelId,
                guildId,
                kind == DiscordMessageMutationKind.Create ? "LiveCreate" : "LiveUpdate",
                out var patch))
        {
            return false;
        }

        mutation = kind == DiscordMessageMutationKind.Create
            ? DiscordMessageMutation.Create(patch)
            : DiscordMessageMutation.Update(patch);
        return true;
    }

    public bool TryNormalizeForwardSource(
        JsonElement getChannelResponse,
        DiscordForwardSourceKey sourceKey,
        out DiscordForwardContent? content)
    {
        content = null;
        if (!getChannelResponse.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!string.Equals(
                    DiscordJson.GetString(message, "id"),
                    sourceKey.MessageId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            content = ReadForwardContent(message);
            return content.IsSufficient;
        }

        return false;
    }

    private bool TryNormalizeMessage(
        JsonElement message,
        string? channelIdHint,
        string? guildIdHint,
        string observationKind,
        out DiscordMessagePatch patch)
    {
        patch = null!;
        if (message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var messageId = DiscordJson.GetString(message, "id");
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        var channelId = DiscordJson.GetString(message, "channel_id") ?? channelIdHint;
        var channel = string.IsNullOrWhiteSpace(channelId)
            ? default
            : OptionalValue<string>.From(channelId);
        var guildId = DiscordJson.GetString(message, "guild_id") ?? guildIdHint;
        var guild = string.IsNullOrWhiteSpace(guildId)
            ? default
            : OptionalValue<string>.From(guildId);

        var authorId = default(OptionalValue<string>);
        var authorUsername = default(OptionalValue<string>);
        var authorDisplayName = default(OptionalValue<string?>);
        var authorGuildNickname = ReadGuildNickname(message, out var guildNicknamePath);
        var authorSource = default(OptionalValue<DiscordDisplayNameSource>);
        if (message.TryGetProperty("author", out var author) &&
            author.ValueKind == JsonValueKind.Object)
        {
            var id = DiscordJson.GetString(author, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                authorId = OptionalValue<string>.From(id);
            }

            if (author.TryGetProperty("username", out var usernameElement))
            {
                authorUsername = OptionalValue<string>.From(
                    DiscordJson.GetString(usernameElement) ?? string.Empty);
            }

            if (author.TryGetProperty("global_name", out var displayElement))
            {
                authorDisplayName = OptionalValue<string?>.From(
                    displayElement.ValueKind == JsonValueKind.Null
                        ? null
                        : DiscordJson.GetString(displayElement));
            }

            authorSource = OptionalValue<DiscordDisplayNameSource>.From(
                authorGuildNickname.HasValue &&
                !string.IsNullOrWhiteSpace(authorGuildNickname.Value)
                    ? DiscordDisplayNameSource.GuildNickname
                    : authorDisplayName.HasValue &&
                        !string.IsNullOrWhiteSpace(authorDisplayName.Value)
                        ? DiscordDisplayNameSource.GlobalDisplayName
                        : authorUsername.HasValue &&
                            !string.IsNullOrWhiteSpace(authorUsername.Value)
                            ? DiscordDisplayNameSource.Username
                            : DiscordDisplayNameSource.Unknown);
        }

        var forwardEnvelope = ReadForwardEnvelope(message, guildId);
        var contentSource = forwardEnvelope.Snapshot ?? message;
        var content = ReadContent(contentSource);
        if ((!content.HasValue || string.IsNullOrWhiteSpace(content.Value)) &&
            forwardEnvelope.Snapshot is not null)
        {
            content = PreferNonEmpty(content, ReadContent(message));
        }

        var createdAt = ReadTimestamp(message, "timestamp");
        var editedAt = ReadTimestamp(message, "edited_timestamp");
        var emojis = ReadCustomEmojis(contentSource, content);
        var attachments = ReadAttachments(contentSource);
        var embeds = ReadEmbeds(contentSource);
        var mentions = ReadMentions(contentSource);
        if (forwardEnvelope.Snapshot is not null)
        {
            emojis = PreferNonEmpty(emojis, ReadCustomEmojis(message, ReadContent(message)));
            attachments = PreferNonEmpty(attachments, ReadAttachments(message));
            embeds = PreferNonEmpty(embeds, ReadEmbeds(message));
            mentions = PreferNonEmpty(mentions, ReadMentions(message));
        }

        LogForwardPayloadStructure(message, observationKind, messageId);
        var stickers = ReadStickers(contentSource, out var stickerPayloadField);
        var hasStickerEvidence = HasStickerEvidence(contentSource);
        if (forwardEnvelope.Snapshot is not null)
        {
            var wrapperStickers = ReadStickers(message, out var wrapperStickerField);
            stickers = PreferNonEmpty(stickers, wrapperStickers);
            hasStickerEvidence |= HasStickerEvidence(message);
            if (string.IsNullOrWhiteSpace(stickerPayloadField))
            {
                stickerPayloadField = wrapperStickerField;
            }
        }

        if (hasStickerEvidence && (!stickers.HasValue || stickers.Value.Count == 0))
        {
            stickers = OptionalValue<IReadOnlyList<DiscordStickerMetadata>>.From(new[]
            {
                new DiscordStickerMetadata(string.Empty, string.Empty, null, null),
            });
            stickerPayloadField ??= "positive-evidence";
        }

        var isOpaqueEmptyCandidate = IsOpaqueEmptyMessage(
            message,
            content,
            attachments,
            embeds,
            hasStickerEvidence);
        DiscordForwardMetadata? forward = null;
        var fallbackKind = DiscordMessageFallbackKind.None;
        var hasUsablePayload = HasUsablePayload(
            content,
            attachments,
            embeds,
            stickers,
            hasStickerEvidence);
        if (forwardEnvelope.IsForward)
        {
            var resolution = hasUsablePayload
                ? forwardEnvelope.Snapshot is not null
                    ? DiscordForwardResolutionMode.Snapshot
                    : DiscordForwardResolutionMode.FlattenedPayload
                : forwardEnvelope.SourceKey is not null
                    ? DiscordForwardResolutionMode.LookupPending
                    : DiscordForwardResolutionMode.Fallback;
            forward = new DiscordForwardMetadata(
                resolution,
                forwardEnvelope.SourceKey,
                hasStickerEvidence);
            if (!hasUsablePayload)
            {
                fallbackKind = DiscordMessageFallbackKind.ForwardedMessage;
            }
        }
        else if (isOpaqueEmptyCandidate)
        {
            var isLivePayload = observationKind.StartsWith("Live", StringComparison.Ordinal);
            fallbackKind = isLivePayload
                ? DiscordMessageFallbackKind.PendingHydration
                : DiscordMessageFallbackKind.Message;
            _logger.Information(
                "OPAQUE",
                $"message={SanitizeMetadata(messageId)} " +
                $"resolution={(isLivePayload ? "PendingHydration" : "NeutralFallback")} " +
                $"stickerMetadata=false presentation={(isLivePayload ? "Deferred" : "MessageFallback")}.");
        }

        LogForwardDecision(
            messageId,
            forward,
            content,
            attachments,
            embeds,
            stickers,
            fallbackKind);

        LogAuthorMetadata(
            message,
            observationKind,
            messageId,
            authorId,
            authorUsername,
            authorDisplayName,
            authorGuildNickname,
            guild,
            guildNicknamePath,
            authorSource);
        LogStickerMetadata(messageId, stickerPayloadField, stickers);

        patch = new DiscordMessagePatch(messageId)
        {
            ChannelId = channel,
            GuildId = guild,
            AuthorId = authorId,
            AuthorUsername = authorUsername,
            AuthorDisplayName = authorDisplayName,
            AuthorGuildNickname = authorGuildNickname,
            AuthorDisplayNameSource = authorSource,
            AuthorGuildNicknameObservationSource = authorGuildNickname.HasValue &&
                !string.IsNullOrWhiteSpace(authorGuildNickname.Value)
                    ? OptionalValue<DiscordDisplayNameSource>.From(
                        DiscordDisplayNameSource.RpcGuildNickname)
                    : default,
            Content = content,
            CreatedAt = createdAt,
            EditedAt = editedAt,
            CustomEmojis = emojis,
            Attachments = attachments,
            Embeds = embeds,
            Mentions = mentions,
            Stickers = stickers,
            Forward = OptionalValue<DiscordForwardMetadata?>.From(forward),
            FallbackKind = OptionalValue<DiscordMessageFallbackKind>.From(fallbackKind),
        };
        return true;
    }

    private static ForwardEnvelope ReadForwardEnvelope(
        JsonElement message,
        string? guildIdHint)
    {
        var hasReference = TryGetProperty(
                message,
                "message_reference",
                "messageReference",
                out var reference) &&
            reference.ValueKind == JsonValueKind.Object;
        var referenceType = hasReference
            ? GetInt32(reference, "type")
            : null;
        var referenceTypeName = hasReference
            ? GetString(reference, "type")
            : null;
        var explicitReference = referenceType == 1 ||
            string.Equals(referenceTypeName, "FORWARD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(referenceTypeName, "Forward", StringComparison.OrdinalIgnoreCase);

        JsonElement? snapshot = null;
        if (TryGetProperty(
                message,
                "message_snapshots",
                "messageSnapshots",
                out var snapshots) &&
            snapshots.ValueKind == JsonValueKind.Array &&
            snapshots.GetArrayLength() > 0)
        {
            var envelope = snapshots.EnumerateArray().First();
            snapshot = envelope.ValueKind == JsonValueKind.Object &&
                TryGetProperty(envelope, "message", "snapshotMessage", out var nested) &&
                nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : envelope;
        }

        var isForward = explicitReference || snapshot is not null;
        DiscordForwardSourceKey? sourceKey = null;
        if (isForward && hasReference)
        {
            var sourceChannelId = GetString(reference, "channel_id", "channelId");
            var sourceMessageId = GetString(reference, "message_id", "messageId");
            var sourceGuildId = GetString(reference, "guild_id", "guildId") ?? guildIdHint;
            if (!string.IsNullOrWhiteSpace(sourceGuildId) &&
                !string.IsNullOrWhiteSpace(sourceChannelId) &&
                !string.IsNullOrWhiteSpace(sourceMessageId))
            {
                sourceKey = new DiscordForwardSourceKey(
                    sourceGuildId,
                    sourceChannelId,
                    sourceMessageId);
            }
        }

        return new ForwardEnvelope(isForward, snapshot, sourceKey);
    }

    private static DiscordForwardContent ReadForwardContent(JsonElement message)
    {
        var content = ReadContent(message);
        var customEmojis = ReadCustomEmojis(message, content);
        var attachments = ReadAttachments(message);
        var embeds = ReadEmbeds(message);
        var mentions = ReadMentions(message);
        var stickers = ReadStickers(message, out _);
        var stickerEvidence = HasStickerEvidence(message);
        var normalizedStickers = stickers.HasValue
            ? stickers.Value.ToArray()
            : Array.Empty<DiscordStickerMetadata>();
        if (stickerEvidence && normalizedStickers.Length == 0)
        {
            normalizedStickers = new[]
            {
                new DiscordStickerMetadata(string.Empty, string.Empty, null, null),
            };
        }

        return new DiscordForwardContent(
            content.HasValue ? content.Value : string.Empty,
            customEmojis.HasValue
                ? customEmojis.Value.ToArray()
                : Array.Empty<DiscordCustomEmoji>(),
            attachments.HasValue
                ? attachments.Value.ToArray()
                : Array.Empty<DiscordAttachmentMetadata>(),
            embeds.HasValue
                ? embeds.Value.ToArray()
                : Array.Empty<DiscordEmbedMetadata>(),
            mentions.HasValue
                ? mentions.Value.ToArray()
                : Array.Empty<DiscordMention>(),
            normalizedStickers,
            stickerEvidence);
    }

    private static OptionalValue<string> ReadContent(JsonElement message) =>
        message.TryGetProperty("content", out var contentElement)
            ? OptionalValue<string>.From(DiscordJson.GetString(contentElement) ?? string.Empty)
            : default;

    private static OptionalValue<IReadOnlyList<T>> PreferNonEmpty<T>(
        OptionalValue<IReadOnlyList<T>> primary,
        OptionalValue<IReadOnlyList<T>> secondary) =>
        primary.HasValue && primary.Value.Count > 0
            ? primary
            : secondary.HasValue
                ? secondary
                : primary;

    private static OptionalValue<string> PreferNonEmpty(
        OptionalValue<string> primary,
        OptionalValue<string> secondary) =>
        primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value)
            ? primary
            : secondary.HasValue
                ? secondary
                : primary;

    private static bool HasUsablePayload(
        OptionalValue<string> content,
        OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>> attachments,
        OptionalValue<IReadOnlyList<DiscordEmbedMetadata>> embeds,
        OptionalValue<IReadOnlyList<DiscordStickerMetadata>> stickers,
        bool hasStickerEvidence) =>
        content.HasValue && !string.IsNullOrWhiteSpace(content.Value) ||
        attachments.HasValue && attachments.Value.Count > 0 ||
        embeds.HasValue && embeds.Value.Count > 0 ||
        stickers.HasValue && stickers.Value.Count > 0 ||
        hasStickerEvidence;

    private static bool HasStickerEvidence(JsonElement message)
    {
        foreach (var fieldName in new[]
                 {
                     "sticker_items",
                     "stickers",
                     "stickerItems",
                     "sticker_item",
                     "stickerItem",
                     "sticker",
                     "sticker_ids",
                     "stickerIds",
                 })
        {
            if (!message.TryGetProperty(fieldName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 ||
                value.ValueKind == JsonValueKind.Object ||
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(DiscordJson.GetString(value)))
            {
                return true;
            }
        }

        if (!TryGetProperty(
                message,
                "content_parsed",
                "contentParsed",
                out var parsed) ||
            parsed.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return parsed.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object &&
            (string.Equals(
                    GetString(item, "type"),
                    "sticker",
                    StringComparison.OrdinalIgnoreCase) ||
                HasProperty(item, "sticker", "sticker_id", "stickerId")));
    }

    private readonly record struct ForwardEnvelope(
        bool IsForward,
        JsonElement? Snapshot,
        DiscordForwardSourceKey? SourceKey);

    private void LogForwardDecision(
        string messageId,
        DiscordForwardMetadata? forward,
        OptionalValue<string> content,
        OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>> attachments,
        OptionalValue<IReadOnlyList<DiscordEmbedMetadata>> embeds,
        OptionalValue<IReadOnlyList<DiscordStickerMetadata>> stickers,
        DiscordMessageFallbackKind fallbackKind)
    {
        if (forward is null)
        {
            return;
        }

        _logger.Information(
            "FORWARD",
            $"wrapper={messageId} detected=true " +
            $"sourceChannel={forward.SourceKey?.ChannelId ?? "missing"} " +
            $"sourceMessage={forward.SourceKey?.MessageId ?? "missing"} " +
            $"contentLength={(content.HasValue ? content.Value.Length : 0)} " +
            $"attachments={(attachments.HasValue ? attachments.Value.Count : 0)} " +
            $"embeds={(embeds.HasValue ? embeds.Value.Count : 0)} " +
            $"stickers={(stickers.HasValue ? stickers.Value.Count : 0)} " +
            $"resolution={forward.Resolution} fallback={fallbackKind}.");
    }

    private void LogForwardPayloadStructure(
        JsonElement message,
        string observationKind,
        string messageId)
    {
        var hasReference = TryGetProperty(
                message,
                "message_reference",
                "messageReference",
                out var reference) &&
            reference.ValueKind == JsonValueKind.Object;
        var hasSnapshotContainer = TryGetProperty(
            message,
            "message_snapshots",
            "messageSnapshots",
            out var snapshotContainer);
        if (!hasReference && !hasSnapshotContainer)
        {
            return;
        }

        var referenceType = hasReference
            ? GetInt32(reference, "type")?.ToString(CultureInfo.InvariantCulture)
                ?? GetString(reference, "type")
                ?? "missing"
            : "missing";
        var sourceChannelId = hasReference
            ? GetString(reference, "channel_id", "channelId") ?? "missing"
            : "missing";
        var sourceMessageId = hasReference
            ? GetString(reference, "message_id", "messageId") ?? "missing"
            : "missing";
        var sourceGuildId = hasReference
            ? GetString(reference, "guild_id", "guildId") ?? "missing"
            : "missing";
        var topLevelContentLength = GetString(message, "content")?.Length ?? 0;
        var topLevelParsedCount = GetContainerCount(message, "content_parsed", "contentParsed");
        var topLevelAttachmentCount = GetContainerCount(message, "attachments");
        var topLevelEmbedCount = GetContainerCount(message, "embeds");
        var topLevelStickerCount = GetContainerCount(
            message,
            "sticker_items",
            "stickerItems",
            "stickers");

        var snapshots = hasSnapshotContainer && snapshotContainer.ValueKind == JsonValueKind.Array
                ? snapshotContainer.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

        _logger.Information(
            "FORWARD-PAYLOAD",
            $"event={observationKind} wrapper={messageId} referenceType={referenceType} " +
            $"sourceChannel={sourceChannelId} sourceMessage={sourceMessageId} " +
            $"sourceGuild={sourceGuildId} topContentLength={topLevelContentLength} " +
            $"topContentParsed={topLevelParsedCount} topAttachments={topLevelAttachmentCount} " +
            $"topEmbeds={topLevelEmbedCount} topStickers={topLevelStickerCount} " +
            $"snapshotCount={snapshots.Length}.");

        for (var index = 0; index < snapshots.Length; index++)
        {
            var envelope = snapshots[index];
            var snapshot = envelope.ValueKind == JsonValueKind.Object &&
                TryGetProperty(envelope, "message", "snapshotMessage", out var nestedMessage) &&
                nestedMessage.ValueKind == JsonValueKind.Object
                    ? nestedMessage
                    : envelope;
            var propertyNames = snapshot.ValueKind == JsonValueKind.Object
                ? string.Join(
                    ',',
                    snapshot.EnumerateObject()
                        .Select(property => property.Name)
                        .OrderBy(name => name, StringComparer.Ordinal))
                : "none";
            _logger.Information(
                "FORWARD-PAYLOAD",
                $"event={observationKind} wrapper={messageId} snapshot={index} " +
                $"contentLength={GetString(snapshot, "content")?.Length ?? 0} " +
                $"contentParsed={GetContainerCount(snapshot, "content_parsed", "contentParsed")} " +
                $"attachments={GetContainerCount(snapshot, "attachments")} " +
                $"embeds={GetContainerCount(snapshot, "embeds")} " +
                $"stickerItems={GetContainerCount(snapshot, "sticker_items", "stickerItems")} " +
                $"stickers={GetContainerCount(snapshot, "stickers")} " +
                $"flags={GetInt32(snapshot, "flags")?.ToString(CultureInfo.InvariantCulture) ?? "missing"} " +
                $"type={GetInt32(snapshot, "type")?.ToString(CultureInfo.InvariantCulture) ?? GetString(snapshot, "type") ?? "missing"} " +
                $"timestampPresent={HasProperty(snapshot, "timestamp")} " +
                $"editedTimestampPresent={HasProperty(snapshot, "edited_timestamp", "editedTimestamp")} " +
                $"fields={propertyNames}.");
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string firstName,
        string secondName,
        out JsonElement value) =>
        element.TryGetProperty(firstName, out value) ||
        element.TryGetProperty(secondName, out value);

    private static bool HasProperty(JsonElement element, params string[] names) =>
        names.Any(name => element.TryGetProperty(name, out _));

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = DiscordJson.GetString(element, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = DiscordJson.GetInt32(element, name);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static int GetContainerCount(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Array => value.GetArrayLength(),
                JsonValueKind.Object or JsonValueKind.String => 1,
                _ => 0,
            };
        }

        return 0;
    }

    private static OptionalValue<DateTimeOffset?> ReadTimestamp(
        JsonElement message,
        string propertyName)
    {
        if (!message.TryGetProperty(propertyName, out var value))
        {
            return default;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return OptionalValue<DateTimeOffset?>.From(null);
        }

        var parsed = DateTimeOffset.TryParse(
            DiscordJson.GetString(value),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : (DateTimeOffset?)null;
        return OptionalValue<DateTimeOffset?>.From(parsed);
    }

    private static OptionalValue<IReadOnlyList<DiscordCustomEmoji>> ReadCustomEmojis(
        JsonElement message,
        OptionalValue<string> content)
    {
        var hasParsedContent = TryGetProperty(
            message,
            "content_parsed",
            "contentParsed",
            out var parsedContent);
        if (content.HasValue)
        {
            var contentEmojis = new List<DiscordCustomEmoji>();
            foreach (Match match in CustomEmojiPattern().Matches(content.Value))
            {
                contentEmojis.Add(new DiscordCustomEmoji(
                    match.Groups[3].Value,
                    match.Groups[2].Value,
                    match.Groups[1].Value.Length > 0));
            }

            if (contentEmojis.Count > 0 || !hasParsedContent)
            {
                return OptionalValue<IReadOnlyList<DiscordCustomEmoji>>.From(contentEmojis);
            }
        }

        if (!hasParsedContent)
        {
            return default;
        }

        var parsedEmojis = new List<DiscordCustomEmoji>();
        VisitParsedContent(parsedContent, null, parsedEmojis);
        return OptionalValue<IReadOnlyList<DiscordCustomEmoji>>.From(parsedEmojis);
    }

    private static void VisitParsedContent(
        JsonElement element,
        string? context,
        ICollection<DiscordCustomEmoji> emojis)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                VisitParsedContent(item, context, emojis);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = DiscordJson.GetString(element, "type");
        var isEmojiContext = ContainsEmoji(context) || ContainsEmoji(type);
        if (isEmojiContext)
        {
            var id = DiscordJson.GetString(element, "id")
                ?? DiscordJson.GetString(element, "emoji_id");
            var name = DiscordJson.GetString(element, "name")
                ?? DiscordJson.GetString(element, "emoji_name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                var animated = element.TryGetProperty("animated", out var animatedElement) &&
                    animatedElement.ValueKind == JsonValueKind.True;
                emojis.Add(new DiscordCustomEmoji(id, name, animated));
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            VisitParsedContent(property.Value, property.Name, emojis);
        }
    }

    private static bool ContainsEmoji(string? value) =>
        value?.Contains("emoji", StringComparison.OrdinalIgnoreCase) == true;

    private static OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>> ReadAttachments(
        JsonElement message)
    {
        if (!message.TryGetProperty("attachments", out var attachments))
        {
            return default;
        }

        if (attachments.ValueKind != JsonValueKind.Array)
        {
            return OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>>.From(
                Array.Empty<DiscordAttachmentMetadata>());
        }

        var normalized = attachments.EnumerateArray()
            .Where(attachment => attachment.ValueKind == JsonValueKind.Object)
            .Select(attachment => new DiscordAttachmentMetadata(
                DiscordJson.GetString(attachment, "id") ?? string.Empty,
                DiscordJson.GetString(attachment, "filename"),
                DiscordJson.GetString(attachment, "url"),
                DiscordJson.GetString(attachment, "proxy_url"),
                DiscordJson.GetInt64(attachment, "size"),
                DiscordJson.GetInt32(attachment, "width"),
                DiscordJson.GetInt32(attachment, "height"),
                DiscordJson.GetString(attachment, "content_type")))
            .ToArray();
        return OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>>.From(normalized);
    }

    private static OptionalValue<IReadOnlyList<DiscordEmbedMetadata>> ReadEmbeds(JsonElement message)
    {
        if (!message.TryGetProperty("embeds", out var embeds))
        {
            return default;
        }

        if (embeds.ValueKind != JsonValueKind.Array)
        {
            return OptionalValue<IReadOnlyList<DiscordEmbedMetadata>>.From(
                Array.Empty<DiscordEmbedMetadata>());
        }

        var normalized = embeds.EnumerateArray()
            .Where(embed => embed.ValueKind == JsonValueKind.Object)
            .Select(embed => new DiscordEmbedMetadata(
                DiscordJson.GetString(embed, "type"),
                DiscordJson.GetString(embed, "url"),
                DiscordJson.GetString(embed, "title"),
                GetNestedUrl(embed, "image"),
                GetNestedUrl(embed, "thumbnail")))
            .ToArray();
        return OptionalValue<IReadOnlyList<DiscordEmbedMetadata>>.From(normalized);
    }

    private static string? GetNestedUrl(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? DiscordJson.GetString(nested, "url")
            : null;

    private static OptionalValue<IReadOnlyList<DiscordMention>> ReadMentions(JsonElement message)
    {
        if (!message.TryGetProperty("mentions", out var mentions))
        {
            return default;
        }

        if (mentions.ValueKind != JsonValueKind.Array)
        {
            return OptionalValue<IReadOnlyList<DiscordMention>>.From(
                Array.Empty<DiscordMention>());
        }

        var normalized = mentions.EnumerateArray()
            .Where(mention => mention.ValueKind == JsonValueKind.Object)
            .Select(mention =>
            {
                var displayName = DiscordJson.GetString(mention, "global_name")
                    ?? DiscordJson.GetString(mention, "display_name")
                    ?? DiscordJson.GetString(mention, "username");
                return new DiscordMention(
                    DiscordJson.GetString(mention, "id") ?? string.Empty,
                    displayName);
            })
            .Where(mention => !string.IsNullOrWhiteSpace(mention.UserId))
            .GroupBy(mention => mention.UserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return OptionalValue<IReadOnlyList<DiscordMention>>.From(normalized);
    }

    private static OptionalValue<string?> ReadGuildNickname(
        JsonElement message,
        out string? sourcePath)
    {
        sourcePath = null;
        if (TryReadNicknameObject(
                message,
                "member",
                out var nickname,
                out var nicknamePropertyName))
        {
            sourcePath = $"member.{nicknamePropertyName}";
            return OptionalValue<string?>.From(nickname);
        }

        if (message.TryGetProperty("nick", out var directNickname))
        {
            sourcePath = "message.nick";
            return OptionalValue<string?>.From(
                directNickname.ValueKind == JsonValueKind.Null
                    ? null
                    : DiscordJson.GetString(directNickname));
        }

        if (TryReadNicknameObject(
                message,
                "author",
                out nickname,
                out nicknamePropertyName))
        {
            sourcePath = $"author.{nicknamePropertyName}";
            return OptionalValue<string?>.From(nickname);
        }

        foreach (var propertyName in new[] { "guild_nick", "guild_nickname", "author_nick" })
        {
            if (message.TryGetProperty(propertyName, out var value))
            {
                sourcePath = propertyName;
                return OptionalValue<string?>.From(
                    value.ValueKind == JsonValueKind.Null ? null : DiscordJson.GetString(value));
            }
        }

        return default;
    }

    private static bool TryReadNicknameObject(
        JsonElement parent,
        string propertyName,
        out string? nickname,
        out string? nicknamePropertyName)
    {
        nickname = null;
        nicknamePropertyName = null;
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var nicknameProperty in new[] { "nick", "nickname", "guild_nick", "guild_nickname" })
        {
            if (!value.TryGetProperty(nicknameProperty, out var nicknameValue))
            {
                continue;
            }

            nickname = nicknameValue.ValueKind == JsonValueKind.Null
                ? null
                : DiscordJson.GetString(nicknameValue);
            nicknamePropertyName = nicknameProperty;
            return true;
        }

        return false;
    }

    private static OptionalValue<IReadOnlyList<DiscordStickerMetadata>> ReadStickers(
        JsonElement message,
        out string? payloadField)
    {
        var candidates = new List<DiscordStickerMetadata>();
        var observedFields = new List<string>();
        foreach (var fieldName in new[]
                 {
                     "sticker_items",
                     "stickers",
                     "stickerItems",
                     "sticker_item",
                     "stickerItem",
                     "sticker",
                     "sticker_ids",
                     "stickerIds",
                 })
        {
            if (!message.TryGetProperty(fieldName, out var value))
            {
                continue;
            }

            observedFields.Add(fieldName);
            ReadStickerContainer(value, candidates);
        }

        if (TryGetProperty(message, "content_parsed", "contentParsed", out var contentParsed) &&
            ReadParsedStickerCandidates(contentParsed, candidates))
        {
            observedFields.Add(
                message.TryGetProperty("content_parsed", out _)
                    ? "content_parsed"
                    : "contentParsed");
        }

        if (observedFields.Count == 0)
        {
            payloadField = null;
            return default;
        }

        payloadField = string.Join('+', observedFields.Distinct(StringComparer.Ordinal));
        var normalized = candidates
            .Where(sticker => !string.IsNullOrWhiteSpace(sticker.StickerId) ||
                !string.IsNullOrWhiteSpace(sticker.Name))
            .GroupBy(
                sticker => string.IsNullOrWhiteSpace(sticker.StickerId)
                    ? $"name:{sticker.Name}"
                    : $"id:{sticker.StickerId}",
                StringComparer.Ordinal)
            .Select(MergeStickerMetadata)
            .ToArray();
        return OptionalValue<IReadOnlyList<DiscordStickerMetadata>>.From(normalized);
    }

    private static void ReadStickerContainer(
        JsonElement value,
        ICollection<DiscordStickerMetadata> target)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ReadStickerContainer(item, target);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var stickerId = DiscordJson.GetString(value);
            if (!string.IsNullOrWhiteSpace(stickerId))
            {
                target.Add(new DiscordStickerMetadata(stickerId, string.Empty, null, null));
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (value.TryGetProperty("sticker", out var nestedSticker) &&
            nestedSticker.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            ReadStickerContainer(nestedSticker, target);
        }

        var metadata = new DiscordStickerMetadata(
            DiscordJson.GetString(value, "id")
                ?? DiscordJson.GetString(value, "sticker_id")
                ?? DiscordJson.GetString(value, "stickerId")
                ?? string.Empty,
            DiscordJson.GetString(value, "name")
                ?? DiscordJson.GetString(value, "sticker_name")
                ?? DiscordJson.GetString(value, "stickerName")
                ?? string.Empty,
            DiscordJson.GetInt32(value, "format_type")
                ?? DiscordJson.GetInt32(value, "format")
                ?? DiscordJson.GetInt32(value, "formatType"),
            DiscordJson.GetString(value, "url")
                ?? DiscordJson.GetString(value, "asset_url")
                ?? DiscordJson.GetString(value, "assetUrl")
                ?? DiscordJson.GetString(value, "proxy_url")
                ?? DiscordJson.GetString(value, "proxyUrl"));
        if (!string.IsNullOrWhiteSpace(metadata.StickerId) ||
            !string.IsNullOrWhiteSpace(metadata.Name))
        {
            target.Add(metadata);
        }
    }

    private static bool ReadParsedStickerCandidates(
        JsonElement contentParsed,
        ICollection<DiscordStickerMetadata> target)
    {
        if (contentParsed.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var before = target.Count;
        foreach (var item in contentParsed.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = DiscordJson.GetString(item, "type");
            if (string.Equals(type, "sticker", StringComparison.OrdinalIgnoreCase) ||
                item.TryGetProperty("sticker", out _) ||
                item.TryGetProperty("sticker_id", out _) ||
                item.TryGetProperty("stickerId", out _))
            {
                ReadStickerContainer(item, target);
            }
        }

        return target.Count > before;
    }

    private static DiscordStickerMetadata MergeStickerMetadata(
        IGrouping<string, DiscordStickerMetadata> group)
    {
        var values = group.ToArray();
        return new DiscordStickerMetadata(
            values.Select(value => value.StickerId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            values.Select(value => value.Name)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            values.Select(value => value.FormatType).FirstOrDefault(value => value.HasValue),
            values.Select(value => value.AssetUrl)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool IsOpaqueEmptyMessage(
        JsonElement message,
        OptionalValue<string> content,
        OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>> attachments,
        OptionalValue<IReadOnlyList<DiscordEmbedMetadata>> embeds,
        bool hasStickerEvidence)
    {
        if (hasStickerEvidence ||
            !content.HasValue || !string.IsNullOrWhiteSpace(content.Value) ||
            attachments.HasValue && attachments.Value.Count > 0 ||
            embeds.HasValue && embeds.Value.Count > 0 ||
            DiscordJson.GetInt32(message, "type") != 0)
        {
            return false;
        }

        return !message.TryGetProperty("blocked", out var blocked) ||
            blocked.ValueKind != JsonValueKind.True;
    }

    private void LogAuthorMetadata(
        JsonElement message,
        string observationKind,
        string messageId,
        OptionalValue<string> authorId,
        OptionalValue<string> username,
        OptionalValue<string?> displayName,
        OptionalValue<string?> guildNickname,
        OptionalValue<string> guildId,
        string? guildNicknamePath,
        OptionalValue<DiscordDisplayNameSource> source)
    {
        var memberPresent = message.TryGetProperty("member", out var member) &&
            member.ValueKind == JsonValueKind.Object;
        var authorDisplayName = ReadNestedString(message, "author", "display_name");
        var topLevelNick = ReadDirectString(message, "nick");
        var topLevelNickname = ReadDirectString(message, "nickname");
        var candidatePaths = DescribePresentNameFields(message);
        var diagnosticKey = string.Join(
            '|',
            observationKind,
            authorId.HasValue ? authorId.Value : "unknown",
            memberPresent,
            candidatePaths);

        lock (_nameSourceDiagnosticSync)
        {
            if (_nameSourceDiagnosticKeys.Count >= MaxNameSourceDiagnostics ||
                !_nameSourceDiagnosticKeys.Add(diagnosticKey))
            {
                return;
            }
        }

        _logger.Information(
            "NAME-SOURCE",
            $"event={SanitizeMetadata(observationKind)} message={SanitizeMetadata(messageId)} guild={SanitizeMetadata(guildId.HasValue ? guildId.Value : "unknown")} author={SanitizeMetadata(authorId.HasValue ? authorId.Value : "unknown")} memberPresent={memberPresent} candidatePaths={SanitizeMetadata(candidatePaths)} topNick={FormatValue(topLevelNick)} topNickname={FormatValue(topLevelNickname)} usernamePresent={HasText(username)} username={FormatValue(username.HasValue ? username.Value : null)} globalNamePresent={displayName.HasValue && !string.IsNullOrWhiteSpace(displayName.Value)} globalName={FormatValue(displayName.HasValue ? displayName.Value : null)} authorDisplayNamePresent={!string.IsNullOrWhiteSpace(authorDisplayName)} authorDisplayName={FormatValue(authorDisplayName)} guildNickPresent={guildNickname.HasValue && !string.IsNullOrWhiteSpace(guildNickname.Value)} guildNick={FormatValue(guildNickname.HasValue ? guildNickname.Value : null)} sourcePath={SanitizeMetadata(guildNicknamePath ?? "none")} selected={(source.HasValue ? source.Value : DiscordDisplayNameSource.Unknown)}.");

        static bool HasText(OptionalValue<string> value) =>
            value.HasValue && !string.IsNullOrWhiteSpace(value.Value);

        static string FormatValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "<none>" : $"\"{SanitizeMetadata(value)}\"";
    }

    private static string? ReadNestedString(
        JsonElement parent,
        string objectProperty,
        string valueProperty)
    {
        return parent.TryGetProperty(objectProperty, out var nested) &&
            nested.ValueKind == JsonValueKind.Object &&
            nested.TryGetProperty(valueProperty, out var value) &&
            value.ValueKind != JsonValueKind.Null
                ? DiscordJson.GetString(value)
                : null;
    }

    private static string? ReadDirectString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Null
                ? DiscordJson.GetString(value)
                : null;
    }

    private static string DescribePresentNameFields(JsonElement message)
    {
        var paths = new List<string>();
        AddObjectFields(message, "member", paths);
        AddObjectFields(message, "author", paths);
        foreach (var propertyName in new[]
                 {
                     "guild_nick",
                     "guild_nickname",
                     "author_nick",
                     "nick",
                     "nickname",
                 })
        {
            if (message.TryGetProperty(propertyName, out _))
            {
                paths.Add(propertyName);
            }
        }

        return paths.Count == 0 ? "none" : string.Join(',', paths);

        static void AddObjectFields(
            JsonElement parent,
            string objectProperty,
            ICollection<string> target)
        {
            if (!parent.TryGetProperty(objectProperty, out var nested) ||
                nested.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var propertyName in new[]
                     {
                         "nick",
                         "nickname",
                         "guild_nick",
                         "guild_nickname",
                         "display_name",
                         "global_name",
                         "username",
                     })
            {
                if (nested.TryGetProperty(propertyName, out _))
                {
                    target.Add($"{objectProperty}.{propertyName}");
                }
            }
        }
    }

    private void LogStickerMetadata(
        string messageId,
        string? payloadField,
        OptionalValue<IReadOnlyList<DiscordStickerMetadata>> stickers)
    {
        if (!stickers.HasValue)
        {
            return;
        }

        if (stickers.Value.Count == 0)
        {
            _logger.Information(
                "STICKER",
                $"message={SanitizeMetadata(messageId)} payloadField={payloadField ?? "unknown"} count=0 recognized=false.");
            return;
        }

        foreach (var sticker in stickers.Value)
        {
            _logger.Information(
                "STICKER",
                $"message={SanitizeMetadata(messageId)} payloadField={payloadField ?? "unknown"} count={stickers.Value.Count} id={SanitizeMetadata(sticker.StickerId)} name={SanitizeMetadata(sticker.Name)} format={sticker.FormatType?.ToString() ?? "unknown"} urlPresent={!string.IsNullOrWhiteSpace(sticker.AssetUrl)}.");
        }
    }

    private static string SanitizeMetadata(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    [GeneratedRegex("<(a?):([A-Za-z0-9_]+):([0-9]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex CustomEmojiPattern();
}
