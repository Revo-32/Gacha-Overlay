using GachaOverlay.Core.Chat;
using GachaOverlay.App.Presentation;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Core.Settings;
using System.Windows;
using System.Windows.Media;

namespace GachaOverlay.Tests.Chat;

public sealed class M75MediaSourcePolicyTests
{
    [Fact]
    public void SuccessfulPreview_RemovesOnlyExactSourceToken()
    {
        var media = Media("https://media.tenor.com/asset.gif", "https://tenor.com/view/123");

        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            "description https://tenor.com/view/123 https://example.com/keep",
            media,
            previewSucceeded: true,
            enabled: true);

        Assert.Equal("description  https://example.com/keep", result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FailureOrDisabled_KeepsOriginalUrl(bool succeeded, bool enabled)
    {
        const string content = "look https://tenor.com/view/123";
        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            content,
            Media("https://media.tenor.com/asset.gif", "https://tenor.com/view/123"),
            succeeded,
            enabled);

        Assert.Equal(content, result);
    }

    [Fact]
    public void UnrelatedAssetHost_DoesNotSuppressSource()
    {
        const string content = "https://example.com/post";
        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            content,
            Media("https://evil.invalid/image.png", "https://example.com/post"),
            true,
            true);

        Assert.Equal(content, result);
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/a.png", "https://media.discordapp.net/a.png", true)]
    [InlineData("https://tenor.com/view/a", "https://media.tenor.com/a.gif", true)]
    [InlineData("https://klipy.com/gifs/a", "https://cdn.klipy.com/a.gif", true)]
    [InlineData("https://example.com/a", "https://example.com/b", true)]
    [InlineData("https://example.com/a", "https://other.example/b", false)]
    [InlineData("http://tenor.com/a", "https://media.tenor.com/a.gif", false)]
    public void ProviderRelationship_IsNormalized(
        string source,
        string asset,
        bool expected) =>
        Assert.Equal(expected, ChatMediaSourcePolicy.AreRelated(source, asset));

    [Fact]
    public void TrailingPunctuation_IsPreserved()
    {
        var result = ChatMediaSourcePolicy.SuppressExactSourceToken(
            "watch https://tenor.com/view/123, now",
            Media("https://media.tenor.com/a.gif", "https://tenor.com/view/123"),
            true,
            true);

        Assert.Equal("watch , now", result);
    }

    [Fact]
    public void PreviewRefresh_PreservesAlreadyEnrichedCustomEmoji()
    {
        var presentation = new ChatMessagePresentation(
            "message",
            "author",
            DateTimeOffset.UtcNow,
            new[]
            {
                new ChatToken(ChatTokenKind.CustomEmoji, ":wave:", "emoji-1"),
                new ChatToken(ChatTokenKind.Text, " https://tenor.com/view/123"),
            },
            ":wave: https://tenor.com/view/123",
            new[] { Media("https://media.tenor.com/asset.gif", "https://tenor.com/view/123") },
            Array.Empty<ChatStickerPresentation>(),
            0,
            false,
            1,
            1);
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(),
            _ => { });
        var enrichedImage = new DrawingImage();
        viewModel.Tokens[0].Image = enrichedImage;

        viewModel.Thumbnail = new DrawingImage();

        Assert.Same(enrichedImage, viewModel.Tokens[0].Image);
    }

    [Fact]
    public void LargeMode_IsVisiblyLargerForImageAndStickerWhilePreservingCompactMetrics()
    {
        using var viewModel = new ChatMessageViewModel(
            PresentationWithSticker(),
            new ResourceLocalizationService(),
            _ => { });
        var compact = AppSettings.CreateDefault() with
        {
            ChatImageSizeMode = ChatImageSizeMode.Compact,
        };

        viewModel.ApplySettings(compact, ChatResponsiveLevel.Full, Typography());
        Assert.Equal(132, viewModel.ThumbnailWidth);
        Assert.Equal(96, viewModel.ThumbnailMaxHeight);
        Assert.Equal(96, viewModel.StickerExtent);

        viewModel.ApplySettings(
            compact with { ChatImageSizeMode = ChatImageSizeMode.Large },
            ChatResponsiveLevel.Full,
            Typography());
        Assert.Equal(360, viewModel.ThumbnailWidth);
        Assert.Equal(270, viewModel.ThumbnailMaxHeight);
        Assert.Equal(180, viewModel.StickerExtent);
    }

    [Theory]
    [InlineData(ChatResponsiveLevel.Full)]
    [InlineData(ChatResponsiveLevel.Reduced)]
    [InlineData(ChatResponsiveLevel.UltraCompact)]
    public void DenseManualSpacing_RemainsAppliedAcrossResponsiveModes(ChatResponsiveLevel level)
    {
        using var viewModel = new ChatMessageViewModel(
            PresentationWithSticker(),
            new ResourceLocalizationService(),
            _ => { });
        var settings = AppSettings.CreateDefault() with
        {
            ChatLineHeightMultiplier = 1,
            ChatMessageSpacing = -2,
        };

        viewModel.ApplySettings(settings, level, Typography());

        Assert.Equal(Math.Ceiling(viewModel.FontSizeDip), viewModel.LineHeight);
        Assert.Equal(0, viewModel.MessageMargin.Bottom);
    }

    private static ChatMediaCandidate Media(string asset, string source) =>
        new(asset, "image/gif", null, null, source);

    private static ChatMessagePresentation PresentationWithSticker() => new(
        "message",
        "author",
        DateTimeOffset.UtcNow,
        Array.Empty<ChatToken>(),
        string.Empty,
        new[] { Media("https://cdn.example/image.png", "https://cdn.example/image.png") },
        new[] { new ChatStickerPresentation("900", "Wave", 1, null) },
        0,
        false,
        1,
        1);

    private static ResolvedChatTypography Typography()
    {
        var role = new ResolvedChatFontRole(
            new FontFamily("Segoe UI"),
            FontWeights.Normal,
            "Segoe UI",
            ChatFontResolutionSource.System,
            false,
            null);
        return new ResolvedChatTypography(ChatFontPreset.Kimm, "Test", role, role);
    }
}
