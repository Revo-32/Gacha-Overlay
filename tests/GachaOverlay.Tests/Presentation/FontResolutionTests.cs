using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Presentation;

public sealed class FontResolutionTests
{
    [Fact]
    public void DefaultBundledCatalog_InitializesTheWpfPackUriParser()
    {
        var exception = Record.Exception(() => new WpfChatFontCatalog());

        Assert.Null(exception);
        Assert.True(UriParser.IsKnownScheme("pack"));
    }

    [Fact]
    public void BundledFonts_ResolveFromCopiedReleaseStyleAssetsWithVerifiedMetadata()
    {
        var fontDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (var file in new[]
                 {
                     "KIMM_Bold.ttf",
                     "KIMM_Light.ttf",
                     "Cafe24PROSlimMax.ttf",
                     "Cafe24PROSlimFit.ttf",
                     "PretendardVariable.ttf",
                     "WantedSansVariable.ttf",
                 })
        {
            Assert.True(File.Exists(Path.Combine(fontDirectory, file)), file);
        }

        var resolver = new ChatTypographyResolver(
            NullAppLogger.Instance,
            new WpfChatFontCatalog(fontDirectory));

        var modern = resolver.Resolve(ChatFontPreset.Kimm);
        Assert.Equal(ChatFontResolutionSource.Bundled, modern.Nickname.Source);
        Assert.Equal(ChatFontResolutionSource.Bundled, modern.Message.Source);
        Assert.False(modern.IsFallback);
        Assert.Equal(FontWeights.Bold, modern.Nickname.FontWeight);
        Assert.Equal(FontWeights.Light, modern.Message.FontWeight);
        Assert.Contains("#KIMM", modern.Nickname.FontFamily.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#KIMM L", modern.Message.FontFamily.Source, StringComparison.OrdinalIgnoreCase);

        var gta = resolver.Resolve(ChatFontPreset.Cafe24ProSlim);
        Assert.Equal(ChatFontResolutionSource.Bundled, gta.Nickname.Source);
        Assert.Equal(ChatFontResolutionSource.Bundled, gta.Message.Source);
        Assert.False(gta.IsFallback);
        Assert.Equal(FontWeights.Bold, gta.Nickname.FontWeight);
        Assert.Equal(FontWeights.Normal, gta.Message.FontWeight);
        Assert.Contains("Slim Max", gta.Nickname.FontFamily.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Slim Fit", gta.Message.FontFamily.Source, StringComparison.OrdinalIgnoreCase);

        foreach (var preset in new[] { ChatFontPreset.Pretendard, ChatFontPreset.WantedSans })
        {
            var bundled = resolver.Resolve(preset);
            Assert.Equal(ChatFontResolutionSource.Bundled, bundled.Nickname.Source);
            Assert.Equal(ChatFontResolutionSource.Bundled, bundled.Message.Source);
            Assert.False(bundled.IsFallback);
        }
    }

    [Theory]
    [InlineData(ChatFontPreset.Pretendard, "Pretendard SemiBold", "Pretendard Regular", 600, 400)]
    [InlineData(ChatFontPreset.WantedSans, "Wanted Sans Bold", "Wanted Sans Medium", 700, 500)]
    public void NewTypographyPresets_ResolveFromBundledAssets(
        ChatFontPreset preset,
        string nicknameName,
        string messageName,
        int nicknameWeight,
        int messageWeight)
    {
        var resolver = new ChatTypographyResolver(
            NullAppLogger.Instance,
            new ConfigurableCatalog(systemFontAvailable: false));

        var typography = resolver.Resolve(preset);

        Assert.False(typography.IsFallback);
        Assert.Equal(ChatFontResolutionSource.Bundled, typography.Nickname.Source);
        Assert.Equal(nicknameName, typography.Nickname.ResolvedDisplayName);
        Assert.Equal(messageName, typography.Message.ResolvedDisplayName);
        Assert.Equal(nicknameWeight, typography.Nickname.FontWeight.ToOpenTypeWeight());
        Assert.Equal(messageWeight, typography.Message.FontWeight.ToOpenTypeWeight());
    }

    [Fact]
    public void NewBundledTypography_UsesExplicitFallbackWhenMetadataIsInvalid()
    {
        var resolver = new ChatTypographyResolver(
            NullAppLogger.Instance,
            new ConfigurableCatalog(
                systemFontAvailable: false,
                bundledFailure: ChatFontFallbackReason.FamilyMetadataMismatch));

        var typography = resolver.Resolve(ChatFontPreset.WantedSans);

        Assert.True(typography.IsFallback);
        Assert.Equal(ChatFontResolutionSource.Fallback, typography.Nickname.Source);
        Assert.Equal("Malgun Gothic", typography.Nickname.ResolvedDisplayName);
        Assert.Equal(ChatFontFallbackReason.FamilyMetadataMismatch, typography.FallbackReason);
    }

    [Fact]
    public void InvalidBundledMetadata_DoesNotPretendResolutionSucceeded()
    {
        var resolver = new ChatTypographyResolver(
            NullAppLogger.Instance,
            new ConfigurableCatalog(
                systemFontAvailable: false,
                bundledFailure: ChatFontFallbackReason.FamilyMetadataMismatch));

        var typography = resolver.Resolve(ChatFontPreset.Kimm);

        Assert.True(typography.IsFallback);
        Assert.Equal(ChatFontResolutionSource.Fallback, typography.Nickname.Source);
        Assert.Equal(ChatFontFallbackReason.FamilyMetadataMismatch, typography.FallbackReason);
    }

    [Fact]
    public void PreviewActualChatAndMeasurement_ConsumeSameResolvedTypography()
    {
        var resolver = new ChatTypographyResolver(
            NullAppLogger.Instance,
            new ConfigurableCatalog(systemFontAvailable: false));
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        var settings = AppSettings.CreateDefault();
        using var coordinator = new ChatPresentationCoordinator(
            new ChatViewModel(),
            new DiscordMediaAssetService(NullAppLogger.Instance),
            localization,
            NullAppLogger.Instance,
            settings,
            resolver);
        var resolved = resolver.Resolve(settings.ChatFontPreset);

        Assert.Same(resolved, coordinator.CurrentTypography);

        using var message = new ChatMessageViewModel(
            Message(),
            localization,
            _ => { });
        var notifications = new List<string?>();
        message.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        message.ApplySettings(settings, ChatResponsiveLevel.Full, resolved);

        Assert.Equal(resolved.Nickname.FontFamily, message.NicknameFontFamily);
        Assert.Equal(resolved.Message.FontFamily, message.MessageFontFamily);
        Assert.Equal(resolved.Nickname.FontWeight, message.NicknameFontWeight);
        Assert.Equal(resolved.Message.FontWeight, message.MessageFontWeight);
        Assert.True(message.TypographyRevision > 0);
        Assert.Contains(nameof(ChatMessageViewModel.EmojiExtent), notifications);

        var before = coordinator.ResponsiveMeasurementRevision;
        coordinator.ApplySettings(settings with { ChatFontPreset = ChatFontPreset.Cafe24ProSlim });
        Assert.True(coordinator.ResponsiveMeasurementRevision > before);
        Assert.Same(
            resolver.Resolve(ChatFontPreset.Cafe24ProSlim),
            coordinator.CurrentTypography);
    }

    private static ChatMessagePresentation Message() => new(
        "1",
        "ItoToko",
        DateTimeOffset.UtcNow,
        new[] { new ChatToken(ChatTokenKind.Text, "지금 접속할 사람? ABC 123") },
        "지금 접속할 사람? ABC 123",
        Array.Empty<ChatMediaCandidate>(),
        Array.Empty<ChatStickerPresentation>(),
        0,
        false,
        1,
        1);

    private sealed class ConfigurableCatalog : IChatFontCatalog
    {
        private readonly bool _systemFontAvailable;
        private readonly ChatFontFallbackReason? _bundledFailure;

        public ConfigurableCatalog(
            bool systemFontAvailable,
            ChatFontFallbackReason? bundledFailure = null)
        {
            _systemFontAvailable = systemFontAvailable;
            _bundledFailure = bundledFailure;
        }

        public bool TryResolveBundled(
            string wpfFamilyName,
            string metadataFamilyName,
            FontWeight requestedWeight,
            string resolvedDisplayName,
            out ResolvedChatFontRole? role,
            out ChatFontFallbackReason failureReason)
        {
            if (_bundledFailure.HasValue)
            {
                role = null;
                failureReason = _bundledFailure.Value;
                return false;
            }

            role = new ResolvedChatFontRole(
                new FontFamily(resolvedDisplayName),
                requestedWeight,
                resolvedDisplayName,
                ChatFontResolutionSource.Bundled,
                false,
                null);
            failureReason = default;
            return true;
        }

        public bool TryResolveSystem(
            string familyName,
            FontWeight requestedWeight,
            out ResolvedChatFontRole? role)
        {
            if (!_systemFontAvailable)
            {
                role = null;
                return false;
            }

            role = new ResolvedChatFontRole(
                new FontFamily("Segoe UI"),
                requestedWeight,
                familyName,
                ChatFontResolutionSource.System,
                false,
                null);
            return true;
        }

        public ResolvedChatFontRole ResolveFallback(
            FontWeight requestedWeight,
            ChatFontFallbackReason reason) => new(
                new FontFamily("Malgun Gothic"),
                requestedWeight,
                "Malgun Gothic",
                ChatFontResolutionSource.Fallback,
                true,
                reason);
    }
}
