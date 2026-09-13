using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed class CoreProtocolM2Tests
{
    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/icon.gif", 1)]
    [InlineData("앞 https://cdn.discordapp.com/attachments/1/2/icon.gif, 뒤 https://example.com/guide", 1)]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/icon.gif https://cdn.discordapp.com/attachments/1/2/icon.gif", 2)]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/second.png", 0)]
    [InlineData("https://example.com/guide", 0)]
    public void OnlyExactPrimaryPreviewSourceIsAssociatedAndOriginalTextRemainsLossless(string text,int associated)
    {
        var message=Message("1","22",text) with {
            Attachments=new[] {
                new DiscordAttachmentMetadata("a","icon.gif","https://cdn.discordapp.com/attachments/1/2/icon.gif",null,2000,100,100,"image/gif"),
                new DiscordAttachmentMetadata("b","second.png","https://cdn.discordapp.com/attachments/1/2/second.png",null,2000,100,100,"image/png")
            }
        };
        var result=new CoreSemanticProjection(new OpaqueMedia()).Capture("g",1,"77",new[] {message},SalesQueueSnapshot.Empty,Array.Empty<HostPresenceSnapshot>()).Chat.Single();
        Assert.Equal(text,string.Concat(result.Runs.Select(run=>run.Text)));
        Assert.Equal(associated,result.Runs.Count(run=>run.Kind=="MediaSource"));
        Assert.All(result.Runs.Where(run=>run.Kind=="MediaSource"),run=>Assert.Equal(result.Media[0].Id,run.MediaId));
    }

    [Fact]
    public void PresentationIdentityTracksViewerAndRichMetadataWithoutCrossViewerReuse()
    {
        var source = Message("1", "22", "원문 <@77>");
        var projection = new CoreSemanticProjection(new OpaqueMedia());
        CoreRenderMessage Capture(NormalizedDiscordMessage message, string viewer) =>
            projection.Capture("g", 1, viewer, new[] { message }, SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>()).Chat.Single();
        var mine = Capture(source, "77");
        Assert.Equal(mine.PresentationHash, Capture(source, "77").PresentationHash);
        Assert.Equal("DirectSelfMention", mine.Attention);
        Assert.NotEqual(mine.PresentationHash, Capture(source, "88").PresentationHash);
        Assert.NotEqual(mine.PresentationHash, Capture(source with { Content = "변경된 내용" }, "77").PresentationHash);
        var ordinary = source with { Mentions = Array.Empty<DiscordMention>(), Reactions = new[] { new DiscordMessageReaction(new DiscordCustomEmoji("", "👍", false), 3) } };
        Assert.Equal("Normal", Capture(ordinary, "77").Attention);
        Assert.Equal("Text", Capture(ordinary, "77").Reactions.Single().Emoji.Kind);
    }

    [Fact]
    public void NonImageAttachmentsEmbedAndPollRemainVisibleAsSemanticDetails()
    {
        var message = Message("1", "22", "본문") with
        {
            Mentions = Array.Empty<DiscordMention>(),
            Attachments = new[] { new DiscordAttachmentMetadata("a", "guide.txt", "https://cdn.discordapp.com/a.txt", null, 20, null, null, "text/plain") },
            Embeds = new[] { new DiscordEmbedMetadata("rich", null, "임베드 제목", null, null, "설명") },
            RemoteMetadata = new DiscordRemoteMessageMetadata("default", 0, 0, false, false, false, false, false, null,
                Array.Empty<DiscordForwardSnapshotMetadata>(), Array.Empty<DiscordComponentMetadata>(),
                new DiscordPollMetadata("질문", new[] { new DiscordPollAnswerMetadata(1, "답변", null, null, null) }, DateTimeOffset.UtcNow, false, "default", null))
        };
        var result = new CoreSemanticProjection(new OpaqueMedia()).Capture("g", 1, "77", new[] { message }, SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>()).Chat.Single();
        Assert.Contains(result.Details!, detail => detail.Kind == "attachment" && detail.Text == "guide.txt");
        Assert.Contains(result.Details!, detail => detail.Kind == "embed" && detail.Text == "임베드 제목");
        Assert.Contains(result.Details!, detail => detail.Kind == "poll" && detail.Text.Contains("질문 · 답변", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectionUsesExistingChatGroupingAndPerViewerMentions()
    {
        var messages = new[] { Message("1", "22", "안녕 <@77> <:test:12345>"), Message("2", "22", "같은 작성자"), Message("3", "33", "다른 작성자") };
        var projection = new CoreSemanticProjection(new OpaqueMedia());
        var mine = projection.Capture("gen", 1, "77", messages, SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
        var other = projection.Capture("gen", 1, "88", messages, SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
        Assert.Equal(new[] { true, false, true }, mine.Chat.Select(message => message.ShowAuthorHeader));
        Assert.True(mine.Chat[0].HasSelfMention);
        Assert.False(other.Chat[0].HasSelfMention);
        Assert.Single(mine.Chat[0].Runs, run => run.IsSelf);
        Assert.DoesNotContain(other.Chat[0].Runs, run => run.IsSelf);
        Assert.NotNull(mine.Chat[0].Runs.Single(run => run.Kind == "CustomEmoji").MediaId);
    }

    [Fact]
    public void SalesUsesCanonicalQueueAndDoesNotReuseAnotherViewersPersonalFlags()
    {
        var first = new SalesQueueEntry("sale1", "guild", "77", DateTimeOffset.UtcNow, "판매자",
            DiscordDisplayNameSource.GuildNickname, true, null, SaleObservationTrust.Trusted,
            DetailSource: "상세 <:test:12345>");
        var queue = SalesQueueSnapshot.Empty with
        {
            ActiveItems = new[] { first },
            CurrentSeller = first,
            ActiveCount = 1,
            CurrentSellerIsSelf = true,
            AuthenticatedUserId = "77",
            ObservationStatus = SalesObservationStatus.Live
        };
        var projection = new CoreSemanticProjection(new OpaqueMedia());
        var mine = projection.Capture("g", 1, "77", Array.Empty<NormalizedDiscordMessage>(), queue, Array.Empty<HostPresenceSnapshot>());
        var other = projection.Capture("g", 1, "88", Array.Empty<NormalizedDiscordMessage>(), queue, Array.Empty<HostPresenceSnapshot>());
        Assert.True(mine.Sales.CurrentIsSelf); Assert.False(other.Sales.CurrentIsSelf);
        Assert.Equal("sale1", other.Sales.CurrentMessageId);
        Assert.Equal("Live", other.Sales.ObservationStatus);
        Assert.Contains(other.Sales.Queue.Single().DetailRuns, run => run.Kind == "CustomEmoji" && run.MediaId is not null);
    }

    [Fact]
    public void RolesReactionsRepliesMediaAndSessionUnknownSurviveProjection()
    {
        var message = Message("1", "22", "사진") with
        {
            AuthorStyle = new DiscordAuthorStyle("r", 0x123456, "r", new DiscordRoleIcon("unicode", "★")),
            Reactions = new[] { new DiscordMessageReaction(new DiscordCustomEmoji("12345", "emoji", true), 3) },
            Attachments = new[] { new DiscordAttachmentMetadata("a", "photo.png", "https://cdn.discordapp.com/a.png", null, 2000, 1280, 720, "image/png") },
            RemoteMetadata = new DiscordRemoteMessageMetadata("default", 0, 0, false, false, false, false, false,
                new DiscordReplyMetadata("reply", "g", "c", "old") { ResolvedAuthorName = "답장 대상", ResolvedContent = "원문 <@77>" },
                Array.Empty<DiscordForwardSnapshotMetadata>(), Array.Empty<DiscordComponentMetadata>(), null)
        };
        var session = new HostPresenceSnapshot(1, HostPresenceState.AwaitingPresence, null, null, DateTimeOffset.UtcNow);
        var projected = new CoreSemanticProjection(new OpaqueMedia()).Capture("g", 1, "77", new[] { message }, SalesQueueSnapshot.Empty, new[] { session });
        Assert.Equal(0x123456U, projected.Chat[0].Author.Color);
        Assert.Equal("★", projected.Chat[0].Author.IconUnicode);
        Assert.Equal(3, projected.Chat[0].Reactions.Single().Count);
        Assert.Equal("resolved", projected.Chat[0].Reply!.Status);
        Assert.Contains(projected.Chat[0].Reply!.Runs, run => run.IsSelf);
        Assert.Equal(1280, projected.Chat[0].Media.Single().Width);
        Assert.Null(projected.Session.Single().CurrentPlayers);
        Assert.DoesNotContain("https://", JsonSerializer.Serialize(projected, OverlayProtocolJson.Options));
    }

    [Fact]
    public void LargeKoreanMessageIsLosslesslyPagedBelowFullSixteenKiBLimit()
    {
        var text = string.Concat(Enumerable.Repeat("한글🙂원문 ", 5000));
        var snapshot = new CoreSemanticProjection(new OpaqueMedia()).Capture("generation", long.MaxValue - 1,
            "18446744073709551614", new[] { Message("18446744073709551613", "22", text) },
            SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
        var frames = CoreSnapshotWire.Encode(snapshot);
        Assert.True(frames.Count > 1);
        Assert.All(frames, frame => Assert.InRange(frame.Length, 1, 16 * 1024));
        var chunks = frames.Select(frame => JsonSerializer.Deserialize<CoreSnapshotChunk>(frame, OverlayProtocolJson.Options)!).ToArray();
        Assert.Equal(Enumerable.Range(0, frames.Count), chunks.Select(chunk => chunk.Index));
        var data = chunks.SelectMany(chunk => Convert.FromBase64String(chunk.PayloadBase64)).ToArray();
        Assert.Equal(chunks[0].TotalBytes, data.Length);
        Assert.Equal(chunks[0].Sha256, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        var decoded = JsonSerializer.Deserialize<CoreSnapshot>(data, OverlayProtocolJson.Options)!;
        Assert.Equal(text, string.Concat(decoded.Chat.Single().Runs.Select(run => run.Text)));
        Assert.Equal(snapshot.Revision, decoded.Revision);
        Assert.Equal(snapshot.SelfUserId, decoded.SelfUserId);
        Assert.Equal(16 * 1024, OverlayTransportProtocol.MaximumInboundWebSocketBytes);
    }

    [Fact]
    public void OversizedSnapshotFailsExplicitlyInsteadOfDroppingContent()
    {
        var snapshot = new CoreSemanticProjection(new OpaqueMedia()).Capture("g", 1, "77",
            new[] { Message("1", "22", new string('가', 300000)) }, SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
        Assert.Throws<InvalidDataException>(() => CoreSnapshotWire.Encode(snapshot));
    }

    [Theory]
    [InlineData("core_session_start_v1", "core_render_v1", null, null, true)]
    [InlineData("session_start_v1", "core_render_v1", null, null, false)]
    [InlineData("core_session_start_v1", "gta_companion_v1", null, null, false)]
    [InlineData("core_session_start_v1", "core_render_v1", "g", -1L, false)]
    [InlineData("core_session_start_v1", "core_render_v1", "g", null, false)]
    public void CapabilityAndResumeAreExplicit(string type, string capability, string? generation, long? revision, bool valid)
    {
        var hello = new CoreSessionStart(1, type, new[] { capability }, generation, revision);
        if (valid) CoreSnapshotWire.ValidateHello(hello);
        else Assert.Throws<InvalidDataException>(() => CoreSnapshotWire.ValidateHello(hello));
    }

    [Fact]
    public void ResumeOnlySkipsBootstrapForExactGenerationAndRevision()
    {
        var snapshot = new CoreSemanticProjection(new OpaqueMedia()).Capture("g", 12, "77", Array.Empty<NormalizedDiscordMessage>(), SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
        var hello = new CoreSessionStart(1, CoreClientProtocol.SessionStart, new[] { CoreClientProtocol.Capability }, "g", 12);
        Assert.True(CoreSnapshotWire.CanResume(hello, snapshot));
        Assert.False(CoreSnapshotWire.CanResume(hello with { AfterRevision = 11 }, snapshot));
        Assert.False(CoreSnapshotWire.CanResume(hello with { AfterRevision = 13 }, snapshot));
        Assert.False(CoreSnapshotWire.CanResume(hello with { Generation = "restart" }, snapshot));
    }

    [Fact]
    public void AuthContractAndLegacyFullShapesAreNotExtendedByCore()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DiscordWebAuthStartRequest>(
            "{\"protocolVersion\":1,\"clientInstallationId\":\"00000000-0000-0000-0000-000000000001\",\"clientType\":\"Core\"}", OverlayProtocolJson.Options));
        var legacy = JsonSerializer.Serialize(new BootstrapResponse(1, "g", 1, 77, Array.Empty<HostPresenceSnapshot>()), OverlayProtocolJson.Options);
        Assert.DoesNotContain("core", legacy, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CoreSessionStart>(
            "{\"protocolVersion\":1,\"type\":\"core_session_start_v1\",\"capabilities\":[\"core_render_v1\"],\"selfUserId\":\"spoof\"}", OverlayProtocolJson.Options));
    }

    private static NormalizedDiscordMessage Message(string id, string author, string content) => new(id, "channel", author,
        "name", "표시 이름", content, DateTimeOffset.UtcNow, null, Array.Empty<DiscordCustomEmoji>(),
        Array.Empty<DiscordAttachmentMetadata>(), Array.Empty<DiscordEmbedMetadata>(), new[] { new DiscordMention("77", "나") });

    private sealed class OpaqueMedia : ICoreMediaReferences
    {
        public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "|" + kind + "|" + identity))).ToLowerInvariant();
    }
}
