using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class WindowsDiscordOpaqueMessageResolverTests
{
    [Fact]
    public void ForwardMarkerThenText_RecoversForwardedBody()
    {
        var resolution = WindowsDiscordOpaqueMessageResolver.Classify(
        [
            Text("13:14"),
            Text("전달됨"),
            Text("퍼시픽정주행 1/4 25%"),
        ]);

        Assert.Equal(DiscordOpaqueMessageResolutionKind.ForwardedText, resolution.Kind);
        Assert.Equal("퍼시픽정주행 1/4 25%", resolution.Content);
    }

    [Fact]
    public void ForwardMarkerWithImage_IsNeverClassifiedAsDirectSticker()
    {
        var resolution = WindowsDiscordOpaqueMessageResolver.Classify(
        [
            Text("Forwarded"),
            Image("Steam Laugh, Sticker"),
        ]);

        Assert.Equal(DiscordOpaqueMessageResolutionKind.ForwardedMessage, resolution.Kind);
    }

    [Theory]
    [InlineData("Steam Laugh, Sticker")]
    [InlineData("스팀 웃음, 스티커")]
    [InlineData("スチーム笑い、ステッカー")]
    public void ExplicitStickerImageName_IsPositiveStickerEvidence(string name)
    {
        var resolution = WindowsDiscordOpaqueMessageResolver.Classify([Image(name)]);

        Assert.Equal(DiscordOpaqueMessageResolutionKind.Sticker, resolution.Kind);
    }

    [Fact]
    public void GenericImage_IsNotEnoughToClaimSticker()
    {
        var resolution = WindowsDiscordOpaqueMessageResolver.Classify([Image("이미지")]);

        Assert.Equal(DiscordOpaqueMessageResolutionKind.Unknown, resolution.Kind);
    }

    private static WindowsDiscordOpaqueMessageResolver.UiObservation Text(string name) =>
        new(true, false, name);

    private static WindowsDiscordOpaqueMessageResolver.UiObservation Image(string name) =>
        new(false, true, name);
}
