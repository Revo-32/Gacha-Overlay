using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Chat;

public sealed class M752StickerTests
{
    [Theory]
    [InlineData(1, "https://media.discordapp.net/stickers/900.png?size=256&quality=lossless")]
    [InlineData(2, "https://media.discordapp.net/stickers/900.png?size=256&quality=lossless")]
    [InlineData(4, "https://media.discordapp.net/stickers/900.gif?size=256&quality=lossless")]
    [InlineData(null, null)]
    public void RenderableStickerFormats_UseGlobalDiscordAssetEndpoints(
        int? format,
        string? expected)
    {
        var url = DiscordMediaAssetService.ResolveStickerUrl(
            new ChatStickerPresentation("900", "Wave", format, null));

        Assert.Equal(expected, url);
    }

    [Fact]
    public void SuppliedHttpsAssetUrl_HasPriorityForExternalStickerPayload()
    {
        const string asset = "https://cdn.example.test/external-sticker.webp";

        var url = DiscordMediaAssetService.ResolveStickerUrl(
            new ChatStickerPresentation("900", "Wave", 1, asset));

        Assert.Equal(asset, url);
    }

    [Fact]
    public void ProtocolRelativeExternalAsset_IsSafelyNormalizedToHttps()
    {
        var url = DiscordMediaAssetService.ResolveStickerUrl(
            new ChatStickerPresentation(
                "900",
                "Wave",
                1,
                "//cdn.discordapp.com/stickers/900.png"));

        Assert.Equal("https://cdn.discordapp.com/stickers/900.png", url);
    }

    [Fact]
    public void LottieSticker_UsesVisibleLocalizedFallbackInsteadOfInvalidBitmapUrl()
    {
        var sticker = new ChatStickerPresentation("900", "Wave", 3, null);
        Assert.Null(DiscordMediaAssetService.ResolveStickerUrl(sticker));
        using var viewModel = new ChatMessageViewModel(
            Presentation(sticker),
            new ResourceLocalizationService("ko"),
            _ => { });

        Assert.True(viewModel.HasSticker);
        Assert.True(viewModel.ShowStickerFallback);
        Assert.Equal("[스티커]", viewModel.StickerFallbackText);
        Assert.Equal("[스티커]", viewModel.PlainText);
        Assert.False(viewModel.HasVisibleMedia);
    }

    [Fact]
    public void UnknownFormatWithoutPayloadUrl_UsesFallbackInsteadOfGuessingPng()
    {
        var sticker = new ChatStickerPresentation("900", "Mystery", 99, null);

        Assert.Null(DiscordMediaAssetService.ResolveStickerUrl(sticker));
    }

    [Fact]
    public void MissingFormatWithoutPayloadUrl_UsesFallbackInsteadOfGuessingPng()
    {
        var sticker = new ChatStickerPresentation("900", "Mystery", null, null);

        Assert.Null(DiscordMediaAssetService.ResolveStickerUrl(sticker));
    }

    [Fact]
    public void MissingIdWithName_RemainsVisibleAsLocalizedFallback()
    {
        var sticker = new ChatStickerPresentation(string.Empty, "External Wave", 2, null);
        using var viewModel = new ChatMessageViewModel(
            Presentation(sticker),
            new ResourceLocalizationService("en"),
            _ => { });

        Assert.Null(DiscordMediaAssetService.ResolveStickerUrl(sticker));
        Assert.True(viewModel.ShowStickerFallback);
        Assert.Equal("[Sticker]", viewModel.StickerFallbackText);
        Assert.Equal("[Sticker]", viewModel.PlainText);
        Assert.False(viewModel.HasVisibleMedia);
    }

    [Fact]
    public void MissingName_UsesUnnamedFallback()
    {
        var sticker = new ChatStickerPresentation("900", string.Empty, 3, null);
        using var viewModel = new ChatMessageViewModel(
            Presentation(sticker),
            new ResourceLocalizationService("ko"),
            _ => { });

        Assert.True(viewModel.ShowStickerFallback);
        Assert.Equal("[스티커]", viewModel.StickerFallbackText);
        Assert.Equal("[스티커]", viewModel.PlainText);
        Assert.False(viewModel.HasVisibleMedia);
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/gif", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData(null, true)]
    [InlineData("text/html", false)]
    [InlineData("application/json", false)]
    public void HttpContentType_IsValidatedBeforeBitmapDecode(string? contentType, bool expected)
    {
        Assert.Equal(expected, DiscordMediaAssetService.IsSupportedImageContentType(contentType));
    }

    private static ChatMessagePresentation Presentation(ChatStickerPresentation sticker) => new(
        "message",
        "author",
        DateTimeOffset.UtcNow,
        Array.Empty<ChatToken>(),
        string.Empty,
        Array.Empty<ChatMediaCandidate>(),
        new[] { sticker },
        0,
        false,
        1,
        1);
}
