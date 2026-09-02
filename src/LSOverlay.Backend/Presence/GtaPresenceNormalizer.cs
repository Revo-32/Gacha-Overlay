using LSOverlay.Backend.Events;

namespace LSOverlay.Backend.Presence;

internal sealed class GtaPresenceNormalizer
{
    private readonly GtaPresenceProfile _profile;
    private readonly Func<DateTimeOffset> _clock;

    public GtaPresenceNormalizer(
        GtaPresenceProfile? profile = null,
        Func<DateTimeOffset>? clock = null)
    {
        _profile = profile ?? GtaPresenceProfile.Enhanced;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TrackedHostPresenceSnapshot Normalize(
        ulong hostId,
        BackendDiscordPresenceStatus discordStatus,
        IEnumerable<BackendActivityCandidate> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);

        if (discordStatus is BackendDiscordPresenceStatus.Offline or
            BackendDiscordPresenceStatus.AwaitingPresence)
        {
            return Empty(hostId, discordStatus);
        }

        foreach (var activity in activities)
        {
            if (!activity.IsPlaying || activity.ApplicationId != _profile.ApplicationId)
            {
                continue;
            }

            var validParty = TryValidateParty(
                activity.PartyMembers,
                activity.PartyCapacity,
                out var current,
                out var maximum);
            var online = string.Equals(
                    activity.State,
                    _profile.OnlineState,
                    StringComparison.Ordinal) &&
                validParty;
            return new TrackedHostPresenceSnapshot(
                hostId,
                discordStatus,
                true,
                online,
                online ? current : null,
                online ? maximum : null,
                _clock());
        }

        return Empty(hostId, discordStatus);
    }

    private TrackedHostPresenceSnapshot Empty(
        ulong hostId,
        BackendDiscordPresenceStatus status) => new(
            hostId,
            status,
            false,
            false,
            null,
            null,
            _clock());

    private static bool TryValidateParty(
        long? members,
        long? capacity,
        out int current,
        out int maximum)
    {
        current = 0;
        maximum = 0;
        if (members is null || capacity is null ||
            members < 0 || capacity <= 0 || members > capacity ||
            members > int.MaxValue || capacity > int.MaxValue)
        {
            return false;
        }

        current = (int)members.Value;
        maximum = (int)capacity.Value;
        return true;
    }
}
