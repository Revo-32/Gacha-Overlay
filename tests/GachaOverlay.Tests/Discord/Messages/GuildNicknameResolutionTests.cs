using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Discord.Messages;

public sealed class GuildNicknameResolutionTests
{
    private static readonly DiscordTargetChannels Targets = new(
        "guild-a", "Guild", "main", "Main", "sales", "Sales");

    [Fact]
    public void CacheKey_IncludesGuildAndAuthor()
    {
        var cache = new GuildNicknameCache();
        cache.Set("guild-a", "author", "Alpha");
        cache.Set("guild-b", "author", "Beta");

        Assert.True(cache.TryGet("guild-a", "author", out var alpha));
        Assert.True(cache.TryGet("guild-b", "author", out var beta));
        Assert.Equal("Alpha", alpha);
        Assert.Equal("Beta", beta);
    }

    [Fact]
    public void CurrentExactNickname_IsPreferredOverGlobalName()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "DE-SSANTA") }, []);

        var message = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal("DE-SSANTA", message.AuthorGuildNickname);
        Assert.Equal(DiscordDisplayNameSource.GuildNickname, message.AuthorDisplayNameSource);
    }

    [Fact]
    public void CachedNickname_IsPreferredForLaterCreate()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "Exact") }, []);

        pipeline.ReceiveLive(1, DiscordMessageMutation.Create(Fallback(2, "Wrong Global")));

        var message = pipeline.Current.MainChat.Single(item => item.MessageId == "2");
        Assert.Equal("Exact", message.AuthorGuildNickname);
        Assert.Equal(DiscordDisplayNameSource.CachedGuildNickname, message.AuthorDisplayNameSource);
    }

    [Fact]
    public void UpdateWithoutNickname_RetainsTrustedNickname()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "-The First Star-") }, []);

        pipeline.ReceiveLive(1, DiscordMessageMutation.Update(Fallback(1, "H") with
        {
            Content = OptionalValue<string>.From("edited"),
        }));

        var message = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal("-The First Star-", message.AuthorGuildNickname);
        Assert.Equal("edited", message.Content);
    }

    [Fact]
    public void GlobalName_CannotOverwriteVerifiedCache()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "Server-Name") }, []);

        pipeline.ReceiveLive(1, DiscordMessageMutation.Update(Fallback(1, "Global Name")));

        Assert.Equal("Server-Name", Assert.Single(pipeline.Current.MainChat).AuthorGuildNickname);
    }

    [Fact]
    public void NewVerifiedNickname_UpdatesCacheForFutureMessages()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "Old") }, []);
        pipeline.ReceiveLive(1, DiscordMessageMutation.Update(Exact(1, "New")));

        pipeline.ReceiveLive(1, DiscordMessageMutation.Create(Fallback(2, "Global")));

        Assert.All(pipeline.Current.MainChat, message => Assert.Equal("New", message.AuthorGuildNickname));
    }

    [Fact]
    public void NewVerifiedNickname_RefreshesRetainedFallbackMessages()
    {
        var pipeline = Start();

        pipeline.CompleteBootstrap(
            1,
            new[] { Fallback(1, "Global"), Exact(2, "Exact-Now") },
            []);

        var first = pipeline.Current.MainChat.Single(message => message.MessageId == "1");
        Assert.Equal("Exact-Now", first.AuthorGuildNickname);
        Assert.Equal(DiscordDisplayNameSource.CachedGuildNickname, first.AuthorDisplayNameSource);
    }

    [Fact]
    public void ExplicitNullNickname_DoesNotEraseVerifiedCache()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "Old") }, []);
        pipeline.ReceiveLive(1, DiscordMessageMutation.Update(Fallback(1, "Global") with
        {
            AuthorGuildNickname = OptionalValue<string?>.From(null),
        }));

        pipeline.ReceiveLive(1, DiscordMessageMutation.Create(Fallback(2, "Next Global")));

        Assert.Equal(
            "Old",
            pipeline.Current.MainChat.Single(x => x.MessageId == "1").AuthorGuildNickname);
        Assert.Equal(
            "Old",
            pipeline.Current.MainChat.Single(x => x.MessageId == "2").AuthorGuildNickname);
    }

    [Fact]
    public void SalesAndMainStores_ShareGuildScopedNicknameKnowledge()
    {
        var pipeline = Start();

        pipeline.CompleteBootstrap(
            1,
            new[] { Fallback(1, "Global") },
            new[] { Exact(2, "Sales-Exact", "sales") });

        Assert.Equal("Sales-Exact", Assert.Single(pipeline.Current.MainChat).AuthorGuildNickname);
        Assert.Equal("Sales-Exact", Assert.Single(pipeline.Current.SalesSource).AuthorGuildNickname);
    }

    [Fact]
    public void SameNicknameDifferentUserId_DoesNotShareCacheEntry()
    {
        var pipeline = Start();
        pipeline.CompleteBootstrap(1, new[] { Exact(1, "Shared", authorId: "one") }, []);

        pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Create(Fallback(2, "Other Global", authorId: "two")));

        Assert.Null(pipeline.Current.MainChat.Single(x => x.AuthorId == "two").AuthorGuildNickname);
    }

    private static DiscordMessagePipeline Start()
    {
        var pipeline = new DiscordMessagePipeline();
        Assert.True(pipeline.StartBootstrap(1, Targets));
        return pipeline;
    }

    private static DiscordMessagePatch Exact(
        long id,
        string nickname,
        string channel = "main",
        string authorId = "author") =>
        Base(id, channel, authorId) with
        {
            AuthorGuildNickname = OptionalValue<string?>.From(nickname),
            AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
                DiscordDisplayNameSource.GuildNickname),
        };

    private static DiscordMessagePatch Fallback(
        long id,
        string globalName,
        string channel = "main",
        string authorId = "author") =>
        Base(id, channel, authorId) with
        {
            AuthorDisplayName = OptionalValue<string?>.From(globalName),
            AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
                DiscordDisplayNameSource.GlobalDisplayName),
        };

    private static DiscordMessagePatch Base(long id, string channel, string authorId) => new(
        id.ToString())
    {
        ChannelId = OptionalValue<string>.From(channel),
        AuthorId = OptionalValue<string>.From(authorId),
        AuthorUsername = OptionalValue<string>.From("username"),
        Content = OptionalValue<string>.From($"message-{id}"),
        CreatedAt = OptionalValue<DateTimeOffset?>.From(
            DateTimeOffset.Parse("2026-08-30T01:00:00Z").AddSeconds(id)),
    };
}
