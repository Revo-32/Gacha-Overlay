using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Discord.Messages;

public sealed class GuildDisplayNamePersistenceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-31T02:00:00Z");

    [Fact]
    public void Cache_IsBoundedAndEvictsOldestObservation()
    {
        var resolver = new GuildDisplayNameResolver(maximumEntries: 2, clock: () => Now);
        resolver.SetAccountScope("account");
        Observe(resolver, "one", "One", Now);
        Observe(resolver, "two", "Two", Now.AddSeconds(1));
        Observe(resolver, "three", "Three", Now.AddSeconds(2));

        Assert.Equal("Global", Resolve(resolver, "one").DisplayName);
        Assert.Equal("Two", Resolve(resolver, "two").DisplayName);
        Assert.Equal("Three", Resolve(resolver, "three").DisplayName);
    }

    [Fact]
    public void Cache_IsIsolatedByAuthenticatedAccount()
    {
        var store = new InMemoryGuildDisplayNameCacheStore();
        var resolver = new GuildDisplayNameResolver(store, clock: () => Now);
        resolver.SetAccountScope("account-a");
        Observe(resolver, "author", "Alpha", Now);

        resolver.SetAccountScope("account-b");
        Assert.Equal("Global", Resolve(resolver, "author").DisplayName);
        Observe(resolver, "author", "Beta", Now);

        resolver.SetAccountScope("account-a");
        Assert.Equal("Alpha", Resolve(resolver, "author").DisplayName);
    }

    [Fact]
    public void JsonCache_IsVersionedAndPersistsExactStringAtomically()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "guild-display-names.json");
        var store = new JsonGuildDisplayNameCacheStore(path, NullAppLogger.Instance);
        var expected = new GuildDisplayNameCacheDocument(
            GuildDisplayNameCacheDocument.CurrentVersion,
            "account",
            new[]
            {
                new GuildDisplayNameCacheEntry(
                    "guild",
                    "author",
                    "-The First Star-",
                    DiscordDisplayNameSource.GuildNickname,
                    1d,
                    Now,
                    7),
            });

        store.Save(expected);
        var loaded = store.Load("account");

        Assert.Equal(GuildDisplayNameCacheDocument.CurrentVersion, loaded.Version);
        Assert.Equal("account", loaded.AccountUserId);
        Assert.Equal("-The First Star-", Assert.Single(loaded.Entries).DisplayName);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private static void Observe(
        IGuildDisplayNameResolver resolver,
        string authorId,
        string nickname,
        DateTimeOffset observedAt) => Assert.NotNull(resolver.Observe(
        new GuildNicknameObservation(
            "guild",
            authorId,
            null,
            nickname,
            DiscordDisplayNameSource.GuildNickname,
            1d,
            observedAt)));

    private static GuildDisplayNameResolution Resolve(
        IGuildDisplayNameResolver resolver,
        string authorId) => resolver.Resolve(new GuildDisplayNameRequest(
        "guild",
        authorId,
        null,
        "Global",
        "user"));
}
