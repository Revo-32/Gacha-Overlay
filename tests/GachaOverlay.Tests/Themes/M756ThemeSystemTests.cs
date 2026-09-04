using System.Windows.Media;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Themes;

public sealed class M756ThemeSystemTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void Catalog_ContainsExactlyFiveNamedThemesWithGitHubDarkAsDefault()
    {
        Assert.Equal(ColorThemeId.GitHubDark, ColorThemeCatalog.DefaultTheme);
        Assert.Equal(
            new[]
            {
                "GitHub Dark",
                "One Dark Pro",
                "Nord",
                "Tokyo Night",
                "Monokai",
            },
            ColorThemeCatalog.All.Select(theme => theme.DisplayName));
        Assert.Equal(5, ColorThemeCatalog.All.Count);
        Assert.Equal(5, Enum.GetValues<ColorThemeId>().Length);
    }

    [Fact]
    public void Catalog_CorePaletteMatchesApprovedSpecification()
    {
        AssertPalette(
            ColorThemeId.GitHubDark,
            "#0D1117|#161B22|#21262D|#30363D|#F0F6FC|#C9D1D9|#8B949E|#FFFFFF|#A371F7|#3FB950|#58A6FF|#3FB950|#58A6FF|#D29922|#F85149|#FF000000");
        AssertPalette(
            ColorThemeId.OneDarkPro,
            "#1E222A|#282C34|#303540|#3E4451|#E8EBF0|#C8CDD5|#8D949F|#FFFFFF|#C678DD|#98C379|#56B6C2|#98C379|#61AFEF|#E5C07B|#E06C75|#FF000000");
        AssertPalette(
            ColorThemeId.Nord,
            "#2E3440|#3B4252|#434C5E|#4C566A|#ECEFF4|#D8DEE9|#A7B0C0|#FFFFFF|#B48EAD|#A3BE8C|#81A1C1|#A3BE8C|#88C0D0|#EBCB8B|#BF616A|#FF000000");
        AssertPalette(
            ColorThemeId.TokyoNight,
            "#1A1B26|#24283B|#2B3046|#3B4261|#D5DAFF|#A9B1D6|#8C94B8|#FFFFFF|#BB9AF7|#9ECE6A|#7DCFFF|#9ECE6A|#7DCFFF|#E0AF68|#F7768E|#FF000000");
        AssertPalette(
            ColorThemeId.Monokai,
            "#1D1E1A|#272822|#303129|#49483E|#F8F8F2|#D7D7CC|#A0A097|#FFFFFF|#AE81FF|#A6E22E|#66D9EF|#A6E22E|#66D9EF|#E6DB74|#F92672|#FF000000");
    }

    [Fact]
    public void EveryTheme_DefinesTheSameCompleteSemanticTokenSet()
    {
        var expected = Enum.GetValues<SemanticColorToken>();
        foreach (var theme in ColorThemeCatalog.All)
        {
            Assert.Equal(expected.Length, theme.Colors.Count);
            Assert.All(expected, token => Assert.True(theme.Colors.ContainsKey(token),
                $"{theme.DisplayName} is missing {token}."));
            Assert.Equal(5, theme.Swatches.Count);
        }

        var themes = Assert.IsAssignableFrom<IList<ColorThemeDefinition>>(ColorThemeCatalog.All);
        Assert.True(themes.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => themes.Add(ColorThemeCatalog.All[0]));
        var colors = Assert.IsAssignableFrom<IDictionary<SemanticColorToken, string>>(
            ColorThemeCatalog.All[0].Colors);
        Assert.True(colors.IsReadOnly);
        Assert.Throws<NotSupportedException>(() =>
            colors[SemanticColorToken.AppBackground] = "#000000");
    }

    [Fact]
    public void ThemeResources_AreCompleteFrozenAndStableAcrossRapidSwitches()
    {
        string[]? expectedKeys = null;
        for (var pass = 0; pass < 20; pass++)
        {
            foreach (var theme in ColorThemeCatalog.All)
            {
                var resources = ColorThemeManager.CreateResources(theme);
                var keys = resources.Keys.Cast<object>().Select(key => key.ToString()!)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
                expectedKeys ??= keys;
                Assert.Equal(expectedKeys, keys);
                foreach (var token in Enum.GetValues<SemanticColorToken>())
                {
                    Assert.IsType<Color>(resources[$"{token}Color"]);
                    var brush = Assert.IsType<SolidColorBrush>(resources[$"{token}Brush"]);
                    Assert.True(brush.IsFrozen);
                }
            }
        }
    }

    [Fact]
    public void MajorTextAndStatusCombinations_MeetAuditedContrastFloors()
    {
        foreach (var theme in ColorThemeCatalog.All)
        {
            AssertContrast(theme, SemanticColorToken.TextPrimary, 4.5);
            AssertContrast(theme, SemanticColorToken.TextSecondary, 4.5);
            AssertContrast(theme, SemanticColorToken.TextMuted, 4.5);
            AssertContrast(theme, SemanticColorToken.ChatMessage, 4.5);
            AssertContrast(theme, SemanticColorToken.ChatNickname, 4.5);
            AssertContrast(theme, SemanticColorToken.ChatMention, 3.0);
            AssertContrast(theme, SemanticColorToken.StatusLive, 3.0);
            AssertContrast(theme, SemanticColorToken.StatusInfo, 3.0);
            AssertContrast(theme, SemanticColorToken.StatusWarning, 4.5);
            AssertContrast(theme, SemanticColorToken.StatusError, 2.4);

            var outlineContrast = Contrast(
                theme.Colors[SemanticColorToken.ChatOutline],
                theme.Colors[SemanticColorToken.ChatMessage]);
            Assert.True(outlineContrast >= 7,
                $"{theme.DisplayName} chat outline contrast was {outlineContrast:F2}.");
        }
    }

    [Fact]
    public void TransparentSurfacePolicy_KeepsContentAndCriticalPaintOpaque()
    {
        var contentTokens = new[]
        {
            SemanticColorToken.TextPrimary,
            SemanticColorToken.TextSecondary,
            SemanticColorToken.ChatMessage,
            SemanticColorToken.ChatNickname,
            SemanticColorToken.ChatMention,
            SemanticColorToken.ChatSelfMention,
            SemanticColorToken.ChatOutline,
            SemanticColorToken.StatusLive,
            SemanticColorToken.StatusInfo,
            SemanticColorToken.StatusWarning,
            SemanticColorToken.StatusError,
        };
        foreach (var theme in ColorThemeCatalog.All)
        {
            var resources = ColorThemeManager.CreateResources(theme);
            Assert.All(contentTokens, token => Assert.Equal(
                byte.MaxValue,
                Assert.IsType<SolidColorBrush>(resources[$"{token}Brush"]).Color.A));
        }

        var hud = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "HudWindow.xaml"));
        Assert.DoesNotContain("Window.Opacity", hud);
        Assert.DoesNotContain("Opacity=\"{Binding HudSurfaceOpacity", hud);
        Assert.Contains("<local:ChatView", hud);
        Assert.Contains("<local:SalesQueueView", hud);
    }

    [Fact]
    public void ThemeSelection_IsImmediatePersistentAndTypographyIndependent()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = new JsonSettingsStore(path);
        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            ChatFontPreset = ChatFontPreset.Cafe24ProSlim,
            ChatFontSizePoints = 17.5,
            ChatLineHeightMultiplier = 1.55,
            ChatMessageSpacing = 3.25,
            ChatNicknameOutlineThickness = 6,
            ChatMessageOutlineThickness = 7.25,
            HudSurfaceOpacity = 0.33,
            HudChromeOpacity = 0.72,
            ChatSurfaceOpacity = 0.64,
            SalesSurfaceOpacity = 0.55,
        }));
        var applied = new List<ColorThemeId>();
        using var viewModel = new FoundationViewModel(
            store,
            new ResourceLocalizationService(SupportedLocales.Korean),
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { },
            applyColorTheme: applied.Add);

        viewModel.ColorThemes.Single(option => option.Value == ColorThemeId.TokyoNight)
            .ApplyCommand.Execute(null);

        Assert.Equal(ColorThemeId.TokyoNight, viewModel.SelectedColorTheme);
        Assert.Equal(new[] { ColorThemeId.TokyoNight }, applied);
        Assert.Single(viewModel.ColorThemes, option => option.IsSelected);
        var reloaded = new JsonSettingsStore(path).Load();
        Assert.Equal(ColorThemeId.TokyoNight, reloaded.ColorTheme);
        Assert.Equal(ChatFontPreset.Cafe24ProSlim, reloaded.ChatFontPreset);
        Assert.Equal(17.5, reloaded.ChatFontSizePoints);
        Assert.Equal(1.55, reloaded.ChatLineHeightMultiplier);
        Assert.Equal(3.25, reloaded.ChatMessageSpacing);
        Assert.Equal(6, reloaded.ChatNicknameOutlineThickness);
        Assert.Equal(7.25, reloaded.ChatMessageOutlineThickness);
        Assert.Equal(0.33, reloaded.HudSurfaceOpacity);
        Assert.Equal(0.72, reloaded.HudChromeOpacity);
        Assert.Equal(0.64, reloaded.ChatSurfaceOpacity);
        Assert.Equal(0.55, reloaded.SalesSurfaceOpacity);
        var restartedStore = new JsonSettingsStore(path);
        restartedStore.Load();
        using var restartedViewModel = new FoundationViewModel(
            restartedStore,
            new ResourceLocalizationService(),
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { });
        Assert.Equal(ColorThemeId.TokyoNight, restartedViewModel.SelectedColorTheme);
    }

    [Fact]
    public void LegacySchemaNine_MigratesColorsOnlyAndPreservesOtherSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 9,
              "language": "ko",
              "discordClientId": "123456",
              "discordGuildId": "789012",
              "discordMainChannelId": "345678",
              "discordRedirectUri": "https://localhost/callback",
              "hudLockHotkey": { "modifiers": "Control+Shift", "key": "K" },
              "hudVisibilityHotkey": { "modifiers": "Alt", "key": "V" },
              "hotkeysCustomized": true,
              "salesTrackingEnabled": false,
              "salesShowCurrentSeller": false,
              "salesShowWaitingCount": false,
              "salesShowProduct": true,
              "chatFontPreset": 3,
              "chatFontSizePoints": 18,
              "chatNicknameShadowEnabled": false,
              "chatMessageShadowEnabled": true,
              "chatShadowOpacity": 0.31,
              "chatShadowDepth": 2.75,
              "chatNicknameOutlineEnabled": false,
              "chatNicknameOutlineThickness": 4.25,
              "chatMessageOutlineThickness": 5.5,
              "chatNicknameColor": "#123456",
              "chatMessageColor": "#ABCDEF",
              "chatMentionColor": "#010203",
              "chatSelfMentionColor": "#040506",
              "chatOutlineColor": "#000000",
              "chatShadowColor": "#070809",
              "futureNonColorSetting": { "keep": true }
            }
            """);
        var credentialPath = directory.File("discord-client-secret.dat");
        var oauthPath = directory.File("discord-oauth-token.dat");
        var credentialBytes = new byte[] { 1, 4, 9, 16 };
        var oauthBytes = new byte[] { 2, 3, 5, 7, 11 };
        File.WriteAllBytes(credentialPath, credentialBytes);
        File.WriteAllBytes(oauthPath, oauthBytes);

        var migrated = new JsonSettingsStore(path).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(ColorThemeId.GitHubDark, migrated.ColorTheme);
        Assert.Equal("ko", migrated.Language);
        Assert.False(migrated.ExtensionData?.ContainsKey("discordClientId"));
        Assert.False(migrated.ExtensionData?.ContainsKey("discordGuildId"));
        Assert.False(migrated.ExtensionData?.ContainsKey("discordMainChannelId"));
        Assert.False(migrated.ExtensionData?.ContainsKey("discordRedirectUri"));
        Assert.Equal("Control+Shift", migrated.HudLockHotkey.Modifiers);
        Assert.Equal("K", migrated.HudLockHotkey.Key);
        Assert.Equal("Alt", migrated.HudVisibilityHotkey.Modifiers);
        Assert.Equal("V", migrated.HudVisibilityHotkey.Key);
        Assert.False(migrated.SalesTrackingEnabled);
        Assert.False(migrated.SalesShowCurrentSeller);
        Assert.False(migrated.SalesShowWaitingCount);
        Assert.True(migrated.SalesShowProduct);
        Assert.Equal(ChatFontPreset.Cafe24ProSlim, migrated.ChatFontPreset);
        Assert.Equal(18, migrated.ChatFontSizePoints);
        Assert.False(migrated.ChatNicknameOutlineEnabled);
        Assert.Equal(4.25, migrated.ChatNicknameOutlineThickness);
        Assert.Equal(5.5, migrated.ChatMessageOutlineThickness);
        Assert.True(migrated.ExtensionData!.ContainsKey("futureNonColorSetting"));
        Assert.Equal(credentialBytes, File.ReadAllBytes(credentialPath));
        Assert.Equal(oauthBytes, File.ReadAllBytes(oauthPath));

        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("chatNicknameColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatMessageColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatMentionColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatSelfMentionColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatOutlineColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatShadowColor", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatNicknameShadowEnabled", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatMessageShadowEnabled", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatShadowOpacity", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatShadowDepth", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("futureNonColorSetting", persisted);
    }

    [Fact]
    public void InvalidCurrentTheme_FallsBackToGitHubDarkAndPersists()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 10, "colorTheme": 999, "chatFontSizePoints": 16 }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(ColorThemeId.GitHubDark, loaded.ColorTheme);
        Assert.Equal(16, loaded.ChatFontSizePoints);
        Assert.Contains("\"colorTheme\": 0", File.ReadAllText(path));
    }

    [Fact]
    public void SourceAudit_HasNoRawXamlColorsOrLegacyHexSettingsUi()
    {
        var appRoot = Path.Combine(RepositoryRoot, "src", "GachaOverlay.App");
        var xamlFiles = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories);
        foreach (var path in xamlFiles)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", source);
            Assert.DoesNotContain("DynamicResource Brush.", source);
        }
        var permittedColorFactories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ColorThemeManager.cs",
            "ColorThemeOption.cs",
        };
        foreach (var path in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !permittedColorFactories.Contains(Path.GetFileName(path))))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("new SolidColorBrush", source);
            Assert.DoesNotContain("Color.FromArgb", source);
            Assert.DoesNotContain("Brushes.Black", source);
            Assert.DoesNotContain("Brushes.White", source);
        }
        var productionSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(RepositoryRoot, "src"),
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("ChatNicknameColor", productionSource);
        Assert.DoesNotContain("ChatMessageColor", productionSource);
        Assert.DoesNotContain("SettingsColorInvalid", productionSource);

        var foundation = File.ReadAllText(Path.Combine(
            appRoot,
            "Presentation",
            "FoundationWindow.xaml"));
        Assert.Contains("ItemsSource=\"{Binding ColorThemes}\"", foundation);
        Assert.Contains("SettingsColorTheme", foundation);
        Assert.DoesNotContain("ChatNicknameColor", foundation);
        Assert.DoesNotContain("ChatMessageColor", foundation);
        Assert.DoesNotContain("#RRGGBB", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HEX", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2,
            foundation.Split(
                "Maximum=\"10\" TickFrequency=\"0.25\"",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ThemeAttributionNotice_CoversAllFiveReferencesAndShipsWithBuild()
    {
        var appRoot = Path.Combine(RepositoryRoot, "src", "GachaOverlay.App");
        var noticePath = Path.Combine(
            appRoot,
            "Assets",
            "Themes",
            "ThirdPartyNotices",
            "NOTICE-Color-Themes.txt");
        var notice = File.ReadAllText(noticePath);

        Assert.Contains("GitHub Dark", notice);
        Assert.Contains("One Dark Pro", notice);
        Assert.Contains("Nord", notice);
        Assert.Contains("Tokyo Night", notice);
        Assert.Contains("Monokai", notice);
        Assert.Contains("does not include, copy, or redistribute Monokai Pro", notice);
        Assert.Contains("MIT License text", notice);
        Assert.Contains(
            "Assets\\Themes\\ThirdPartyNotices\\**\\*",
            File.ReadAllText(Path.Combine(appRoot, "GachaOverlay.App.csproj")));
    }

    [Fact]
    public void UserFacingViews_ConsumeRequiredSemanticResources()
    {
        var presentation = Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation");
        var status = File.ReadAllText(Path.Combine(presentation, "SalesStatusIconHost.xaml"));
        var sales = File.ReadAllText(Path.Combine(presentation, "SalesQueueView.xaml"));
        var chat = File.ReadAllText(Path.Combine(presentation, "ChatMessageView.xaml"));
        var richText = File.ReadAllText(Path.Combine(presentation, "CrispOutlinedText.cs"));
        var settings = File.ReadAllText(Path.Combine(presentation, "FoundationWindow.xaml"));

        Assert.Contains("StatusErrorBrush", status);
        Assert.Contains("StatusWarningBrush", status);
        Assert.Contains("StatusLiveBrush", status);
        Assert.Contains("ChatSelfMentionBrush", sales);
        Assert.Contains("ChatNicknameBrush", chat);
        Assert.Contains("ChatMessageBrush", chat);
        Assert.Contains("ChatOutlineBrush", chat);
        Assert.Contains("CrispOutlinedText", chat);
        Assert.Contains("MentionForeground", richText);
        Assert.Contains("SelfMentionForeground", richText);
        Assert.Contains("TextPrimaryBrush", settings);
        Assert.Contains("TextSecondaryBrush", settings);
    }

    [Fact]
    public void NewInstall_UsesRequestedOutlineDefaultsAndNoShadowSettings()
    {
        var defaults = AppSettings.CreateDefault();

        Assert.True(defaults.ChatNicknameOutlineEnabled);
        Assert.True(defaults.ChatMessageOutlineEnabled);
        Assert.Equal(1.5, defaults.ChatNicknameOutlineThickness);
        Assert.Equal(1.5, defaults.ChatMessageOutlineThickness);
        Assert.DoesNotContain(
            defaults.GetType().GetProperties(),
            property => property.Name.Contains("Shadow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, ChatResponsiveLevel.Full)]
    [InlineData(1.5, ChatResponsiveLevel.Full)]
    [InlineData(6, ChatResponsiveLevel.Reduced)]
    [InlineData(10, ChatResponsiveLevel.UltraCompact)]
    public void OutlinePaintSafety_DoesNotChangeLogicalMessageSpacing(
        double thickness,
        ChatResponsiveLevel responsiveLevel)
    {
        var settings = AppSettings.CreateDefault() with
        {
            ChatNicknameOutlineThickness = thickness,
            ChatMessageOutlineThickness = thickness,
            ChatMessageSpacing = 2.25,
        };
        using var viewModel = new ChatMessageViewModel(
            new ChatMessagePresentation(
                "outline-test",
                "Nickname",
                DateTimeOffset.UtcNow,
                new[] { new ChatToken(ChatTokenKind.Text, "message") },
                "message",
                Array.Empty<ChatMediaCandidate>(),
                Array.Empty<ChatStickerPresentation>(),
                0,
                false,
                1,
                1),
            new ResourceLocalizationService(),
            _ => { });
        var typography = new ChatTypographyResolver(NullAppLogger.Instance)
            .Resolve(settings.ChatFontPreset);

        viewModel.ApplySettings(settings, responsiveLevel, typography);

        Assert.Equal(2.25, viewModel.MessageMargin.Bottom);
        Assert.Equal(0, viewModel.MessageMargin.Top);
        var viewportPadding = ChatPaintSafety.CalculateViewportPadding(settings);
        Assert.Equal(11, viewportPadding.Left);
        Assert.Equal(11, viewportPadding.Top);
        Assert.Equal(11, viewportPadding.Right);
        Assert.Equal(11, viewportPadding.Bottom);
    }

    private static void AssertPalette(ColorThemeId id, string expected)
    {
        var theme = ColorThemeCatalog.Get(id);
        var tokens = new[]
        {
            SemanticColorToken.AppBackground,
            SemanticColorToken.SurfaceBase,
            SemanticColorToken.SurfaceRaised,
            SemanticColorToken.BorderStrong,
            SemanticColorToken.TextPrimary,
            SemanticColorToken.TextSecondary,
            SemanticColorToken.TextMuted,
            SemanticColorToken.ChatNickname,
            SemanticColorToken.ChatMention,
            SemanticColorToken.ChatSelfMention,
            SemanticColorToken.AccentPrimary,
            SemanticColorToken.StatusLive,
            SemanticColorToken.StatusInfo,
            SemanticColorToken.StatusWarning,
            SemanticColorToken.StatusError,
            SemanticColorToken.ChatOutline,
        };
        Assert.Equal(expected, string.Join('|', tokens.Select(token => theme.Colors[token])));
    }

    private static void AssertContrast(
        ColorThemeDefinition theme,
        SemanticColorToken foreground,
        double minimum)
    {
        var actual = Contrast(
            theme.Colors[SemanticColorToken.SurfaceBase],
            theme.Colors[foreground]);
        Assert.True(actual >= minimum,
            $"{theme.DisplayName} {foreground} contrast was {actual:F2}, expected {minimum:F2}.");
    }

    private static double Contrast(string first, string second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(string value)
    {
        var rgb = value[^6..];
        var channels = new[]
        {
            Convert.ToByte(rgb[..2], 16),
            Convert.ToByte(rgb[2..4], 16),
            Convert.ToByte(rgb[4..6], 16),
        }.Select(channel => channel / 255d)
            .Select(channel => channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4))
            .ToArray();
        return (0.2126 * channels[0]) +
            (0.7152 * channels[1]) +
            (0.0722 * channels[2]);
    }
}
