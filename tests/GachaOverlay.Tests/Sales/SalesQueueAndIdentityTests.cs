using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesQueueAndIdentityTests
{
    [Fact]
    public void Test36_ZeroActive_HasNoCurrentSellerAndZeroCounts()
    {
        var snapshot = SalesTestFactory.Engine().Current;
        Assert.Null(snapshot.CurrentSeller);
        Assert.Equal(0, snapshot.ActiveCount);
        Assert.Equal(0, snapshot.WaitingCount);
    }

    [Fact]
    public void Test37_OneActive_HasCurrentAndNoWaiting()
    {
        var engine = EngineWith(1);
        Assert.NotNull(engine.Current.CurrentSeller);
        Assert.Equal(1, engine.Current.ActiveCount);
        Assert.Equal(0, engine.Current.WaitingCount);
    }

    [Fact]
    public void Test38_FourActive_HasThreeWaiting()
    {
        var engine = EngineWith(4);
        Assert.Equal(4, engine.Current.ActiveCount);
        Assert.Equal(3, engine.Current.WaitingCount);
    }

    [Fact]
    public void Test39_SameCreatedAt_UsesNumericMessageIdTieBreaker()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("10"),
            SalesTestFactory.Message("2"),
            SalesTestFactory.Message("9"),
        });
        Assert.Equal(new[] { "2", "9", "10" }, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test40_SameAuthorMultipleMessages_AreSeparateEntries()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", "same", 1),
            SalesTestFactory.Message("2", "same", 2),
        });
        Assert.Equal(2, engine.Current.ActiveCount);
    }

    [Fact]
    public void Test41_DeletingCurrentSeller_AdvancesNext()
    {
        var engine = EngineWith(2);
        engine.ApplySourceDelete("1");
        Assert.Equal("2", engine.Current.CurrentSeller!.MessageId);
    }

    [Fact]
    public void Test42_RepeatedRecalculation_KeepsQueueStable()
    {
        var engine = EngineWith(3);
        var before = engine.Current.ActiveItems.Select(x => x.MessageId).ToArray();
        Assert.False(engine.ApplySourceSnapshot(Enumerable.Range(1, 3)
            .Select(id => SalesTestFactory.Message(
                id.ToString(),
                $"author-{id}",
                seconds: id))));
        Assert.Equal(before, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test43_CurrentSellerIsSelf_UsesUserId()
    {
        var engine = EngineWith(2);
        engine.SetAuthenticatedUser("author-1");
        Assert.True(engine.Current.CurrentSellerIsSelf);
    }

    [Fact]
    public void Test44_NextSellerIsSelf_UsesUserId()
    {
        var engine = EngineWith(2);
        engine.SetAuthenticatedUser("author-2");
        Assert.True(engine.Current.NextSellerIsSelf);
    }

    [Fact]
    public void Test45_SameNicknameDifferentUserId_IsNotSelf()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1", "other", nickname: "Shared"));
        engine.SetAuthenticatedUser("self");
        Assert.False(engine.Current.CurrentSellerIsSelf);
    }

    [Fact]
    public void Test46_DifferentNicknameSameUserId_IsSelf()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1", "self", nickname: "Different"));
        engine.SetAuthenticatedUser("self");
        Assert.True(engine.Current.CurrentSellerIsSelf);
    }

    [Fact]
    public void Test47_Sales_UsesGuildDisplayNameResolver()
    {
        var resolver = new RecordingResolver();
        var engine = SalesTestFactory.Engine(resolver: resolver);
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Assert.True(resolver.ResolveCalls > 0);
    }

    [Fact]
    public void Test48_RpcGuildNickname_IsPreferred()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1", nickname: "Exact", globalName: "Wrong"));
        var current = engine.Current.CurrentSeller!;
        Assert.Equal("Exact", current.DisplayName);
        Assert.True(current.IsExactGuildNickname);
    }

    [Fact]
    public void Test49_GlobalFallback_IsNotUsedAsIdentity()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1", "author-id", nickname: null, globalName: "Pretty"));
        engine.SetAuthenticatedUser("Pretty");
        Assert.False(engine.Current.CurrentSellerIsSelf);
        Assert.Equal("author-id", engine.Current.CurrentSeller!.AuthorId);
    }

    [Fact]
    public void Test50_ResolverUpdate_RefreshesQueueLabel()
    {
        var resolver = new GuildDisplayNameResolver(clock: () => SalesTestFactory.Epoch);
        resolver.SetAccountScope("account");
        var engine = SalesTestFactory.Engine(resolver: resolver);
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1", nickname: null, globalName: "Fallback"));
        resolver.Observe(new GuildNicknameObservation(
            "guild", "author", "1", "Exact Later",
            DiscordDisplayNameSource.UiAutomationGuildNickname,
            1d,
            SalesTestFactory.Epoch));
        Assert.True(engine.RefreshDisplayNames());
        Assert.Equal("Exact Later", engine.Current.CurrentSeller!.DisplayName);
    }

    [Fact]
    public void Test51_ResolverUpdate_DoesNotChangeQueueOrder()
    {
        var resolver = new GuildDisplayNameResolver(clock: () => SalesTestFactory.Epoch);
        resolver.SetAccountScope("account");
        var engine = SalesTestFactory.Engine(resolver: resolver);
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", "author", 1, nickname: null),
            SalesTestFactory.Message("2", "author", 2, nickname: null),
        });
        var before = engine.Current.ActiveItems.Select(x => x.MessageId).ToArray();
        resolver.Observe(new GuildNicknameObservation(
            "guild", "author", null, "Exact",
            DiscordDisplayNameSource.UiAutomationGuildNickname,
            1d,
            SalesTestFactory.Epoch));
        engine.RefreshDisplayNames();
        Assert.Equal(before, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test52_SameAuthorDifferentGuilds_HasDifferentDisplayNames()
    {
        var resolver = new GuildDisplayNameResolver(clock: () => SalesTestFactory.Epoch);
        resolver.SetAccountScope("account");
        resolver.Observe(new GuildNicknameObservation(
            "guild-a", "author", null, "Alpha",
            DiscordDisplayNameSource.UiAutomationGuildNickname, 1d, SalesTestFactory.Epoch));
        resolver.Observe(new GuildNicknameObservation(
            "guild-b", "author", null, "Beta",
            DiscordDisplayNameSource.UiAutomationGuildNickname, 1d, SalesTestFactory.Epoch));
        var engine = SalesTestFactory.Engine(resolver: resolver);
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", "author", 1, nickname: null, guildId: "guild-a"),
            SalesTestFactory.Message("2", "author", 2, nickname: null, guildId: "guild-b"),
        });
        Assert.Equal(
            new[] { "Alpha", "Beta" },
            engine.Current.ActiveItems.Select(x => x.DisplayName));
    }

    private static SalesStateEngine EngineWith(int count)
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(Enumerable.Range(1, count).Select(id =>
            SalesTestFactory.Message(
                id.ToString(),
                $"author-{id}",
                seconds: id)));
        return engine;
    }

    private sealed class RecordingResolver : IGuildDisplayNameResolver
    {
        private readonly GuildDisplayNameResolver _inner = new(clock: () => SalesTestFactory.Epoch);

        public int ResolveCalls { get; private set; }

        public void SetAccountScope(string accountUserId) =>
            _inner.SetAccountScope(accountUserId);

        public GuildDisplayNameResolution Resolve(GuildDisplayNameRequest request)
        {
            ResolveCalls++;
            return _inner.Resolve(request);
        }

        public GuildDisplayNameResolution? Observe(GuildNicknameObservation observation) =>
            _inner.Observe(observation);
    }
}
