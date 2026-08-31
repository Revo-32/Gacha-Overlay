using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class M6SalesStatusPresentationTests
{
    [Theory]
    [InlineData(SalesObservationStatus.Unavailable, "Sales status sensor is unavailable")]
    [InlineData(SalesObservationStatus.AccessibilityUnavailable, "Sales status sensor is unavailable")]
    [InlineData(SalesObservationStatus.Paused, "Keep #sales open")]
    [InlineData(SalesObservationStatus.Resyncing, "Resyncing sales status")]
    [InlineData(SalesObservationStatus.Partial, "Only part of the sales status is available")]
    [InlineData(SalesObservationStatus.Error, "Sales status sensor is unavailable")]
    public void NonLiveStatus_UsesNeutralDiagnosticText(
        SalesObservationStatus status,
        string expected)
    {
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        var viewModel = new SalesQueueViewModel(localization);
        viewModel.Apply(SalesQueueSnapshot.Empty with
        {
            IsTrackingEnabled = true,
            IsObservationSourceAvailable = status is not
                (SalesObservationStatus.Unavailable or
                SalesObservationStatus.AccessibilityUnavailable or
                SalesObservationStatus.Error),
            ObservationStatus = status,
        }, AppSettings.CreateDefault());
        Assert.Equal(expected, viewModel.PrimaryLine);
        Assert.DoesNotContain("LIVE", viewModel.PrimaryLine, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en", "Sales sensor paused")]
    [InlineData("ko", "판매 센서 일시 중지")]
    [InlineData("ja", "販売センサーを一時停止中")]
    public void PausedStatus_IsLocalized(string locale, string expected)
    {
        var localization = new ResourceLocalizationService(locale, NullAppLogger.Instance);
        Assert.Equal(expected, localization["SalesStatusPaused"]);
    }
}
