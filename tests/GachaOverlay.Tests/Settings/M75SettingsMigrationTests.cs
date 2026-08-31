using System.Text.Json;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Settings;

public sealed class M75SettingsMigrationTests
{
    [Fact]
    public void LegacyKoPubPreset_MigratesToWantedSansAndPersists()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 8, "chatFontPreset": 1 }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(ChatFontPreset.WantedSans, loaded.ChatFontPreset);
        Assert.Contains("\"chatFontPreset\": 4", File.ReadAllText(path));
    }

    [Fact]
    public void LegacySharedOutline_MigratesToBothIndependentValues()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 8, "chatOutlineThickness": 1.25 }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(1.25, loaded.ChatNicknameOutlineThickness);
        Assert.Equal(1.25, loaded.ChatMessageOutlineThickness);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(1.12, 1)]
    [InlineData(1.13, 1.25)]
    [InlineData(99, 10)]
    public void OutlineRange_ClampsAndSnapsToQuarterDip(double input, double expected) =>
        Assert.Equal(expected, ChatSettings.NormalizeOutlineThickness(input));

    [Fact]
    public void CategoryAndScrollPositions_RoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var settings = AppSettings.CreateDefault() with
        {
            LastSettingsCategory = SettingsCategory.Media,
            SettingsCategoryScrollPositions = new Dictionary<string, double>
            {
                [SettingsCategory.Chat.ToString()] = 318.5,
            },
        };

        Assert.True(new JsonSettingsStore(path).Save(settings));
        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(SettingsCategory.Media, loaded.LastSettingsCategory);
        Assert.Equal(318.5, loaded.SettingsCategoryScrollPositions["Chat"]);
    }

    [Fact]
    public void UnknownCategoryAndInvalidScroll_AreSafelyDiscarded()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 9,
              "lastSettingsCategory": 999,
              "settingsCategoryScrollPositions": { "Unknown": 12, "Hud": -4 }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(SettingsCategory.General, loaded.LastSettingsCategory);
        Assert.False(loaded.SettingsCategoryScrollPositions.ContainsKey("Unknown"));
        Assert.Equal(0, loaded.SettingsCategoryScrollPositions["Hud"]);
    }

    [Theory]
    [InlineData(ManualSalesResyncResult.Requested)]
    [InlineData(ManualSalesResyncResult.Coalesced)]
    [InlineData(ManualSalesResyncResult.TrackingDisabled)]
    [InlineData(ManualSalesResyncResult.DiscordDisconnected)]
    [InlineData(ManualSalesResyncResult.TargetChannelUnavailable)]
    public void ManualResyncResult_ProducesLocalizedActionableFeedback(
        ManualSalesResyncResult result)
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        var localization = new ResourceLocalizationService(SupportedLocales.Korean);
        using var viewModel = new FoundationViewModel(
            store,
            localization,
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { },
            manualSalesResync: () => result);

        viewModel.ManualSalesResyncCommand.Execute(null);

        Assert.False(string.IsNullOrWhiteSpace(viewModel.ManualSalesResyncStatusMessage));
        Assert.DoesNotContain("SettingsManualResync", viewModel.ManualSalesResyncStatusMessage);
    }

    [Fact]
    public void QueueDetailMaximumHeight_PersistsAfterNormalization()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        using var viewModel = new FoundationViewModel(
            store,
            new ResourceLocalizationService(),
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { });

        viewModel.SalesQueueDetailMaxHeight = 999;

        Assert.Equal(640, viewModel.SalesQueueDetailMaxHeight);
        Assert.Equal(640, new JsonSettingsStore(directory.File("settings.json")).Load()
            .SalesQueueDetailMaxHeight);
    }

    [Fact]
    public void ManualResyncCommand_IsDisabledWithGuidanceWhenSalesTrackingIsOff()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        using var viewModel = new FoundationViewModel(
            store,
            new ResourceLocalizationService(SupportedLocales.English),
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { });

        viewModel.SalesTrackingEnabled = false;

        Assert.False(viewModel.ManualSalesResyncCommand.CanExecute(null));
        Assert.Equal(
            "Turn on sales tracking before checking sales status.",
            viewModel.ManualSalesResyncStatusMessage);
    }
}
