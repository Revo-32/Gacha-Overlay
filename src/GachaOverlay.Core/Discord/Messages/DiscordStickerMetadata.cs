namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordStickerMetadata(
    string StickerId,
    string Name,
    int? FormatType,
    string? AssetUrl)
{
    public string? RemoteFormat { get; init; }
}
