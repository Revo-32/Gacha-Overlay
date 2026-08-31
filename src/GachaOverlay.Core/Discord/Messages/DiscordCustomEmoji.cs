namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordCustomEmoji(
    string EmojiId,
    string Name,
    bool Animated);
