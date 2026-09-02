using Discord;
using Discord.WebSocket;

namespace LSOverlay.Backend.Discord;

internal static class DiscordGatewayPolicy
{
    public const int MessageCacheSize = 0;

    public static GatewayIntents RequiredIntents { get; } =
        GatewayIntents.Guilds |
        GatewayIntents.GuildMessages |
        GatewayIntents.GuildMessageReactions |
        GatewayIntents.GuildMessagePolls |
        GatewayIntents.MessageContent |
        GatewayIntents.GuildPresences;

    public static DiscordSocketConfig CreateSocketConfiguration() => new()
    {
        GatewayIntents = RequiredIntents,
        MessageCacheSize = MessageCacheSize,
        AlwaysDownloadUsers = false,
        AlwaysDownloadDefaultStickers = false,
        AlwaysResolveStickers = false,
        IncludeRawPayloadOnGatewayErrors = false,
        LogLevel = LogSeverity.Info,
    };
}
