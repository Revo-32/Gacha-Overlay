namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordMessagePatch
{
    public DiscordMessagePatch(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        MessageId = messageId;
    }

    public string MessageId { get; }

    public OptionalValue<string> ChannelId { get; init; }

    public OptionalValue<string> GuildId { get; init; }

    public OptionalValue<string> AuthorId { get; init; }

    public OptionalValue<string> AuthorUsername { get; init; }

    public OptionalValue<string?> AuthorDisplayName { get; init; }

    public OptionalValue<string?> AuthorGuildNickname { get; init; }

    public OptionalValue<DiscordDisplayNameSource> AuthorDisplayNameSource { get; init; }

    public OptionalValue<DiscordDisplayNameSource> AuthorGuildNicknameObservationSource { get; init; }

    public OptionalValue<string> Content { get; init; }

    public OptionalValue<DateTimeOffset?> CreatedAt { get; init; }

    public OptionalValue<DateTimeOffset?> EditedAt { get; init; }

    public OptionalValue<IReadOnlyList<DiscordCustomEmoji>> CustomEmojis { get; init; }

    public OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>> Attachments { get; init; }

    public OptionalValue<IReadOnlyList<DiscordEmbedMetadata>> Embeds { get; init; }

    public OptionalValue<IReadOnlyList<DiscordMention>> Mentions { get; init; }

    public OptionalValue<IReadOnlyList<DiscordStickerMetadata>> Stickers { get; init; }

    public OptionalValue<DiscordForwardMetadata?> Forward { get; init; }

    public OptionalValue<DiscordMessageFallbackKind> FallbackKind { get; init; }
}
