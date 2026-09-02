using Discord;

namespace LSOverlay.Backend.Discord;

internal sealed record DiscordSdkCapabilitySnapshot(
    bool ForwardedMessages,
    bool MessageSnapshot,
    bool Stickers,
    bool Attachments,
    bool Embeds,
    bool Components,
    bool ReferencedMessage,
    bool Poll);

internal static class DiscordSdkCapabilityAudit
{
    public static DiscordSdkCapabilitySnapshot Inspect() => new(
        typeof(IUserMessage).GetProperty(nameof(IUserMessage.ForwardedMessages)) is not null,
        typeof(MessageSnapshot).GetField(nameof(MessageSnapshot.Message)) is not null,
        typeof(IMessage).GetProperty(nameof(IMessage.Stickers)) is not null,
        typeof(IMessage).GetProperty(nameof(IMessage.Attachments)) is not null,
        typeof(IMessage).GetProperty(nameof(IMessage.Embeds)) is not null,
        typeof(IMessage).GetProperty(nameof(IMessage.Components)) is not null,
        typeof(IUserMessage).GetProperty(nameof(IUserMessage.ReferencedMessage)) is not null,
        typeof(IUserMessage).GetProperty(nameof(IUserMessage.Poll)) is not null);

    public static bool HasRequiredSurface(DiscordSdkCapabilitySnapshot snapshot) =>
        snapshot.ForwardedMessages &&
        snapshot.MessageSnapshot &&
        snapshot.Stickers &&
        snapshot.Attachments &&
        snapshot.Embeds &&
        snapshot.Components &&
        snapshot.ReferencedMessage;

    // Compile-time pins for the Discord.Net 3.20.1 surface used by later normalization.
    public static void AssertCompileTimeSurface(IUserMessage message, MessageSnapshot snapshot)
    {
        _ = message.ForwardedMessages;
        _ = snapshot.Message;
        _ = message.Stickers;
        _ = message.Attachments;
        _ = message.Embeds;
        _ = message.Components;
        _ = message.ReferencedMessage;
        _ = message.Poll;
    }
}
