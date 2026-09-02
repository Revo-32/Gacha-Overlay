namespace GachaOverlay.Core.Discord.Messages;

public sealed record NormalizedDiscordMessage(
    string MessageId,
    string ChannelId,
    string AuthorId,
    string AuthorUsername,
    string? AuthorDisplayName,
    string Content,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EditedAt,
    IReadOnlyList<DiscordCustomEmoji> CustomEmojis,
    IReadOnlyList<DiscordAttachmentMetadata> Attachments,
    IReadOnlyList<DiscordEmbedMetadata> Embeds,
    IReadOnlyList<DiscordMention> Mentions)
{
    public string GuildId { get; init; } = string.Empty;

    public string? AuthorGuildNickname { get; init; }

    public DiscordDisplayNameSource AuthorDisplayNameSource { get; init; } =
        DiscordDisplayNameSource.Unknown;

    public DiscordDisplayNameSource AuthorGuildNicknameObservationSource { get; init; } =
        DiscordDisplayNameSource.Unknown;

    public IReadOnlyList<DiscordStickerMetadata> Stickers { get; init; } =
        Array.Empty<DiscordStickerMetadata>();

    public DiscordForwardMetadata? Forward { get; init; }

    public DiscordMessageFallbackKind FallbackKind { get; init; }

    public DiscordRemoteMessageMetadata? RemoteMetadata { get; init; }
}
