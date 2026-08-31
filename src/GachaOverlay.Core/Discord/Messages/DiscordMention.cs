namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordMention(
    string UserId,
    string? DisplayName);
