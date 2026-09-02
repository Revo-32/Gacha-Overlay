namespace LSOverlay.Backend.Events;

internal static class ReactionIdentityNormalizer
{
    public static BackendReactionSignal Create(
        BackendReactionOperation operation,
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong? userId,
        ulong? emojiId,
        string? emojiName,
        DateTimeOffset observedAt) => new(
            operation,
            guildId,
            channelId,
            messageId,
            userId,
            emojiId,
            string.IsNullOrWhiteSpace(emojiName) ? null : emojiName,
            observedAt);
}
