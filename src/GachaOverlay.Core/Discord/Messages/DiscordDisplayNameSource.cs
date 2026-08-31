namespace GachaOverlay.Core.Discord.Messages;

public enum DiscordDisplayNameSource
{
    Unknown,
    RpcGuildNickname,
    GuildNickname = RpcGuildNickname,
    CachedGuildNickname,
    GlobalDisplayName,
    Username,
    UiAutomationGuildNickname,
    ManualOverride,
}
