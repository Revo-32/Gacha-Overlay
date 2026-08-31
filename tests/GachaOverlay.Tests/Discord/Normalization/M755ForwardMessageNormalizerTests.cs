using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Normalization;

public sealed class M755ForwardMessageNormalizerTests
{
    private readonly DiscordMessageNormalizer _normalizer = new(NullAppLogger.Instance);

    [Fact]
    public void NormalMessage_IsNotForward()
    {
        var message = Normalize("""
            { "id":"wrapper", "author":{"id":"author"}, "content":"normal" }
            """);

        Assert.Null(message.Forward);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    [Fact]
    public void MessageReferenceTypeOne_IsRecognizedAsForward()
    {
        var message = Normalize(Forward("\"content\":\"forwarded\""));

        Assert.Equal(DiscordForwardResolutionMode.FlattenedPayload, message.Forward?.Resolution);
        Assert.Equal("source-message", message.Forward?.SourceKey?.MessageId);
    }

    [Fact]
    public void MessageSnapshots_IsRecognizedWithoutReference()
    {
        var message = Normalize("""
            {
              "id":"wrapper", "author":{"id":"author"}, "content":"",
              "message_snapshots":[{"message":{"content":"snapshot"}}]
            }
            """);

        Assert.Equal(DiscordForwardResolutionMode.Snapshot, message.Forward?.Resolution);
        Assert.Equal("snapshot", message.Content);
    }

    [Fact]
    public void OpaqueEmptyRpcMessage_UsesNeutralFallbackWithoutInventingMetadata()
    {
        var message = Normalize(EmptyWrapper());

        Assert.Empty(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.Message, message.FallbackKind);
        Assert.Null(message.Forward);
    }

    [Fact]
    public void LiveOpaqueMessage_DefersClassificationUntilSnapshotHydration()
    {
        using var document = JsonDocument.Parse("""
            {
              "evt":"MESSAGE_CREATE",
              "data":{
                "channel_id":"main", "guild_id":"guild",
                "message":{
                  "id":"wrapper", "channel_id":"main",
                  "author":{"id":"author"}, "content":"",
                  "attachments":[], "embeds":[], "type":0
                }
              }
            }
            """);

        Assert.True(_normalizer.TryNormalizeDispatch(
            document.RootElement,
            out var mutation,
            out _,
            "guild"));
        Assert.Equal(
            DiscordMessageFallbackKind.PendingHydration,
            mutation?.Patch?.FallbackKind.Value);
        Assert.Null(mutation?.Patch?.Forward.Value);
    }

    [Fact]
    public void ForwardWithoutStickerEvidence_DoesNotCreateStickerFallbackSource()
    {
        var message = Normalize(Forward(string.Empty));

        Assert.Empty(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.ForwardedMessage, message.FallbackKind);
    }

    [Fact]
    public void UnresolvedForwardWithoutSourceIdentity_UsesForwardFallback()
    {
        var message = Normalize("""
            {
              "id":"wrapper", "author":{"id":"author"}, "content":"",
              "attachments":[], "embeds":[],
              "message_reference":{"type":1}
            }
            """);

        Assert.Equal(DiscordForwardResolutionMode.Fallback, message.Forward?.Resolution);
        Assert.Equal(DiscordMessageFallbackKind.ForwardedMessage, message.FallbackKind);
    }

    [Fact]
    public void SnapshotText_IsNormalizedUsingWrapperIdentity()
    {
        var message = Normalize(Snapshot("\"content\":\"hello forward\""));

        Assert.Equal("wrapper", message.MessageId);
        Assert.Equal("hello forward", message.Content);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    [Fact]
    public void SnapshotAttachment_ReusesAttachmentPipelineMetadata()
    {
        var message = Normalize(Snapshot("""
            "content":"",
            "attachments":[{
              "id":"asset", "filename":"forward.png",
              "url":"https://cdn.example/forward.png", "content_type":"image/png"
            }]
            """));

        Assert.Equal("forward.png", Assert.Single(message.Attachments).FileName);
        Assert.Equal(DiscordForwardResolutionMode.Snapshot, message.Forward?.Resolution);
    }

    [Fact]
    public void SnapshotEmbed_ReusesEmbedPipelineMetadata()
    {
        var message = Normalize(Snapshot("""
            "content":"",
            "embeds":[{"type":"image","image":{"url":"https://cdn.example/embed.png"}}]
            """));

        Assert.Equal("https://cdn.example/embed.png", Assert.Single(message.Embeds).ImageUrl);
    }

    [Fact]
    public void SnapshotCustomEmoji_IsNormalized()
    {
        var message = Normalize(Snapshot("""
            "content":"hello <:wave:900>",
            "content_parsed":[{"type":"customEmoji","id":"900","name":"wave"}]
            """));

        Assert.Equal("900", Assert.Single(message.CustomEmojis).EmojiId);
    }

    [Fact]
    public void SnapshotStickerItems_IsNormalized()
    {
        var message = Normalize(Snapshot("""
            "content":"",
            "sticker_items":[{"id":"900","name":"Wave","format_type":2}]
            """));

        Assert.Equal("900", Assert.Single(message.Stickers).StickerId);
        Assert.True(message.Forward?.HasStickerEvidence);
    }

    [Fact]
    public void SnapshotLegacyStickers_IsNormalized()
    {
        var message = Normalize(Snapshot("""
            "content":"",
            "stickers":[{"id":"901","name":"Legacy","format_type":1}]
            """));

        Assert.Equal("901", Assert.Single(message.Stickers).StickerId);
    }

    [Fact]
    public void SnapshotSnakeCaseEnvelope_IsSupported()
    {
        var message = Normalize(Snapshot("\"content\":\"snake\""));

        Assert.Equal("snake", message.Content);
    }

    [Fact]
    public void SnapshotCamelCaseEnvelope_IsSupported()
    {
        var message = Normalize("""
            {
              "id":"wrapper", "author":{"id":"author"}, "content":"",
              "messageReference":{
                "type":"FORWARD", "guildId":"guild",
                "channelId":"source-channel", "messageId":"source-message"
              },
              "messageSnapshots":[{"snapshotMessage":{"content":"camel"}}]
            }
            """);

        Assert.Equal("camel", message.Content);
        Assert.Equal("source-message", message.Forward?.SourceKey?.MessageId);
    }

    [Fact]
    public void SourceMessageId_DoesNotReplaceWrapperIdentityOrCreateDuplicate()
    {
        var store = new DiscordMessageStore();
        var patch = NormalizePatch(Snapshot("\"content\":\"single record\""));
        store.Apply(DiscordMessageMutation.Create(patch));

        var message = Assert.Single(store.GetOrderedSnapshot());
        Assert.Equal("wrapper", message.MessageId);
        Assert.DoesNotContain(store.GetOrderedSnapshot(), item => item.MessageId == "source-message");
    }

    [Fact]
    public void OpaqueNeutralFallback_DiffersFromPositiveStickerEvidence()
    {
        var empty = Normalize(EmptyWrapper());
        var sticker = Normalize("""
            {
              "id":"sticker", "author":{"id":"author"}, "content":"",
              "attachments":[], "embeds":[],
              "sticker_items":[{"id":"900","name":"Wave","format_type":2}], "type":0
            }
            """);

        Assert.Equal(DiscordMessageFallbackKind.Message, empty.FallbackKind);
        Assert.Equal(DiscordMessageFallbackKind.None, sticker.FallbackKind);
        Assert.Single(sticker.Stickers);
    }

    [Fact]
    public void ForwardStickerPositiveEvidence_UsesStickerPipeline()
    {
        var message = Normalize(Snapshot("""
            "content":"", "sticker_items":[{"id":"900","name":"Wave","format_type":2}]
            """));

        Assert.Single(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    [Fact]
    public void ForwardStickerEvidenceWithoutMetadata_CreatesConfirmedStickerFallbackOnly()
    {
        var message = Normalize(Snapshot("\"content\":\"\", \"sticker_items\":[{}]"));

        var sticker = Assert.Single(message.Stickers);
        Assert.Equal(string.Empty, sticker.StickerId);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    [Fact]
    public void UnknownForwardContent_UsesForwardFallbackRatherThanStickerFallback()
    {
        var message = Normalize(Forward(string.Empty));

        Assert.Empty(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.ForwardedMessage, message.FallbackKind);
    }

    [Fact]
    public void InsufficientSnapshotWithSourceIdentity_RequestsOnDemandLookup()
    {
        var message = Normalize(Snapshot(string.Empty));

        Assert.Equal(DiscordForwardResolutionMode.LookupPending, message.Forward?.Resolution);
        Assert.True(message.Forward?.RequiresLookup);
    }

    [Fact]
    public void ActualLocalRpcFlattenedText_RemainsNormalPresentationContent()
    {
        var message = Normalize("""
            {
              "id":"wrapper", "author":{"id":"forwarder"},
              "content":"forward text", "content_parsed":["forward text"],
              "attachments":[], "embeds":[], "type":0
            }
            """);

        Assert.Equal("forward text", message.Content);
        Assert.Empty(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    [Fact]
    public void ActualLocalRpcFlattenedImage_RemainsExistingAttachmentContent()
    {
        var message = Normalize("""
            {
              "id":"wrapper", "author":{"id":"forwarder"}, "content":"",
              "attachments":[{"id":"1","url":"https://cdn.example/image.png","content_type":"image/png"}],
              "embeds":[], "type":0
            }
            """);

        Assert.Single(message.Attachments);
        Assert.Equal(DiscordMessageFallbackKind.None, message.FallbackKind);
    }

    private NormalizedDiscordMessage Normalize(string messageJson)
    {
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(NormalizePatch(messageJson)));
        return Assert.Single(store.GetOrderedSnapshot());
    }

    private DiscordMessagePatch NormalizePatch(string messageJson)
    {
        using var document = JsonDocument.Parse(
            $$"""{ "data": { "messages": [{{messageJson}}] } }""");
        return Assert.Single(_normalizer.NormalizeSnapshot(
            document.RootElement.Clone(),
            "main",
            "guild"));
    }

    private static string EmptyWrapper() => """
        {
          "id":"wrapper", "author":{"id":"author"}, "content":"",
          "attachments":[], "embeds":[], "type":0
        }
        """;

    private static string Forward(string fields) => $$"""
        {
          "id":"wrapper", "author":{"id":"forwarder"},
          "content":"", "attachments":[], "embeds":[],
          "message_reference":{
            "type":1, "guild_id":"guild",
            "channel_id":"source-channel", "message_id":"source-message"
          }
          {{(string.IsNullOrWhiteSpace(fields) ? string.Empty : "," + fields)}}
        }
        """;

    private static string Snapshot(string snapshotFields) => $$$"""
        {
          "id":"wrapper", "author":{"id":"forwarder"},
          "content":"", "attachments":[], "embeds":[],
          "message_reference":{
            "type":1, "guild_id":"guild",
            "channel_id":"source-channel", "message_id":"source-message"
          },
          "message_snapshots":[{"message":{ {{{snapshotFields}}} }}]
        }
        """;
}
