using System.Globalization;
using GachaOverlay.Core.Localization;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Localization;

public sealed class ResourceLocalizationServiceTests
{
    [Fact]
    public void Constructor_DefaultsToEnglishRegardlessOfCurrentUiCulture()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");

            var localization = new ResourceLocalizationService();

            Assert.Equal(SupportedLocales.English, localization.CurrentLocale);
            Assert.Equal("Foundation Ready", localization["FoundationReady"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(SupportedLocales.Korean, "기반 준비 완료")]
    [InlineData(SupportedLocales.Japanese, "基盤の準備完了")]
    public void SetLanguage_UsesSelectedResource(string locale, string expected)
    {
        var localization = new ResourceLocalizationService();

        localization.SetLanguage(locale);

        Assert.Equal(locale, localization.CurrentLocale);
        Assert.Equal(expected, localization["FoundationReady"]);
    }

    [Fact]
    public void MissingLocalizedTranslation_FallsBackToEnglish()
    {
        var localization = new ResourceLocalizationService(SupportedLocales.Korean);

        var value = localization["EnglishFallbackProbe"];

        Assert.Equal("English fallback ready", value);
    }

    [Fact]
    public void InvalidLocale_FallsBackToEnglish()
    {
        var localization = new ResourceLocalizationService("unsupported-locale");

        Assert.Equal(SupportedLocales.English, localization.CurrentLocale);
        Assert.Equal("Foundation Ready", localization["FoundationReady"]);
    }

    [Fact]
    public void RuntimeLanguageChange_RaisesNotification()
    {
        var localization = new ResourceLocalizationService();
        var notificationCount = 0;
        localization.LanguageChanged += (_, _) => notificationCount++;

        localization.SetLanguage(SupportedLocales.Japanese);

        Assert.Equal(1, notificationCount);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "HUD Settings")]
    [InlineData(SupportedLocales.Korean, "HUD 설정")]
    [InlineData(SupportedLocales.Japanese, "HUD 設定")]
    public void M3HudSettings_AreLocalized(string locale, string expected)
    {
        var localization = new ResourceLocalizationService(locale);

        Assert.Equal(expected, localization["SettingsTitle"]);
        Assert.NotEqual("TrayShowHud", localization["TrayShowHud"]);
        Assert.NotEqual("HudLocked", localization["HudLocked"]);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "Main Chat")]
    [InlineData(SupportedLocales.Korean, "메인 채팅")]
    [InlineData(SupportedLocales.Japanese, "メインチャット")]
    public void M4ChatSettings_AreLocalized(string locale, string expected)
    {
        var localization = new ResourceLocalizationService(locale);

        Assert.Equal(expected, localization["SettingsChatTitle"]);
        Assert.NotEqual("SettingsChatBalanced", localization["SettingsChatBalanced"]);
        Assert.NotEqual("SettingsImageEnlarge", localization["SettingsImageEnlarge"]);
        Assert.NotEqual("SettingsColorTheme", localization["SettingsColorTheme"]);
        Assert.NotEqual(
            "ColorThemeGitHubDarkDescription",
            localization["ColorThemeGitHubDarkDescription"]);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "[Sticker]")]
    [InlineData(SupportedLocales.Korean, "[스티커]")]
    [InlineData(SupportedLocales.Japanese, "[ステッカー]")]
    public void M41StickerAndPresetStrings_AreLocalized(string locale, string expected)
    {
        var localization = new ResourceLocalizationService(locale);

        Assert.Equal(expected, localization["ChatStickerFallbackUnnamed"]);
        Assert.NotEqual("SettingsPresetGtaLegacy", localization["SettingsPresetGtaLegacy"]);
        Assert.NotEqual("SettingsChatPresetApplied", localization["SettingsChatPresetApplied"]);
    }

    [Theory]
    [InlineData(SupportedLocales.English)]
    [InlineData(SupportedLocales.Korean)]
    [InlineData(SupportedLocales.Japanese)]
    public void M75NavigationSalesToolsAndRecoveryStrings_AreLocalized(string locale)
    {
        var localization = new ResourceLocalizationService(locale);
        var keys = new[]
        {
            "SettingsCategoryGeneral",
            "SettingsCategoryDiscord",
            "SettingsCategoryHud",
            "SettingsCategoryChat",
            "SettingsCategoryMedia",
            "SettingsCategorySales",
            "SettingsCategoryHotkeys",
            "SettingsCategoryDiagnostics",
            "SettingsProductMappingManager",
            "SettingsSalesPreview",
            "SettingsManualSalesResync",
            "SettingsClearMediaCache",
            "SalesPreviewLongNames",
        };

        Assert.All(keys, key => Assert.NotEqual(key, localization[key]));
    }
}
