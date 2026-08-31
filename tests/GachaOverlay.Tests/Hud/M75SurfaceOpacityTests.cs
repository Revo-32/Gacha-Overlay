using GachaOverlay.Core.Hud;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Hud;

public sealed class M75SurfaceOpacityTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0.5, 0.5, 0.25)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0.5, 0)]
    [InlineData(2, 0.5, 0.5)]
    public void EffectiveOpacity_IsNormalizedGlobalTimesLocal(
        double global,
        double local,
        double expected)
    {
        Assert.Equal(expected, HudSurfaceOpacityPolicy.CalculateEffectiveOpacity(global, local));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 255)]
    [InlineData(0.5, 128)]
    public void Alpha_UsesFullByteRange(double opacity, byte expected) =>
        Assert.Equal(expected, HudSurfaceOpacityPolicy.CalculateAlpha(opacity, 1));

    [Fact]
    public void ZeroGlobalOpacity_DoesNotChangeChatTypographyOrFixedPaintGutter()
    {
        using var viewModel = new ChatMessageViewModel(
            new ChatMessagePresentation(
                "message",
                "author",
                DateTimeOffset.UtcNow,
                new[] { new ChatToken(ChatTokenKind.Text, "body") },
                "body",
                Array.Empty<ChatMediaCandidate>(),
                Array.Empty<ChatStickerPresentation>(),
                0,
                false,
                1,
                1),
            new ResourceLocalizationService(),
            _ => { });
        var settings = AppSettings.CreateDefault() with { HudSurfaceOpacity = 0 };
        var typography = new ChatTypographyResolver(NullAppLogger.Instance)
            .Resolve(settings.ChatFontPreset);

        viewModel.ApplySettings(settings, ChatResponsiveLevel.Full, typography);

        Assert.Equal(0, settings.HudSurfaceOpacity);
        Assert.True(viewModel.FontSizeDip > 0);
        Assert.Equal(11, ChatPaintSafety.CalculateViewportPadding(settings).Left);
    }
}
