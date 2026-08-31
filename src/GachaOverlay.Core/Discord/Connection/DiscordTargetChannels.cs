namespace GachaOverlay.Core.Discord.Connection;

public sealed record DiscordTargetChannels(
    string GuildId,
    string GuildName,
    string MainChannelId,
    string MainChannelName,
    string SalesChannelId,
    string SalesChannelName);
