using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Tests.Chat;

public sealed class M42TypographyAndPresetTests
{
    [Theory]
    [InlineData(ChatStylePreset.Clean, ChatFontPreset.Pretendard, "Pretendard")]
    [InlineData(ChatStylePreset.Modern, ChatFontPreset.Kimm, "한국기계연구원")]
    [InlineData(ChatStylePreset.HighReadability, ChatFontPreset.WantedSans, "Wanted Sans")]
    [InlineData(ChatStylePreset.GtaLegacy, ChatFontPreset.Cafe24ProSlim, "Cafe24 PRO Slim")]
    public void StylePreset_MapsToRequestedFont(
        ChatStylePreset style,
        ChatFontPreset expectedFont,
        string expectedDisplayName)
    {
        var applied = ChatStylePresets.Apply(AppSettings.CreateDefault(), style);

        Assert.Equal(expectedFont, applied.ChatFontPreset);
        Assert.Equal(expectedDisplayName, ChatSettings.ResolveFontFamily(applied.ChatFontPreset));
    }

    [Theory]
    [InlineData(ChatFontPreset.Kimm, "KIMM_Bold", ChatFontRoleWeight.Bold, "KIMM_Light", ChatFontRoleWeight.Light)]
    [InlineData(ChatFontPreset.Pretendard, "Pretendard Variable", ChatFontRoleWeight.SemiBold, "Pretendard Variable", ChatFontRoleWeight.Normal)]
    [InlineData(ChatFontPreset.WantedSans, "Wanted Sans Variable", ChatFontRoleWeight.Bold, "Wanted Sans Variable", ChatFontRoleWeight.Medium)]
    [InlineData(ChatFontPreset.Cafe24ProSlim, "Cafe24 PRO Slim", ChatFontRoleWeight.Bold, "Cafe24 PRO Slim", ChatFontRoleWeight.Normal)]
    public void Typography_SeparatesNicknameAndMessageRoles(
        ChatFontPreset preset,
        string nicknameFamily,
        ChatFontRoleWeight nicknameWeight,
        string messageFamily,
        ChatFontRoleWeight messageWeight)
    {
        var typography = ChatSettings.ResolveTypography(preset);

        Assert.Equal(nicknameFamily, typography.NicknameFamilyName);
        Assert.Equal(nicknameWeight, typography.NicknameWeight);
        Assert.Equal(messageFamily, typography.MessageFamilyName);
        Assert.Equal(messageWeight, typography.MessageWeight);
    }

    [Fact]
    public void GtaLegacy_UsesBoldNicknameAndNormalMessage()
    {
        var typography = ChatSettings.ResolveTypography(ChatFontPreset.Cafe24ProSlim);

        Assert.Equal(ChatFontRoleWeight.Bold, typography.NicknameWeight);
        Assert.Equal(ChatFontRoleWeight.Normal, typography.MessageWeight);
    }

    [Fact]
    public void Modern_IsRecognizedAfterApplyingRecommendedDefaults()
    {
        var settings = ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            ChatStylePreset.Modern);

        Assert.Equal(ChatStylePreset.Modern, ChatStylePresets.Match(settings));
    }

    [Fact]
    public void ManualModification_ProducesCustomState()
    {
        var preset = ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            ChatStylePreset.HighReadability);

        var custom = preset with { ChatFontSizePoints = preset.ChatFontSizePoints + 1 };

        Assert.Null(ChatStylePresets.Match(custom));
    }

    [Fact]
    public void FontOnlyChange_DoesNotReapplySizeOrTheme()
    {
        var custom = AppSettings.CreateDefault() with
        {
            ChatFontSizePoints = 19,
            ColorTheme = ColorThemeId.TokyoNight,
        };

        var changed = custom with { ChatFontPreset = ChatFontPreset.Cafe24ProSlim };

        Assert.Equal(19, changed.ChatFontSizePoints);
        Assert.Equal(ColorThemeId.TokyoNight, changed.ColorTheme);
    }

    [Fact]
    public void ApplyingPreset_DoesNotLockLaterManualValues()
    {
        var preset = ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            ChatStylePreset.GtaLegacy);

        var edited = preset with
        {
            ChatMaxLines = 3,
        };

        Assert.Equal(3, edited.ChatMaxLines);
        Assert.Null(ChatStylePresets.Match(edited));
    }

    [Theory]
    [InlineData(ChatFontPreset.Kimm, true)]
    [InlineData(ChatFontPreset.Pretendard, true)]
    [InlineData(ChatFontPreset.WantedSans, true)]
    [InlineData(ChatFontPreset.Cafe24ProSlim, true)]
    public void Typography_RecordsWhetherFontCanBeBundled(
        ChatFontPreset preset,
        bool expectedBundled)
    {
        Assert.Equal(expectedBundled, ChatSettings.ResolveTypography(preset).IsBundled);
    }

    [Fact]
    public void HighReadability_KeepsReadableSpacingAndOutline()
    {
        var settings = ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            ChatStylePreset.HighReadability);

        Assert.True(settings.ChatLineHeightMultiplier >= 1.5);
        Assert.True(settings.ChatMessageOutlineEnabled);
    }

    [Theory]
    [InlineData(ChatStylePreset.Clean)]
    [InlineData(ChatStylePreset.Modern)]
    [InlineData(ChatStylePreset.HighReadability)]
    [InlineData(ChatStylePreset.GtaLegacy)]
    public void ApplyingTypographyPreset_DoesNotChangeColorTheme(ChatStylePreset preset)
    {
        var settings = AppSettings.CreateDefault() with { ColorTheme = ColorThemeId.Monokai };

        var applied = ChatStylePresets.Apply(settings, preset);

        Assert.Equal(ColorThemeId.Monokai, applied.ColorTheme);
    }
}
