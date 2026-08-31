using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.App.Presentation;

namespace GachaOverlay.Tests.Presentation;

public sealed class M75SalesPreviewTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void Preview_ContainsEveryRequiredIsolatedScenario()
    {
        using var preview = new SalesPreviewViewModel(
            new ResourceLocalizationService(),
            AppSettings.CreateDefault());

        Assert.Equal(Enum.GetValues<SalesPreviewScenario>(), preview.Scenarios.Select(x => x.Value));
        Assert.Equal(14, preview.Scenarios.Count);
    }

    [Fact]
    public void LanguageChange_RefreshesScenarioLabelsAndProductionPresentationText()
    {
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        using var preview = new SalesPreviewViewModel(localization, AppSettings.CreateDefault());
        var englishLabel = preview.Scenarios[0].DisplayText;

        localization.SetLanguage(SupportedLocales.Korean);

        Assert.NotEqual(englishLabel, preview.Scenarios[0].DisplayText);
        Assert.Equal("일반", preview.Scenarios[0].DisplayText);
        Assert.DoesNotContain("Sales", preview.Sales.PrimaryLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "Normal", "Long names")]
    [InlineData(SupportedLocales.Korean, "일반", "긴 이름")]
    [InlineData(SupportedLocales.Japanese, "通常", "長い名前")]
    public void ScenarioOptions_ExposeOnlyLocalizedDisplayText(
        string locale,
        string first,
        string last)
    {
        using var preview = new SalesPreviewViewModel(
            new ResourceLocalizationService(locale),
            AppSettings.CreateDefault());

        Assert.Equal(first, preview.Scenarios.First().DisplayText);
        Assert.Equal(last, preview.Scenarios.Last().DisplayText);
        Assert.All(preview.Scenarios, option =>
        {
            Assert.Equal(option.DisplayText, option.ToString());
            Assert.DoesNotContain(nameof(SalesPreviewScenarioOption), option.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain('{', option.ToString());
        });
    }

    [Fact]
    public void ScenarioSelection_ImmediatelyRebuildsPreviewWithoutApplyCommand()
    {
        using var preview = new SalesPreviewViewModel(
            new ResourceLocalizationService(SupportedLocales.English),
            AppSettings.CreateDefault());

        preview.SelectedScenario = SalesPreviewScenario.Error;

        Assert.Equal(SalesFeatureHealthState.Error, preview.Sales.HealthState);
        Assert.Equal(SalesStatusIconKind.Error, preview.Sales.IconKind);
        Assert.DoesNotContain(
            "SettingsApplyScenario",
            File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "src",
                "GachaOverlay.App",
                "Presentation",
                "SalesPreviewWindow.xaml")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RapidScenarioSelection_LeavesOnlyLatestScenarioState()
    {
        using var preview = new SalesPreviewViewModel(
            new ResourceLocalizationService(),
            AppSettings.CreateDefault());

        preview.SelectedScenario = SalesPreviewScenario.Resyncing;
        preview.SelectedScenario = SalesPreviewScenario.Disconnected;
        preview.SelectedScenario = SalesPreviewScenario.Paused;

        Assert.Equal(SalesPreviewScenario.Paused, preview.SelectedScenario);
        Assert.Equal(SalesFeatureHealthState.Paused, preview.Sales.HealthState);
        Assert.Equal(SalesStatusIconKind.Warning, preview.Sales.IconKind);
    }

    [Fact]
    public void PreviewScenario_DoesNotMutateProductionSettingsObject()
    {
        var settings = AppSettings.CreateDefault() with { SalesTrackingEnabled = false };
        using var preview = new SalesPreviewViewModel(
            new ResourceLocalizationService(),
            settings);

        preview.SelectedScenario = SalesPreviewScenario.CurrentTurn;

        Assert.False(settings.SalesTrackingEnabled);
    }
}
