using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Messages;

public sealed class M45GuildDisplayNameTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-31T01:00:00Z");
    private static readonly DiscordTargetChannels Targets = new(
        "guild", "Guild", "main", "Main", "sales", "Sales");

    [Fact]
    public void Test01_GuildNicknameFieldPresent_IsRpcGuildNickname()
    {
        var patch = Normalize("""
            { "data": { "messages": [{
              "id": "1", "nick": "Spicai",
              "author": { "id": "author", "username": "account", "global_name": "R&J" }
            }] } }
            """);

        Assert.Equal("Spicai", patch.AuthorGuildNickname.Value);
        Assert.Equal(
            DiscordDisplayNameSource.RpcGuildNickname,
            patch.AuthorDisplayNameSource.Value);
    }

    [Fact]
    public void Test02_GuildNicknameAbsent_GlobalNamePresent_IsGlobalFallback()
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", null, "R&J", "account"));

        Assert.Equal("R&J", resolution.DisplayName);
        Assert.Equal(DiscordDisplayNameSource.GlobalDisplayName, resolution.Source);
    }

    [Fact]
    public void Test03_GuildAndGlobalNamesAbsent_IsUsernameFallback()
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", null, null, "account"));

        Assert.Equal("account", resolution.DisplayName);
        Assert.Equal(DiscordDisplayNameSource.Username, resolution.Source);
    }

    [Fact]
    public void Test04_FallbackResult_IsNotMarkedExactGuildNickname()
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", null, "Global", "account"));

        Assert.False(resolution.IsExactGuildNickname);
        Assert.Equal("GuildNicknameUnavailable", resolution.FallbackReason);
    }

    [Theory]
    [InlineData("Spicai")]
    [InlineData("SOFT_N_WET")]
    [InlineData("araico")]
    [InlineData("DE-SSANTA")]
    [InlineData("-The First Star-")]
    public void Tests05To09_ExactGuildNickname_IsPreservedByteForByte(string nickname)
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", nickname, "Wrong Global", "account"));

        Assert.Equal(nickname, resolution.DisplayName);
        Assert.True(resolution.IsExactGuildNickname);
    }

    [Fact]
    public void Test10_CacheKey_ContainsGuildIdAndAuthorId()
    {
        var resolver = Resolver();
        Observe(resolver, "guild-a", "author", "Alpha");

        Assert.Equal(
            "Alpha",
            resolver.Resolve(new GuildDisplayNameRequest(
                "guild-a", "author", null, "Global", "user")).DisplayName);
        Assert.Equal(
            "Global",
            resolver.Resolve(new GuildDisplayNameRequest(
                "guild-a", "other", null, "Global", "user")).DisplayName);
    }

    [Fact]
    public void Test11_SameAuthorInDifferentGuilds_HasDifferentNicknames()
    {
        var resolver = Resolver();
        Observe(resolver, "guild-a", "author", "Alpha");
        Observe(resolver, "guild-b", "author", "Beta");

        Assert.Equal("Alpha", ResolveCached(resolver, "guild-a", "author").DisplayName);
        Assert.Equal("Beta", ResolveCached(resolver, "guild-b", "author").DisplayName);
    }

    [Fact]
    public void Test12_VerifiedGuildNickname_OverridesGlobalName()
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", "Exact", "Global", "account"));

        Assert.Equal("Exact", resolution.DisplayName);
        Assert.Equal(DiscordDisplayNameSource.RpcGuildNickname, resolution.Source);
    }

    [Fact]
    public void Test13_GlobalName_NeverOverwritesVerifiedGuildNickname()
    {
        var resolver = Resolver();
        Observe(resolver, "guild", "author", "Exact");

        var resolution = resolver.Resolve(new GuildDisplayNameRequest(
            "guild", "author", null, "New Global", "account"));

        Assert.Equal("Exact", resolution.DisplayName);
        Assert.Equal(DiscordDisplayNameSource.CachedGuildNickname, resolution.Source);
    }

    [Fact]
    public void Test14_NewVerifiedGuildNickname_UpdatesCache()
    {
        var resolver = Resolver();
        Observe(resolver, "guild", "author", "Old");
        Observe(resolver, "guild", "author", "New");

        Assert.Equal("New", ResolveCached(resolver, "guild", "author").DisplayName);
    }

    [Fact]
    public void Test15_UpdateWithoutNickname_RetainsVerifiedNickname()
    {
        var pipeline = StartedPipeline();
        pipeline.CompleteBootstrap(1, new[] { Patch("1", "author", "Old") }, []);
        pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Update(Patch("1", "author", null, "New Global")));

        Assert.Equal("Old", Assert.Single(pipeline.Current.MainChat).AuthorGuildNickname);
    }

    [Fact]
    public void Test16_Resolver_ReturnsSourceConfidenceAndExactness()
    {
        var resolution = Resolver().Resolve(new GuildDisplayNameRequest(
            "guild", "author", "Exact", "Global", "account"));

        Assert.Equal(DiscordDisplayNameSource.RpcGuildNickname, resolution.Source);
        Assert.Equal(DiscordDisplayNameSource.RpcGuildNickname, resolution.ObservationSource);
        Assert.Equal(1d, resolution.Confidence);
        Assert.True(resolution.IsExactGuildNickname);
        Assert.NotNull(resolution.ObservedAt);
        Assert.True(resolution.Revision > 0);
    }

    [Fact]
    public void Test17_ManualOverride_UsesAuthorIdKey()
    {
        var resolver = Resolver();
        Observe(
            resolver,
            "guild",
            "author-a",
            "Manual Exact",
            DiscordDisplayNameSource.ManualOverride);

        Assert.Equal(
            "Manual Exact",
            ResolveCached(resolver, "guild", "author-a").DisplayName);
        Assert.Equal(
            "Global",
            resolver.Resolve(new GuildDisplayNameRequest(
                "guild", "author-b", null, "Global", "user")).DisplayName);
    }

    [Fact]
    public void Test18_InvalidEmptyOverride_DoesNotCorruptCache()
    {
        var resolver = Resolver();
        Observe(resolver, "guild", "author", "Valid");
        var invalid = resolver.Observe(new GuildNicknameObservation(
            "guild",
            "author",
            null,
            "   ",
            DiscordDisplayNameSource.ManualOverride,
            1d,
            Now.AddMinutes(1)));

        Assert.Null(invalid);
        Assert.Equal("Valid", ResolveCached(resolver, "guild", "author").DisplayName);
    }

    [Fact]
    public void Test19_NewExactResolution_RefreshesRetainedChatItems()
    {
        var pipeline = StartedPipeline();
        pipeline.CompleteBootstrap(
            1,
            new[] { Patch("1", "author", null, "Global") },
            []);

        Assert.True(pipeline.ObserveGuildNickname(new GuildNicknameObservation(
            "guild",
            "author",
            "1",
            "UIA Exact",
            DiscordDisplayNameSource.UiAutomationGuildNickname,
            0.9d,
            Now)));

        var message = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal("UIA Exact", message.AuthorGuildNickname);
        Assert.Equal(
            DiscordDisplayNameSource.UiAutomationGuildNickname,
            message.AuthorGuildNicknameObservationSource);
    }

    [Fact]
    public void Test20_ChatOrdering_RemainsUnchangedAfterRefresh()
    {
        var pipeline = StartedPipeline();
        pipeline.CompleteBootstrap(
            1,
            new[]
            {
                Patch("1", "author", null, "Global"),
                Patch("2", "author", null, "Global"),
            },
            []);
        var before = pipeline.Current.MainChat.Select(message => message.MessageId).ToArray();

        pipeline.ObserveGuildNickname(new GuildNicknameObservation(
            "guild", "author", "2", "Exact", DiscordDisplayNameSource.UiAutomationGuildNickname,
            1d, Now));

        Assert.Equal(before, pipeline.Current.MainChat.Select(message => message.MessageId));
    }

    [Fact]
    public void Test21_DisplayNameChange_DoesNotAffectUserIdIdentity()
    {
        var pipeline = StartedPipeline();
        pipeline.CompleteBootstrap(1, new[] { Patch("1", "stable-id", "Old") }, []);
        pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Update(Patch("1", "stable-id", "New")));

        var message = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal("stable-id", message.AuthorId);
        Assert.Equal("New", message.AuthorGuildNickname);
    }

    [Fact]
    public void Test22_SameDisplayNameWithDifferentUserIds_RemainsDistinct()
    {
        var pipeline = StartedPipeline();
        pipeline.CompleteBootstrap(
            1,
            new[]
            {
                Patch("1", "author-a", "Shared"),
                Patch("2", "author-b", "Shared"),
            },
            []);

        Assert.Equal(
            new[] { "author-a", "author-b" },
            pipeline.Current.MainChat.Select(message => message.AuthorId));
    }

    private static GuildDisplayNameResolver Resolver()
    {
        var resolver = new GuildDisplayNameResolver(clock: () => Now);
        resolver.SetAccountScope("account");
        return resolver;
    }

    private static void Observe(
        IGuildDisplayNameResolver resolver,
        string guildId,
        string authorId,
        string nickname,
        DiscordDisplayNameSource source = DiscordDisplayNameSource.RpcGuildNickname)
    {
        Assert.NotNull(resolver.Observe(new GuildNicknameObservation(
            guildId,
            authorId,
            null,
            nickname,
            source,
            1d,
            Now)));
    }

    private static GuildDisplayNameResolution ResolveCached(
        IGuildDisplayNameResolver resolver,
        string guildId,
        string authorId) => resolver.Resolve(new GuildDisplayNameRequest(
        guildId,
        authorId,
        null,
        "Global",
        "user"));

    private static DiscordMessagePipeline StartedPipeline()
    {
        var pipeline = new DiscordMessagePipeline();
        pipeline.SetAuthenticatedUser("account");
        Assert.True(pipeline.StartBootstrap(1, Targets));
        return pipeline;
    }

    private static DiscordMessagePatch Patch(
        string messageId,
        string authorId,
        string? nickname,
        string globalName = "Global") => new(messageId)
        {
            ChannelId = OptionalValue<string>.From("main"),
            AuthorId = OptionalValue<string>.From(authorId),
            AuthorUsername = OptionalValue<string>.From("account"),
            AuthorDisplayName = OptionalValue<string?>.From(globalName),
            AuthorGuildNickname = nickname is null
            ? default
            : OptionalValue<string?>.From(nickname),
            AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
            nickname is null
                ? DiscordDisplayNameSource.GlobalDisplayName
                : DiscordDisplayNameSource.RpcGuildNickname),
            AuthorGuildNicknameObservationSource = nickname is null
            ? default
            : OptionalValue<DiscordDisplayNameSource>.From(
                DiscordDisplayNameSource.RpcGuildNickname),
            Content = OptionalValue<string>.From("message"),
            CreatedAt = OptionalValue<DateTimeOffset?>.From(
            Now.AddSeconds(long.Parse(messageId))),
        };

    private static DiscordMessagePatch Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var normalizer = new DiscordMessageNormalizer(NullAppLogger.Instance);
        return Assert.Single(normalizer.NormalizeSnapshot(
            document.RootElement,
            "main",
            "guild"));
    }
}
