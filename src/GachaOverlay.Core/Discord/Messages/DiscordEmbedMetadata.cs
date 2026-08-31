namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordEmbedMetadata(
    string? Type,
    string? Url,
    string? Title,
    string? ImageUrl,
    string? ThumbnailUrl);
