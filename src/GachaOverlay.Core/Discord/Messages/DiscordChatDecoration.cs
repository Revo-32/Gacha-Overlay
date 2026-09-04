namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordRoleIcon(
    string Kind,
    string Value,
    string? Url = null);

public sealed record DiscordAuthorStyle(
    string? ColorRoleId,
    uint? Color,
    string? IconRoleId,
    DiscordRoleIcon? Icon);

public sealed record DiscordMessageReaction(
    DiscordCustomEmoji Emoji,
    int Count);
