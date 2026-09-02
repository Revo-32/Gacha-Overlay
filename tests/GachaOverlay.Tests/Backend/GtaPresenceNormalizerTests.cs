using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;

namespace GachaOverlay.Tests.Backend;

public sealed class GtaPresenceNormalizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
    private readonly GtaPresenceNormalizer _normalizer = new(clock: () => Now);

    [Fact]
    public void EnhancedProfile_UsesFixedApplicationIdAndExactOnlineState()
    {
        Assert.Equal((ulong)1329870933695135785, GtaPresenceProfile.Enhanced.ApplicationId);
        Assert.Equal("GTA Online", GtaPresenceProfile.Enhanced.OnlineState);
        Assert.Equal("Grand Theft Auto V Enhanced", GtaPresenceProfile.Enhanced.DisplayLabel);
    }

    [Fact]
    public void Normalize_UsesStructuredPartyForExactEnhancedActivity()
    {
        var result = Normalize(Activity(state: "GTA Online", members: 11, capacity: 32));

        Assert.True(result.GtaActivityPresent);
        Assert.True(result.GtaOnlineActive);
        Assert.Equal(11, result.CurrentPlayers);
        Assert.Equal(32, result.MaximumPlayers);
        Assert.Equal(Now, result.ObservedAt);
    }

    [Theory]
    [InlineData("gta online")]
    [InlineData("GTA Online ")]
    [InlineData("Story Mode")]
    [InlineData(null)]
    public void Normalize_RequiresExactOrdinalState(string? state)
    {
        var result = Normalize(Activity(state: state, members: 1, capacity: 30));

        Assert.True(result.GtaActivityPresent);
        Assert.False(result.GtaOnlineActive);
        Assert.Null(result.CurrentPlayers);
        Assert.Null(result.MaximumPlayers);
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(1, 0)]
    [InlineData(31, 30)]
    [InlineData(null, 30)]
    [InlineData(1, null)]
    public void Normalize_RejectsInvalidOrMissingStructuredParty(int? members, int? capacity)
    {
        var result = Normalize(Activity(
            "GTA Online",
            members is null ? null : (long?)members.Value,
            capacity is null ? null : (long?)capacity.Value));

        Assert.True(result.GtaActivityPresent);
        Assert.False(result.GtaOnlineActive);
        Assert.Null(result.CurrentPlayers);
        Assert.Null(result.MaximumPlayers);
    }

    [Fact]
    public void Normalize_IgnoresWrongApplicationAndNonPlayingActivities()
    {
        var result = _normalizer.Normalize(
            42,
            BackendDiscordPresenceStatus.Online,
            new[]
            {
                Activity("GTA Online", 2, 30) with { ApplicationId = 1 },
                Activity("GTA Online", 2, 30) with { IsPlaying = false },
            });

        Assert.False(result.GtaActivityPresent);
        Assert.False(result.GtaOnlineActive);
    }

    [Theory]
    [InlineData((int)BackendDiscordPresenceStatus.Offline)]
    [InlineData((int)BackendDiscordPresenceStatus.AwaitingPresence)]
    public void Normalize_OfflineAndAwaitingNeverExposeActivity(int statusValue)
    {
        var status = (BackendDiscordPresenceStatus)statusValue;
        var result = _normalizer.Normalize(42, status, new[] { Activity("GTA Online", 2, 30) });

        Assert.False(result.GtaActivityPresent);
        Assert.False(result.GtaOnlineActive);
    }

    private TrackedHostPresenceSnapshot Normalize(BackendActivityCandidate activity) =>
        _normalizer.Normalize(42, BackendDiscordPresenceStatus.Online, new[] { activity });

    private static BackendActivityCandidate Activity(
        string? state,
        long? members,
        long? capacity) => new(
            GtaPresenceProfile.Enhanced.ApplicationId,
            "Grand Theft Auto V",
            true,
            state,
            members,
            capacity);
}
