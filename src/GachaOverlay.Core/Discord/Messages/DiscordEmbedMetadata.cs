namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordEmbedMetadata(
    string? Type,
    string? Url,
    string? Title,
    string? ImageUrl,
    string? ThumbnailUrl,
    string? Description = null,
    DateTimeOffset? Timestamp = null,
    uint? Color = null,
    string? VideoUrl = null,
    string? AuthorName = null,
    string? AuthorUrl = null,
    string? FooterText = null,
    string? ProviderName = null,
    IReadOnlyList<DiscordEmbedFieldMetadata>? Fields = null);

public sealed record DiscordEmbedFieldMetadata(
    string Name,
    string Value,
    bool IsInline);
