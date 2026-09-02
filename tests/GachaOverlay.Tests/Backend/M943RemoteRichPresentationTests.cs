using System.Reflection;
using Discord;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Providers;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Backend.Chat;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed class M943RemoteRichPresentationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("Png", 1, "png")]
    [InlineData("Apng", 2, "png")]
    [InlineData("Lottie", 3, null)]
    [InlineData("Gif", 4, "gif")]
    [InlineData("Unknown", null, null)]
    public void RemoteSticker_FormatMapsToExistingBoundedMediaPipeline(
        string format,
        int? expectedFormat,
        string? expectedExtension)
    {
        var message = Message() with
        {
            Stickers = new[] { new ChatSticker(900, "Wave", format, null) },
        };

        var presentation = Project(message);

        var sticker = Assert.Single(presentation.Stickers);
        Assert.Equal(expectedFormat, sticker.FormatType);
        var url = DiscordMediaAssetService.ResolveStickerUrl(sticker);
        Assert.Equal(
            expectedExtension is null
                ? null
                : $"https://media.discordapp.net/stickers/900.{expectedExtension}" +
                    "?size=256&quality=lossless",
            url);
    }

    [Theory]
    [InlineData(StickerFormatType.Png, "png", "media.discordapp.net")]
    [InlineData(StickerFormatType.Apng, "png", "media.discordapp.net")]
    [InlineData(StickerFormatType.Gif, "gif", "media.discordapp.net")]
    [InlineData(StickerFormatType.Lottie, "json", "cdn.discordapp.com")]
    public void BackendStickerAssetUrl_UsesRenderableDiscordEndpoint(
        StickerFormatType format,
        string extension,
        string host)
    {
        var url = DiscordChatMessageNormalizer.ResolveStickerAssetUrl(900, format);

        Assert.StartsWith($"https://{host}/stickers/900.{extension}", url);
        Assert.Equal(
            format == StickerFormatType.Lottie,
            !url.Contains("?size=256&quality=lossless", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteStructuredSticker_UsesNamedFallbackAndSuppressesLegacyPlaceholder()
    {
        var presentation = Project(Message() with
        {
            Stickers = new[] { new ChatSticker(900, "Wave", "Png", null) },
        });
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });

        Assert.True(viewModel.HasSticker);
        Assert.True(viewModel.ShowStickerFallback);
        Assert.Equal("스티커: Wave", viewModel.StickerFallbackText);
        Assert.Equal("스티커: Wave", viewModel.PlainText);
        Assert.DoesNotContain("[스티커]", viewModel.PlainText, StringComparison.Ordinal);
        Assert.Empty(viewModel.ForwardedMessages);
    }

    [Fact]
    public void RemoteForward_IsOneStructuredBlockWithTextMediaAndNoFalseReply()
    {
        var message = Message() with
        {
            ForwardedSnapshots = new[]
            {
                Forward(
                    "forwarded text",
                    attachments: new[]
                    {
                        new ChatAttachment(
                            90,
                            "forward.png",
                            "https://cdn.example/forward.png",
                            "https://proxy.example/forward.png",
                            10,
                            "image/png",
                            100,
                            80,
                            null,
                            null,
                            false,
                            null,
                            null,
                            false),
                    }),
            },
            Reference = new ChatMessageReference("Forward", 10, 100, 99, null),
        };

        var normalized = Normalize(message);
        var presentation = Project(normalized);
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });

        Assert.Equal(DiscordMessageFallbackKind.None, normalized.FallbackKind);
        Assert.Equal(string.Empty, normalized.Content);
        Assert.Empty(normalized.Attachments);
        Assert.Null(normalized.RemoteMetadata?.Reply);
        Assert.Equal(string.Empty, presentation.PlainText);
        Assert.Empty(presentation.Media);
        var forwarded = Assert.Single(viewModel.ForwardedMessages);
        Assert.Equal("전달된 메시지", forwarded.Label);
        Assert.DoesNotContain("(1)", forwarded.Label, StringComparison.Ordinal);
        Assert.Equal("forwarded text", forwarded.Text);
        Assert.Equal("forward.png", forwarded.PrimaryMedia?.DisplayName);
        Assert.True(forwarded.ShowMediaFallback);
        Assert.Contains("forward.png", forwarded.MediaFallbackText, StringComparison.Ordinal);
        Assert.False(viewModel.HasReply);
        Assert.False(viewModel.HasPrimaryText);
        Assert.DoesNotContain(
            viewModel.Tokens,
            token => token.Text.Contains("전달된 메시지", StringComparison.Ordinal));
        Assert.DoesNotContain("전달된 메시지", viewModel.RemoteDetailsText, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteForward_CustomEmojiUsesTokenAndImagePresentationPipeline()
    {
        var presentation = Project(Message() with
        {
            ForwardedSnapshots = new[]
            {
                Forward("처음 <:GTA_Bunker:1418347703552839802> 끝"),
            },
        });

        var forward = Assert.Single(presentation.ForwardedMessages);
        Assert.Equal("처음 :GTA_Bunker: 끝", forward.Text);
        var emoji = Assert.Single(forward.Tokens.Where(token =>
            token.Kind == ChatTokenKind.CustomEmoji));
        Assert.Equal("1418347703552839802", emoji.Identity);
        Assert.Equal(":GTA_Bunker:", emoji.Text);

        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });
        var forwardedViewModel = Assert.Single(viewModel.ForwardedMessages);
        var emojiViewModel = Assert.Single(forwardedViewModel.Tokens.Where(token =>
            token.Kind == ChatTokenKind.CustomEmoji));
        Assert.Equal("1418347703552839802", emojiViewModel.Identity);
        Assert.DoesNotContain(
            forwardedViewModel.Tokens,
            token => token.Text.Contains("<:GTA_Bunker:", StringComparison.Ordinal));
    }

    [Fact]
    public void TrueReplyAndForward_RemainDistinctPresentationParts()
    {
        var repliedTo = Message("original") with
        {
            Author = new ChatAuthor(88, "source", "Original", null, false, false),
        };
        var presentation = Project(Message("wrapper text") with
        {
            ForwardedSnapshots = new[] { Forward("forwarded text") },
            Reference = new ChatMessageReference("Default", 10, 100, 99, repliedTo),
        });
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(SupportedLocales.English),
            _ => { });

        Assert.True(viewModel.HasReply);
        Assert.Contains("Original", viewModel.ReplyText, StringComparison.Ordinal);
        Assert.Equal("wrapper text", viewModel.PlainText);
        Assert.True(viewModel.HasPrimaryText);
        Assert.Equal("forwarded text", Assert.Single(viewModel.ForwardedMessages).Text);
    }

    [Fact]
    public void StructuredForward_SuppressesOpaqueFallbackEvenIfLegacyFlagIsPresent()
    {
        var presentation = Project(Message() with
        {
            ForwardedSnapshots = new[] { Forward("structured") },
        }) with
        {
            FallbackKind = DiscordMessageFallbackKind.ForwardedMessage,
        };
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });

        Assert.False(viewModel.HasPrimaryText);
        Assert.DoesNotContain(
            viewModel.Tokens,
            token => token.Text.Contains("전달된 메시지", StringComparison.Ordinal));
        Assert.Equal(string.Empty, viewModel.PlainText);
        Assert.Equal("structured", Assert.Single(viewModel.ForwardedMessages).Text);
    }

    [Fact]
    public void RichUpdate_ReplacesForwardReplyAndFallbackWithoutStaleParts()
    {
        var rich = Project(Message() with
        {
            ForwardedSnapshots = new[] { Forward("old") },
            Reference = new ChatMessageReference("Default", 10, 100, 99, Message("reply")),
        });
        using var viewModel = new ChatMessageViewModel(
            rich,
            new ResourceLocalizationService(SupportedLocales.English),
            _ => { });
        Assert.Single(viewModel.ForwardedMessages);
        Assert.True(viewModel.HasReply);

        var plain = Project(Message("plain")) with { Revision = rich.Revision + 1 };
        viewModel.Update(plain);

        Assert.Empty(viewModel.ForwardedMessages);
        Assert.False(viewModel.HasForwardedMessages);
        Assert.False(viewModel.HasReply);
        Assert.Equal("plain", viewModel.PlainText);
        Assert.Single(viewModel.Tokens);
        Assert.False(viewModel.ShowStickerFallback);
    }

    [Fact]
    public void ForwardFingerprint_UpdatesWhenStructuredSnapshotChanges()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var first = Normalize(Message() with
        {
            ForwardedSnapshots = new[] { Forward("first") },
        });
        var second = Normalize(Message() with
        {
            ForwardedSnapshots = new[] { Forward("second") },
        });

        Assert.Equal(
            ChatPresentationChangeKind.SnapshotAdd,
            Assert.Single(synchronizer.Synchronize(State(first), null)).Kind);
        var update = Assert.Single(synchronizer.Synchronize(State(second), null));

        Assert.Equal(ChatPresentationChangeKind.Update, update.Kind);
        Assert.Equal("second", Assert.Single(update.Message!.ForwardedMessages).Text);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "Forwarded message", "Sticker: Wave")]
    [InlineData(SupportedLocales.Korean, "전달된 메시지", "스티커: Wave")]
    [InlineData(SupportedLocales.Japanese, "転送されたメッセージ", "ステッカー: Wave")]
    public void RemoteRichLabels_AreLocalized(
        string locale,
        string expectedForward,
        string expectedSticker)
    {
        var localization = new ResourceLocalizationService(locale);
        var forward = new ChatForwardMessageViewModel(
            new ChatForwardPresentation(
                "text",
                Array.Empty<ChatMediaCandidate>(),
                new[] { new ChatStickerPresentation("900", "Wave", 3, null) },
                0),
            localization);

        Assert.Equal(expectedForward, forward.Label);
        Assert.Equal(expectedSticker, forward.StickerFallbackText);
        Assert.True(forward.ShowStickerFallback);
    }

    [Fact]
    public void LegacyOpaqueStickerAndForwardFallbacks_RemainUnchanged()
    {
        using var sticker = LegacyViewModel(DiscordMessageFallbackKind.Sticker);
        using var forward = LegacyViewModel(DiscordMessageFallbackKind.ForwardedMessage);

        Assert.Equal("[스티커]", sticker.PlainText);
        Assert.Equal("[전달된 메시지]", forward.PlainText);
        Assert.Empty(sticker.ForwardedMessages);
        Assert.Empty(forward.ForwardedMessages);
    }

    [Fact]
    public void MessageTemplate_UsesDocumentedDeterministicCompositionOrder()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "ChatMessageView.xaml"));
        var balanced = Slice(xaml, "Text=\"{Binding ReplyText}\"", "x:Name=\"CompactBody\"");
        var compact = Slice(xaml, "x:Name=\"CompactNickname\"", "x:Name=\"UltraCompactNickname\"");
        var forwarded = Slice(
            xaml,
            "<DataTemplate x:Key=\"ForwardMessageTemplate\">",
            "</DataTemplate>");

        AssertOrder(
            balanced,
            "Text=\"{Binding ReplyText}\"",
            "x:Name=\"BalancedBody\"",
            "ItemsSource=\"{Binding ForwardedMessages}\"",
            "<local:ChatMediaView",
            "Text=\"{Binding RemoteDetailsText}\"");
        AssertOrder(
            compact,
            "Text=\"{Binding ReplyText}\"",
            "x:Name=\"CompactBody\"",
            "ItemsSource=\"{Binding ForwardedMessages}\"",
            "<local:ChatMediaView",
            "Text=\"{Binding RemoteDetailsText}\"");
        AssertOrder(
            forwarded,
            "Text=\"{Binding Label}\"",
            "Tokens=\"{Binding Tokens}\"",
            "Source=\"{Binding StickerImage}\"",
            "Source=\"{Binding Thumbnail}\"",
            "Text=\"{Binding DetailsText}\"");
    }

    [Fact]
    public void RemoteChannelComboBox_UsesDisplayNameTemplateForItemsAndSelection()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "FoundationWindow.xaml"));
        var channelSelector = Slice(xaml, "x:Name=\"RemoteChannelComboBox\"", "/>");

        Assert.Contains(
            "<DataTemplate x:Key=\"RemoteDisplayNameTemplate\"><TextBlock Text=\"{Binding DisplayName}\" /></DataTemplate>",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemTemplate=\"{StaticResource RemoteDisplayNameTemplate}\"",
            channelSelector,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayMemberPath", channelSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("MainChatSourceComboBox", xaml, StringComparison.Ordinal);
        Assert.Equal(
            "#🏠메인",
            new RemoteChannelOption("100", "🏠메인", "10", 0, false).DisplayName);
    }

    private static ChatMessageViewModel LegacyViewModel(DiscordMessageFallbackKind fallback) =>
        new(
            new ChatMessagePresentation(
                "legacy",
                "author",
                DateTimeOffset.UnixEpoch,
                Array.Empty<ChatToken>(),
                string.Empty,
                Array.Empty<ChatMediaCandidate>(),
                Array.Empty<ChatStickerPresentation>(),
                0,
                false,
                1,
                1)
            {
                FallbackKind = fallback,
            },
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });

    private static ChatMessagePresentation Project(ChatMessage message) =>
        Project(Normalize(message));

    private static ChatMessagePresentation Project(NormalizedDiscordMessage message) =>
        Assert.IsType<ChatMessagePresentation>(Assert.Single(
            new ChatPresentationSynchronizer().Synchronize(State(message), null)).Message);

    private static NormalizedDiscordMessage Normalize(ChatMessage message)
    {
        var method = typeof(RemoteChatIngressAdapter).GetMethod(
            "MapPatch",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var patch = Assert.IsType<DiscordMessagePatch>(method.Invoke(null, new object[] { message }));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        return Assert.Single(store.GetOrderedSnapshot());
    }

    private static DiscordMessageState State(NormalizedDiscordMessage message) => new(
        1,
        false,
        new[] { message },
        Array.Empty<NormalizedDiscordMessage>());

    private static ChatMessage Message(string content = "") => new(
        1,
        10,
        100,
        "Default",
        0,
        new ChatAuthor(7, "user", "Display", "Guild Nick", false, false),
        content,
        DateTimeOffset.UnixEpoch,
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
        Array.Empty<ChatForwardSnapshot>(),
        null,
        Array.Empty<ChatComponent>(),
        null);

    private static ChatForwardSnapshot Forward(
        string text,
        IReadOnlyList<ChatAttachment>? attachments = null) => new(
        "Default",
        text,
        DateTimeOffset.UnixEpoch,
        null,
        attachments ?? Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        Array.Empty<ChatComponent>());

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static void AssertOrder(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after index {previous}.");
            previous = current;
        }
    }

}
