namespace LSOverlay.Backend.Chat;

internal enum ChatPermissionTarget
{
    Role,
    Member,
}

internal sealed record ChatRolePermission(ulong RoleId, ulong Permissions);

internal sealed record ChatPermissionOverwrite(
    ulong TargetId,
    ChatPermissionTarget TargetType,
    ulong Allow,
    ulong Deny);

internal static class DiscordPermissionEvaluator
{
    internal const ulong Administrator = 1UL << 3;
    internal const ulong AddReactions = 1UL << 6;
    internal const ulong ViewChannel = 1UL << 10;
    internal const ulong ReadMessageHistory = 1UL << 16;
    internal const ulong AllPermissions = ulong.MaxValue;

    public static ulong Compute(
        ulong guildId,
        ulong memberId,
        IReadOnlyCollection<ulong> memberRoleIds,
        IReadOnlyCollection<ChatRolePermission> guildRoles,
        IReadOnlyCollection<ChatPermissionOverwrite> channelOverwrites)
    {
        ArgumentNullException.ThrowIfNull(memberRoleIds);
        ArgumentNullException.ThrowIfNull(guildRoles);
        ArgumentNullException.ThrowIfNull(channelOverwrites);

        var roles = guildRoles.ToDictionary(role => role.RoleId);
        var permissions = roles.TryGetValue(guildId, out var everyone)
            ? everyone.Permissions
            : 0UL;
        foreach (var roleId in memberRoleIds)
        {
            if (roles.TryGetValue(roleId, out var role))
            {
                permissions |= role.Permissions;
            }
        }

        if ((permissions & Administrator) != 0)
        {
            return AllPermissions;
        }

        var everyoneOverwrite = channelOverwrites.FirstOrDefault(overwrite =>
            overwrite.TargetType == ChatPermissionTarget.Role &&
            overwrite.TargetId == guildId);
        if (everyoneOverwrite is not null)
        {
            permissions &= ~everyoneOverwrite.Deny;
            permissions |= everyoneOverwrite.Allow;
        }

        ulong roleDeny = 0;
        ulong roleAllow = 0;
        var roleSet = memberRoleIds.ToHashSet();
        foreach (var overwrite in channelOverwrites)
        {
            if (overwrite.TargetType != ChatPermissionTarget.Role ||
                overwrite.TargetId == guildId ||
                !roleSet.Contains(overwrite.TargetId))
            {
                continue;
            }

            roleDeny |= overwrite.Deny;
            roleAllow |= overwrite.Allow;
        }

        permissions &= ~roleDeny;
        permissions |= roleAllow;

        var memberOverwrite = channelOverwrites.FirstOrDefault(overwrite =>
            overwrite.TargetType == ChatPermissionTarget.Member &&
            overwrite.TargetId == memberId);
        if (memberOverwrite is not null)
        {
            permissions &= ~memberOverwrite.Deny;
            permissions |= memberOverwrite.Allow;
        }

        return permissions;
    }

    public static bool CanRead(ulong permissions) =>
        (permissions & ViewChannel) != 0 &&
        (permissions & ReadMessageHistory) != 0;

    public static bool CanAddReactions(ulong permissions) =>
        CanRead(permissions) &&
        (permissions & AddReactions) != 0;
}
