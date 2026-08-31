using System.Text.Json;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Chat;

public sealed class M755ForwardMediaPolicyTests
{
    [Fact]
    public void ForwardImage_UsesExistingMediaPipeline()
    {
        var presentation = Project("""
            "content":"",
            "attachments":[{
              "id":"1", "url":"https://cdn.example/image.png", "content_type":"image/png"
            }]
            """);

        Assert.Equal("https://cdn.example/image.png", Assert.Single(presentation.Media).Url);
    }

    [Fact]
    public void ForwardEmbed_UsesExistingMediaPipeline()
    {
        var presentation = Project("""
            "content":"",
            "embeds":[{
              "url":"https://tenor.com/view/source",
              "image":{"url":"https://media.tenor.com/asset.gif"}
            }]
            """);

        var media = Assert.Single(presentation.Media);
        Assert.Equal("https://media.tenor.com/asset.gif", media.Url);
        Assert.Equal("https://tenor.com/view/source", media.SourceUrl);
    }

    [Fact]
    public void ForwardSuccessfulPreview_SuppressesOnlyExactSourceUrl()
    {
        var media = new ChatMediaCandidate(
            "https://media.tenor.com/asset.gif",
            "image/gif",
            null,
            null,
            "https://tenor.com/view/source");

        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            "forward https://tenor.com/view/source https://example.com/keep",
            media,
            previewSucceeded: true,
            enabled: true);

        Assert.Equal("forward  https://example.com/keep", result);
    }

    [Fact]
    public void ForwardFailedPreview_KeepsSourceUrl()
    {
        const string content = "forward https://tenor.com/view/source";
        var media = new ChatMediaCandidate(
            "https://media.tenor.com/asset.gif",
            "image/gif",
            null,
            null,
            "https://tenor.com/view/source");

        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            content,
            media,
            previewSucceeded: false,
            enabled: true);

        Assert.Equal(content, result);
    }

    [Fact]
    public void ForwardSupportedSticker_ProjectsActualStickerMetadata()
    {
        var presentation = Project("""
            "content":"",
            "sticker_items":[{"id":"900","name":"Wave","format_type":2}]
            """);

        var sticker = Assert.Single(presentation.Stickers);
        Assert.Equal("900", sticker.StickerId);
        Assert.Equal(2, sticker.FormatType);
    }

    [Fact]
    public void ForwardUnsupportedSticker_RemainsAvailableForFallback()
    {
        var presentation = Project("""
            "content":"",
            "sticker_items":[{"id":"900","name":"Wave","format_type":3}]
            """);

        Assert.Equal(3, Assert.Single(presentation.Stickers).FormatType);
    }

    [Fact]
    public void ForwardMultipleMedia_PreservesAdditionalMediaCount()
    {
        var presentation = Project("""
            "content":"",
            "attachments":[
              {"id":"1","url":"https://cdn.example/one.png","content_type":"image/png"},
              {"id":"2","url":"https://cdn.example/two.png","content_type":"image/png"}
            ]
            """);

        Assert.Equal(2, presentation.Media.Count);
        Assert.Equal(1, presentation.AdditionalMediaCount);
    }

    [Fact]
    public void ForwardTextAndMedia_PreservesTextBeforeMediaPresentation()
    {
        var presentation = Project("""
            "content":"caption",
            "attachments":[{"id":"1","url":"https://cdn.example/image.png","content_type":"image/png"}]
            """);

        Assert.Equal("caption", presentation.PlainText);
        Assert.Single(presentation.Media);
    }

    [Fact]
    public void ForwardCustomEmoji_UsesExistingTokenPipeline()
    {
        var presentation = Project("""
            "content":"<:wave:900>",
            "content_parsed":[{"type":"customEmoji","id":"900","name":"wave"}]
            """);

        var token = Assert.Single(presentation.Tokens);
        Assert.Equal(ChatTokenKind.CustomEmoji, token.Kind);
        Assert.Equal("900", token.Identity);
    }

    [Fact]
    public void ForwardPresentation_AlwaysKeepsWrapperMessageIdentity()
    {
        var presentation = Project("\"content\":\"identity\"");

        Assert.Equal("wrapper", presentation.MessageId);
        Assert.NotEqual("source-message", presentation.MessageId);
    }

    private static ChatMessagePresentation Project(string snapshotFields)
    {
        var normalizer = new DiscordMessageNormalizer(NullAppLogger.Instance);
        using var document = JsonDocument.Parse($$$"""
            {
              "data":{"messages":[{
                "id":"wrapper", "channel_id":"main",
                "author":{"id":"forwarder","username":"forwarder"},
                "content":"",
                "message_reference":{
                  "type":1, "guild_id":"guild",
                  "channel_id":"source", "message_id":"source-message"
                },
                "message_snapshots":[{"message":{ {{{snapshotFields}}} }}]
              }]}
            }
            """);
        var patch = Assert.Single(normalizer.NormalizeSnapshot(document.RootElement, "main", "guild"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        var state = new DiscordMessageState(
            1,
            false,
            store.GetOrderedSnapshot(),
            Array.Empty<NormalizedDiscordMessage>());
        var change = Assert.Single(new ChatPresentationSynchronizer().Synchronize(state, null));
        return Assert.IsType<ChatMessagePresentation>(change.Message);
    }
}
