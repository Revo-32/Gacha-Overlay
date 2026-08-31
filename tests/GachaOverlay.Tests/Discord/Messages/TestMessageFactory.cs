using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Discord.Messages;

internal static class TestMessageFactory
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DiscordMessagePatch FullPatch(
        long id,
        string channelId = "main",
        string? content = null,
        int? order = null,
        string authorId = "author-1") =>
        new(id.ToString())
        {
            ChannelId = OptionalValue<string>.From(channelId),
            AuthorId = OptionalValue<string>.From(authorId),
            AuthorUsername = OptionalValue<string>.From("user"),
            AuthorDisplayName = OptionalValue<string?>.From("Display User"),
            Content = OptionalValue<string>.From(content ?? $"message-{id}"),
            CreatedAt = OptionalValue<DateTimeOffset?>.From(Epoch.AddSeconds(order ?? (int)id)),
            EditedAt = OptionalValue<DateTimeOffset?>.From(null),
            CustomEmojis = OptionalValue<IReadOnlyList<DiscordCustomEmoji>>.From(
                Array.Empty<DiscordCustomEmoji>()),
            Attachments = OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>>.From(
                Array.Empty<DiscordAttachmentMetadata>()),
            Embeds = OptionalValue<IReadOnlyList<DiscordEmbedMetadata>>.From(
                Array.Empty<DiscordEmbedMetadata>()),
        };

    public static DiscordMessagePatch ContentPatch(
        long id,
        string content,
        string channelId = "main") =>
        new(id.ToString())
        {
            ChannelId = OptionalValue<string>.From(channelId),
            Content = OptionalValue<string>.From(content),
        };
}
