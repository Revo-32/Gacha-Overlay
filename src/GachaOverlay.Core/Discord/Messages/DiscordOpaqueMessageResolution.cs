namespace GachaOverlay.Core.Discord.Messages;

public enum DiscordOpaqueMessageResolutionKind
{
    Unknown,
    ForwardedText,
    ForwardedMessage,
    Sticker,
}

public sealed record DiscordOpaqueMessageResolution(
    DiscordOpaqueMessageResolutionKind Kind,
    string? Content = null);

public interface IDiscordOpaqueMessageResolver
{
    Task<DiscordOpaqueMessageResolution> ResolveAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken);
}
