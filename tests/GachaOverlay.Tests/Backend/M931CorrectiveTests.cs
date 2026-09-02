using LSOverlay.Backend.Chat;
using LSOverlay.Protocol;
using LSOverlay.TransportProbe;

namespace GachaOverlay.Tests.Backend;

public sealed class M931CorrectiveTests
{
    [Fact]
    public async Task CreateAndCanonicalUpdate_ResolveRealStyleAuthorConsistently()
    {
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.Available,
                "REVO*32"),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source);

        var create = await resolver.ResolveAsync(
            1,
            Observation(123, globalDisplayName: "ItoToko"));
        var update = await resolver.ResolveAsync(
            1,
            Observation(
                123,
                globalDisplayName: "ItoToko",
                exactGuildNickname: "REVO*32"));

        Assert.Equal("REVO*32", create.DisplayName);
        Assert.Equal("REVO*32", update.DisplayName);
        Assert.Equal(create.UserId, update.UserId);
        Assert.Equal(123UL, create.UserId);
    }

    [Fact]
    public async Task CurrentExactGuildNickname_OutranksAndRefreshesCachedNickname()
    {
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.Available,
                "OldNickname"),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source);
        Assert.Equal(
            "OldNickname",
            (await resolver.ResolveAsync(1, Observation(123))).DisplayName);

        var current = await resolver.ResolveAsync(
            1,
            Observation(123, exactGuildNickname: "NewNickname"));
        var cached = await resolver.ResolveAsync(1, Observation(123));

        Assert.Equal("NewNickname", current.DisplayName);
        Assert.Equal("NewNickname", cached.DisplayName);
        Assert.Equal(1, source.Requests);
    }

    [Fact]
    public async Task ExactCurrentGuildNickname_AvoidsTargetedMemberRequest()
    {
        var source = new FakeMemberSource();
        var resolver = new CanonicalRemoteAuthorResolver(source);

        var author = await resolver.ResolveAsync(
            1,
            Observation(123, exactGuildNickname: "REVO*32"));

        Assert.Equal("REVO*32", author.DisplayName);
        Assert.Equal("REVO*32", author.GuildNickname);
        Assert.Equal(0, source.Requests);
    }

    [Fact]
    public async Task MissingGuildNickname_FallsBackToGlobalDisplayThenUsername()
    {
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source);

        var global = await resolver.ResolveAsync(
            1,
            Observation(1, username: "itotoko", globalDisplayName: "Ito Toko"));
        var username = await resolver.ResolveAsync(
            1,
            Observation(2, username: "itotoko"));

        Assert.Equal("Ito Toko", global.DisplayName);
        Assert.Equal("itotoko", username.DisplayName);
        Assert.Null(global.GuildNickname);
        Assert.Null(username.GuildNickname);
    }

    [Fact]
    public async Task MissingAllAuthorText_UsesUnknownWithoutChangingAuthorId()
    {
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source);

        var author = await resolver.ResolveAsync(
            1,
            Observation(123, username: null));

        Assert.Equal(123UL, author.UserId);
        Assert.Equal("Unknown", author.Username);
        Assert.Equal("Unknown", author.DisplayName);
    }

    [Fact]
    public async Task TargetedMemberCache_IsBoundedAndAvoidsRepeatedResolution()
    {
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source);
        await resolver.ResolveAsync(1, Observation(1));
        await resolver.ResolveAsync(1, Observation(1));
        Assert.Equal(1, source.Requests);

        for (ulong authorId = 2;
             authorId <= CanonicalRemoteAuthorResolver.MaximumCacheEntries + 25;
             authorId++)
        {
            await resolver.ResolveAsync(1, Observation(authorId));
        }

        Assert.True(
            resolver.CachedEntryCount <= CanonicalRemoteAuthorResolver.MaximumCacheEntries);
    }

    [Fact]
    public async Task ExpiredMemberCache_RefetchesConservatively()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new FakeMemberSource
        {
            Result = new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.Available,
                "Cached"),
        };
        var resolver = new CanonicalRemoteAuthorResolver(source, () => now);
        await resolver.ResolveAsync(1, Observation(123));
        now += CanonicalRemoteAuthorResolver.CacheLifetime + TimeSpan.FromSeconds(1);

        await resolver.ResolveAsync(1, Observation(123));

        Assert.Equal(2, source.Requests);
    }

    [Fact]
    public async Task UnavailableMemberResolution_UsesShortBackoffInsteadOfPerCreateRest()
    {
        var source = new FakeMemberSource();
        var resolver = new CanonicalRemoteAuthorResolver(source);

        var first = await resolver.ResolveAsync(
            1,
            Observation(123, globalDisplayName: "ItoToko"));
        var second = await resolver.ResolveAsync(
            1,
            Observation(123, globalDisplayName: "ItoToko"));

        Assert.Equal("ItoToko", first.DisplayName);
        Assert.Equal("ItoToko", second.DisplayName);
        Assert.Equal(1, source.Requests);
    }

    [Fact]
    public void ProbeFormatter_ShowsForwardSnapshotTextAndBoundedOutput()
    {
        var forward = Forward(new string('가', 400));

        var output = ChatProbeFormatter.Format(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(forward));

        Assert.Contains("forwards=1", output, StringComparison.Ordinal);
        Assert.Contains("Forward[1]:", output, StringComparison.Ordinal);
        Assert.Contains("text=\"", output, StringComparison.Ordinal);
        Assert.Contains("...", output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('가', 241), output, StringComparison.Ordinal);
        Assert.True(output.Length < 1_500);
    }

    [Fact]
    public void ProbeFormatter_IdentifiesForwardMediaWithoutUrlsOrSecrets()
    {
        const string accessSecret = "access-token-must-not-render";
        const string pairingSecret = "pairing-secret-must-not-render";
        var attachment = new ChatAttachment(
            5,
            "image.png",
            $"https://cdn.example/image.png?Authorization={accessSecret}",
            "https://proxy.example/image.png",
            42,
            "image/png",
            1920,
            1080,
            null,
            null,
            false,
            null,
            pairingSecret,
            false);
        var forward = Forward("forwarded image", new[] { attachment });

        var output = ChatProbeFormatter.Format(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(forward));

        Assert.Contains("Forward[1]:", output, StringComparison.Ordinal);
        Assert.Contains("attachments=1", output, StringComparison.Ordinal);
        Assert.Contains("file=\"image.png\"", output, StringComparison.Ordinal);
        Assert.Contains("type=\"image/png\"", output, StringComparison.Ordinal);
        Assert.Contains("size=42", output, StringComparison.Ordinal);
        Assert.Contains("dimensions=1920x1080", output, StringComparison.Ordinal);
        Assert.Contains("originalAuthor=<unavailable>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", output, StringComparison.Ordinal);
        Assert.DoesNotContain(accessSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(pairingSecret, output, StringComparison.Ordinal);
    }

    private static RemoteAuthorObservation Observation(
        ulong authorId,
        string? username = "itotoko",
        string? globalDisplayName = null,
        string? exactGuildNickname = null) => new(
        authorId,
        username,
        globalDisplayName,
        exactGuildNickname,
        false,
        false);

    private static ChatForwardSnapshot Forward(
        string content,
        IReadOnlyList<ChatAttachment>? attachments = null) => new(
        "Default",
        content,
        DateTimeOffset.UtcNow,
        null,
        attachments ?? Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        Array.Empty<ChatComponent>());

    private static ChatMessage Message(ChatForwardSnapshot forward) => new(
        1,
        1,
        1,
        "Default",
        0,
        new ChatAuthor(123, "itotoko", "REVO*32", "REVO*32", false, false),
        "wrapper",
        DateTimeOffset.UtcNow,
        null,
        false,
        false,
        false,
        0,
        Array.Empty<ChatEmoji>(),
        Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        new[] { forward },
        null,
        Array.Empty<ChatComponent>(),
        null);

    private sealed class FakeMemberSource : IRemoteGuildMemberSource
    {
        private int _requests;

        public RemoteGuildMemberResolution Result { get; set; } = new(
            RemoteGuildMemberResolutionStatus.Unavailable);

        public int Requests => Volatile.Read(ref _requests);

        public Task<RemoteGuildMemberResolution> ResolveAsync(
            ulong guildId,
            ulong authorId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(Result);
        }
    }
}
