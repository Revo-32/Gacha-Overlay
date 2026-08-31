namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordMessageState(
    long Generation,
    bool IsBootstrapping,
    IReadOnlyList<NormalizedDiscordMessage> MainChat,
    IReadOnlyList<NormalizedDiscordMessage> SalesSource)
{
    public static DiscordMessageState Empty { get; } = new(
        0,
        false,
        Array.Empty<NormalizedDiscordMessage>(),
        Array.Empty<NormalizedDiscordMessage>());
}
