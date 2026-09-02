using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Presentation;

public sealed class M97SalesStatusUiTests
{
    [Fact]
    public void Buttons_AreOrderedNegotiatingSellingCompletedClear()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "SalesQueueView.xaml"));
        var negotiating = xaml.IndexOf("SetNegotiatingCommand", StringComparison.Ordinal);
        var selling = xaml.IndexOf("SetSellingCommand", StringComparison.Ordinal);
        var completed = xaml.IndexOf("SetCompletedCommand", StringComparison.Ordinal);
        var clear = xaml.IndexOf("ClearStatusCommand", StringComparison.Ordinal);

        Assert.True(negotiating >= 0);
        Assert.True(negotiating < selling);
        Assert.True(selling < completed);
        Assert.True(completed < clear);
    }

    [Fact]
    public void Controls_AppearOnlyForSelfAndRequireRemotePrimaryLiveUnlocked()
    {
        var viewModel = CreateViewModel(out var snapshot);

        Assert.True(viewModel.DetailItems[0].IsStatusActionVisible);
        Assert.True(viewModel.DetailItems[0].IsStatusActionEnabled);
        Assert.False(viewModel.DetailItems[1].IsStatusActionVisible);
        Assert.False(viewModel.DetailItems[1].IsStatusActionEnabled);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        Assert.False(viewModel.DetailItems[0].IsStatusActionEnabled);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);
        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation>(),
            EffectiveSalesSource.RemoteRecovering);
        Apply(viewModel, snapshot);

        Assert.False(viewModel.DetailItems[0].IsStatusActionEnabled);
    }

    [Fact]
    public void HumanManualSoldMarker_IsNotPresentedAsBotOwnedStatus()
    {
        var viewModel = CreateViewModel(out var snapshot);
        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation>
            {
                ["30"] = Observation(sold: true),
            },
            EffectiveSalesSource.RemotePrimary);
        Apply(viewModel, snapshot);

        Assert.Equal("No Bot status", viewModel.DetailItems[0].StatusText);
    }

    [Fact]
    public async Task AcceptedResponse_RemainsPendingUntilOfficialEvidenceMatches()
    {
        var viewModel = CreateViewModel(out var snapshot);
        var response = new TaskCompletionSource<SalesStatusActionResponse?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ConfigureStatusAction((messageId, status, cancellationToken) =>
        {
            Assert.Equal(30UL, messageId);
            Assert.Equal(SalesStatus.Selling, status);
            return response.Task;
        });
        Apply(viewModel, snapshot);

        var action = viewModel.ExecuteStatusActionAsync("30", SalesStatus.Selling);
        Assert.Equal("Confirming with Discord…", viewModel.DetailItems[0].StatusText);
        response.SetResult(new SalesStatusActionResponse(
            OverlayTransportProtocol.Version,
            Guid.NewGuid(),
            SalesStatusActionDisposition.Accepted,
            true));
        await Task.Delay(20);

        Assert.False(action.IsCompleted);
        Assert.Equal("Confirming with Discord…", viewModel.DetailItems[0].StatusText);

        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation>
            {
                ["30"] = Observation(botSelling: true),
            },
            EffectiveSalesSource.RemotePrimary);
        Apply(viewModel, snapshot);
        await action;

        Assert.Equal("Selling", viewModel.DetailItems[0].StatusText);
        Assert.Equal("30", snapshot.ActiveItems[0].MessageId);
    }

    [Fact]
    public void BotCompletedOwnMessage_RemainsAvailableForBotStatusClear()
    {
        var viewModel = CreateViewModel(out var original);
        var other = original.ActiveItems[1];
        var afterCompleted = original with
        {
            ActiveItems = new[] { other },
            CurrentSeller = other,
            ActiveCount = 1,
            WaitingCount = 0,
            NextWaitingEntry = null,
            CurrentSellerIsSelf = false,
        };
        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation>
            {
                ["30"] = new SalesCompletionObservation(
                    30,
                    true,
                    false,
                    SalesEvidenceCoverage.Complete,
                    DateTimeOffset.UtcNow,
                    false,
                    false,
                    true),
            },
            EffectiveSalesSource.RemotePrimary,
            new[]
            {
                new SalesStatusActionTarget("30", "Self", string.Empty, true),
            });
        Apply(viewModel, afterCompleted);

        var completed = Assert.Single(
            viewModel.DetailItems.Where(item => item.MessageId == "30"));
        Assert.True(completed.IsStatusActionVisible);
        Assert.True(completed.IsStatusActionEnabled);
        Assert.Equal("Sold", completed.StatusText);
    }

    private static SalesQueueViewModel CreateViewModel(out SalesQueueSnapshot snapshot)
    {
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        var entries = new[]
        {
            Entry("30", "10", "Self"),
            Entry("31", "11", "Other"),
        };
        snapshot = new SalesQueueSnapshot(
            1,
            true,
            entries,
            entries[0],
            2,
            1,
            entries[1],
            true,
            false,
            false,
            true,
            SalesObservationStatus.Live,
            DateTimeOffset.UtcNow,
            "10");
        var viewModel = new SalesQueueViewModel(localization);
        viewModel.ConfigureStatusAction((messageId, status, cancellationToken) =>
            Task.FromResult<SalesStatusActionResponse?>(null));
        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);
        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation>(),
            EffectiveSalesSource.RemotePrimary);
        Apply(viewModel, snapshot);
        return viewModel;
    }

    private static void Apply(
        SalesQueueViewModel viewModel,
        SalesQueueSnapshot snapshot) => viewModel.Apply(
            snapshot,
            AppSettings.CreateDefault() with
            {
                SalesTrackingEnabled = true,
            },
            new SalesFeatureHealthSnapshot(
                SalesFeatureHealthState.Live,
                SalesFeatureHealthReason.None,
                SalesObservationReason.None,
                SalesObservationStatus.Live,
                SalesCoverageState.Complete,
                true,
                DateTimeOffset.UtcNow,
                snapshot.ActiveCount,
                snapshot.ActiveCount,
                EffectiveSalesSource.RemotePrimary,
                RemoteSalesPresentationPhase.Live),
            "#sales",
            SalesQueueChangeContext.None);

    private static SalesQueueEntry Entry(string id, string authorId, string name) => new(
        id,
        "guild",
        authorId,
        DateTimeOffset.UtcNow,
        name,
        DiscordDisplayNameSource.GuildNickname,
        true,
        null,
        SaleObservationTrust.Trusted);

    private static SalesCompletionObservation Observation(
        bool sold = false,
        bool botSelling = false) => new(
            30,
            sold,
            false,
            SalesEvidenceCoverage.Complete,
            DateTimeOffset.UtcNow,
            botSelling,
            false,
            false);
}
