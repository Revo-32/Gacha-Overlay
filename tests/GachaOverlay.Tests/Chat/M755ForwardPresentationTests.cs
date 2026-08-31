using System.Windows;
using System.Windows.Media;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Chat;

public sealed class M755ForwardPresentationTests
{
    [Theory]
    [InlineData(SupportedLocales.Korean, "[전달된 메시지]")]
    [InlineData(SupportedLocales.English, "[Forwarded message]")]
    [InlineData(SupportedLocales.Japanese, "[転送メッセージ]")]
    public void ForwardFallback_IsLocalized(string locale, string expected)
    {
        using var viewModel = CreateViewModel(locale);

        Assert.Equal(expected, viewModel.PlainText);
        Assert.Equal(expected, Assert.Single(viewModel.Tokens).Text);
    }

    [Theory]
    [InlineData(SupportedLocales.Korean, "[스티커]")]
    [InlineData(SupportedLocales.English, "[Sticker]")]
    [InlineData(SupportedLocales.Japanese, "[ステッカー]")]
    public void OpaqueStickerFallback_IsLocalizedInlineTextWithoutMediaBox(
        string locale,
        string expected)
    {
        using var viewModel = new ChatMessageViewModel(
            Presentation(DiscordMessageFallbackKind.Sticker),
            new ResourceLocalizationService(locale),
            _ => { });

        Assert.Equal(expected, viewModel.PlainText);
        Assert.Equal(expected, Assert.Single(viewModel.Tokens).Text);
        Assert.True(viewModel.ShowStickerFallback);
        Assert.False(viewModel.HasVisibleMedia);
        Assert.False(viewModel.HasSticker);
    }

    [Fact]
    public void PendingOpaqueHydration_DoesNotFlashStickerBeforeSnapshotClassification()
    {
        using var viewModel = new ChatMessageViewModel(
            Presentation(DiscordMessageFallbackKind.PendingHydration),
            new ResourceLocalizationService(SupportedLocales.Korean),
            _ => { });

        Assert.Equal(string.Empty, viewModel.PlainText);
        Assert.False(viewModel.ShowStickerFallback);
        Assert.False(viewModel.HasVisibleMedia);
    }

    [Theory]
    [InlineData(SupportedLocales.Korean, "[메시지]")]
    [InlineData(SupportedLocales.English, "[Message]")]
    [InlineData(SupportedLocales.Japanese, "[メッセージ]")]
    public void UnknownOpaqueMessage_UsesLocalizedNeutralFallback(
        string language,
        string expected)
    {
        var localization = new ResourceLocalizationService(language);
        using var viewModel = new ChatMessageViewModel(
            Presentation(DiscordMessageFallbackKind.Message),
            localization,
            _ => { });

        Assert.Equal(expected, viewModel.PlainText);
        Assert.False(viewModel.ShowStickerFallback);
        Assert.False(viewModel.HasVisibleMedia);
    }

    [Fact]
    public void LanguageChange_RefreshesExistingForwardFallback()
    {
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        using var viewModel = CreateViewModel(localization);

        localization.SetLanguage(SupportedLocales.Korean);

        Assert.Equal("[전달된 메시지]", viewModel.PlainText);
    }

    [Fact]
    public void ForwardFallback_RemainsDistinctFromStickerFallback()
    {
        var localization = new ResourceLocalizationService(SupportedLocales.Korean);
        using var viewModel = CreateViewModel(localization);

        Assert.Equal("[전달된 메시지]", viewModel.PlainText);
        Assert.Equal("[스티커]", localization["ChatStickerFallbackUnnamed"]);
        Assert.NotEqual(viewModel.PlainText, localization["ChatStickerFallbackUnnamed"]);
    }

    [Fact]
    public void UltraCompact_DoesNotRemoveForwardFallbackText()
    {
        using var viewModel = CreateViewModel(SupportedLocales.Korean);

        viewModel.ApplySettings(new AppSettings(), ChatResponsiveLevel.UltraCompact, Typography());

        Assert.True(viewModel.IsUltraCompact);
        Assert.Equal("[전달된 메시지]", viewModel.PlainText);
    }

    [Fact]
    public void InternalForwardResolutionName_IsNotExposedToHud()
    {
        using var viewModel = CreateViewModel(SupportedLocales.English);

        Assert.DoesNotContain(nameof(DiscordForwardResolutionMode.LookupFailed), viewModel.PlainText);
        Assert.Equal("[Forwarded message]", viewModel.PlainText);
    }

    private static ChatMessageViewModel CreateViewModel(string locale) =>
        CreateViewModel(new ResourceLocalizationService(locale));

    private static ChatMessageViewModel CreateViewModel(ResourceLocalizationService localization) =>
        new(Presentation(), localization, _ => { });

    private static ChatMessagePresentation Presentation(
        DiscordMessageFallbackKind fallbackKind = DiscordMessageFallbackKind.ForwardedMessage) => new(
        "wrapper",
        "forwarder",
        DateTimeOffset.UtcNow,
        new[] { new ChatToken(ChatTokenKind.Text, string.Empty) },
        string.Empty,
        Array.Empty<ChatMediaCandidate>(),
        Array.Empty<ChatStickerPresentation>(),
        0,
        false,
        1,
        1)
        {
            FallbackKind = fallbackKind,
        };

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
