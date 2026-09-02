using Discord;
using Discord.WebSocket;

namespace LSOverlay.Backend.Discord;

internal sealed record DiscordPermissionAuditResult(
    int TextChannelCount,
    int ViewableChannelCount,
    int HistoryReadableChannelCount,
    int ReactionCapableChannelCount,
    int SendCapableChannelCount,
    bool HasAdministrator,
    bool HasManagementPermissions,
    bool IsOverPrivileged)
{
    public bool HasRequiredReadAccess =>
        ViewableChannelCount > 0 && HistoryReadableChannelCount > 0;
}

internal static class DiscordPermissionAuditor
{
    public static DiscordPermissionAuditResult Audit(SocketGuild guild)
    {
        ArgumentNullException.ThrowIfNull(guild);
        var user = guild.CurrentUser;
        var guildPermissions = user.GuildPermissions;
        var administrator = guildPermissions.Administrator;
        var management = guildPermissions.ManageGuild ||
            guildPermissions.ManageChannels ||
            guildPermissions.ManageRoles ||
            guildPermissions.ManageMessages ||
            guildPermissions.ManageWebhooks ||
            guildPermissions.KickMembers ||
            guildPermissions.BanMembers ||
            guildPermissions.ModerateMembers;
        var viewable = 0;
        var readable = 0;
        var reactions = 0;
        var send = 0;
        foreach (var channel in guild.TextChannels)
        {
            var permissions = user.GetPermissions(channel);
            if (permissions.ViewChannel)
            {
                viewable++;
            }

            if (permissions.ViewChannel && permissions.ReadMessageHistory)
            {
                readable++;
            }

            if (permissions.ViewChannel && permissions.AddReactions)
            {
                reactions++;
            }

            if (permissions.ViewChannel && permissions.SendMessages)
            {
                send++;
            }
        }

        return new DiscordPermissionAuditResult(
            guild.TextChannels.Count,
            viewable,
            readable,
            reactions,
            send,
            administrator,
            management,
            administrator || management || send > 0);
    }
}
