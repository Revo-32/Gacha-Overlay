namespace GachaOverlay.Core.Discord.Messages;

public enum DiscordMessageFallbackKind
{
    None,
    PendingHydration,
    Message,
    Sticker,
    ForwardedMessage,
}

public enum DiscordForwardResolutionMode
{
    None,
    FlattenedPayload,
    Snapshot,
    LookupPending,
    LookupResolved,
    LookupFailed,
    Fallback,
}

public sealed record DiscordForwardSourceKey(
    string GuildId,
    string ChannelId,
    string MessageId);

public sealed record DiscordForwardMetadata(
    DiscordForwardResolutionMode Resolution,
    DiscordForwardSourceKey? SourceKey,
    bool HasStickerEvidence)
{
    public bool RequiresLookup =>
        Resolution == DiscordForwardResolutionMode.LookupPending && SourceKey is not null;
}

public sealed record DiscordForwardContent(
    string Content,
    IReadOnlyList<DiscordCustomEmoji> CustomEmojis,
    IReadOnlyList<DiscordAttachmentMetadata> Attachments,
    IReadOnlyList<DiscordEmbedMetadata> Embeds,
    IReadOnlyList<DiscordMention> Mentions,
    IReadOnlyList<DiscordStickerMetadata> Stickers,
    bool HasStickerEvidence)
{
    public bool IsSufficient =>
        !string.IsNullOrWhiteSpace(Content) ||
        Attachments.Count > 0 ||
        Embeds.Count > 0 ||
        Stickers.Count > 0 ||
        HasStickerEvidence;
}
