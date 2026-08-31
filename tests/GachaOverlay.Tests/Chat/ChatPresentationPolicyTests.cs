using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.Tests.Chat;

public sealed class ChatPresentationPolicyTests
{
    [Theory]
    [InlineData(ChatPresentationChangeKind.SnapshotAdd, true)]
    [InlineData(ChatPresentationChangeKind.Add, true)]
    [InlineData(ChatPresentationChangeKind.Update, false)]
    [InlineData(ChatPresentationChangeKind.Remove, false)]
    public void AutoScroll_TracksOnlyHydrationAndNewCreate(
        ChatPresentationChangeKind kind,
        bool expected)
    {
        Assert.Equal(expected, ChatAutoScrollPolicy.ShouldScrollToLatest(kind));
    }

    [Fact]
    public void BalancedFull_ShowsOptionalTimeAndImages()
    {
        var settings = AppSettings.CreateDefault() with
        {
            ChatLayoutMode = ChatLayoutMode.Balanced,
            ChatShowTime = true,
            ChatShowImages = true,
            ChatImageMode = ChatImageMode.ThumbnailAndEnlarge,
        };

        var layout = ChatLayoutPresentation.Resolve(settings, ChatResponsiveLevel.Full);

        Assert.True(layout.IsBalanced);
        Assert.True(layout.ShowTime);
        Assert.True(layout.ShowImages);
        Assert.True(layout.CanEnlarge);
    }

    [Fact]
    public void Reduced_DropsTimeAndImagesWithoutChangingUserMode()
    {
        var settings = AppSettings.CreateDefault() with { ChatLayoutMode = ChatLayoutMode.Balanced };

        var layout = ChatLayoutPresentation.Resolve(settings, ChatResponsiveLevel.Reduced);

        Assert.True(layout.IsBalanced);
        Assert.False(layout.ShowTime);
        Assert.False(layout.ShowImages);
        Assert.Equal(ChatLayoutMode.Balanced, settings.ChatLayoutMode);
    }

    [Fact]
    public void UltraCompact_OverridesPresentationOnly()
    {
        var settings = AppSettings.CreateDefault() with { ChatLayoutMode = ChatLayoutMode.Compact };

        var layout = ChatLayoutPresentation.Resolve(settings, ChatResponsiveLevel.UltraCompact);

        Assert.True(layout.IsUltraCompact);
        Assert.False(layout.IsCompact);
        Assert.False(layout.ShowImages);
        Assert.Equal(ChatLayoutMode.Compact, settings.ChatLayoutMode);
    }

    [Theory]
    [InlineData("1", 2, 3, "1", 2, 3, true, true)]
    [InlineData("1", 2, 3, "1", 3, 3, true, false)]
    [InlineData("1", 2, 3, "1", 2, 4, true, false)]
    [InlineData("1", 2, 3, "1", 2, 3, false, false)]
    public void EnrichmentGuard_RejectsStaleGenerationRevisionOrDeletion(
        string expectedId,
        long expectedGeneration,
        int expectedRevision,
        string actualId,
        long actualGeneration,
        int actualRevision,
        bool exists,
        bool expected)
    {
        Assert.Equal(
            expected,
            ChatEnrichmentGuard.IsCurrent(
                new ChatEnrichmentIdentity(expectedId, expectedGeneration, expectedRevision),
                new ChatEnrichmentIdentity(actualId, actualGeneration, actualRevision),
                exists));
    }

    [Fact]
    public void VisualMetrics_EmojiGrowsWithFontAndRemainsBounded()
    {
        var small = ChatVisualMetrics.CalculateEmojiExtent(10, 14);
        var medium = ChatVisualMetrics.CalculateEmojiExtent(16, 24);
        var large = ChatVisualMetrics.CalculateEmojiExtent(80, 120);

        Assert.Equal(18, small);
        Assert.True(medium > small);
        Assert.Equal(48, large);
        Assert.Equal(96, ChatVisualMetrics.CalculateStickerExtent(ChatResponsiveLevel.Full));
        Assert.Equal(72, ChatVisualMetrics.CalculateStickerExtent(ChatResponsiveLevel.Reduced));
        Assert.Equal(0, ChatVisualMetrics.CalculateStickerExtent(ChatResponsiveLevel.UltraCompact));
    }

    [Theory]
    [InlineData(ChatStylePreset.Clean, ChatFontPreset.Pretendard, ChatLayoutMode.Balanced, 0.62, 1.42)]
    [InlineData(ChatStylePreset.Modern, ChatFontPreset.Kimm, ChatLayoutMode.Compact, 0.68, 1.4)]
    [InlineData(ChatStylePreset.HighReadability, ChatFontPreset.WantedSans, ChatLayoutMode.Balanced, 0.9, 1.5)]
    [InlineData(ChatStylePreset.GtaLegacy, ChatFontPreset.Cafe24ProSlim, ChatLayoutMode.Compact, 0.42, 1.32)]
    public void StylePreset_AppliesExpectedIndependentSettings(
        ChatStylePreset preset,
        ChatFontPreset expectedFont,
        ChatLayoutMode expectedLayout,
        double expectedOpacity,
        double expectedLineHeight)
    {
        var applied = ChatStylePresets.Apply(AppSettings.CreateDefault(), preset);

        Assert.Equal(expectedFont, applied.ChatFontPreset);
        Assert.Equal(expectedLayout, applied.ChatLayoutMode);
        Assert.Equal(expectedOpacity, applied.HudSurfaceOpacity);
        Assert.Equal(expectedLineHeight, applied.ChatLineHeightMultiplier);
    }

    [Fact]
    public void PresetResult_RemainsManuallyEditableAndResponsiveDoesNotResetFont()
    {
        var applied = ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            ChatStylePreset.GtaLegacy);
        var edited = applied with { ChatFontSizePoints = 17 };

        _ = ChatLayoutPresentation.Resolve(edited, ChatResponsiveLevel.UltraCompact);

        Assert.Equal(17, edited.ChatFontSizePoints);
        Assert.Equal(ChatFontPreset.Cafe24ProSlim, edited.ChatFontPreset);
    }
}
