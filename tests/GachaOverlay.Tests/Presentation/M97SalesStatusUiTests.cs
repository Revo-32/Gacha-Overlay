using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;
using System.Xml.Linq;

namespace GachaOverlay.Tests.Presentation;

public sealed class M97SalesStatusUiTests
{
    [Fact]
    public void Controls_ExposeOnlyCompletionAfterRetiredEmojiRemoval()
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
        Assert.Contains("SetCompletedCommand", xaml, StringComparison.Ordinal);
        foreach (var retired in new[] { "SetNegotiatingCommand", "SetSellingCommand", "ClearStatusCommand" })
        {
            Assert.DoesNotContain(retired, xaml, StringComparison.Ordinal);
            Assert.Null(typeof(SalesQueueDetailItem).GetProperty(retired));
        }
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace markup = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Parse(xaml);
        Assert.Empty(document.Descendants(presentation + "DataTemplate").Single()
            .Descendants(presentation + "Button"));
        var bar = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(markup + "Name") == "QueueBar");
        Assert.Single(bar.Descendants(presentation + "Button")
            .Where(element => (string?)element.Attribute(markup + "Name") == "CompleteOwnSaleButton"));
    }

    [Fact]
    public void Controls_AppearOnlyForSelfAndRequireRemotePrimaryLiveUnlocked()
    {
        var viewModel = CreateViewModel(out var snapshot);

        Assert.True(viewModel.DetailItems[0].IsStatusActionVisible);
        Assert.True(viewModel.DetailItems[0].IsStatusActionEnabled);
        Assert.False(viewModel.DetailItems[1].IsStatusActionVisible);
        Assert.False(viewModel.DetailItems[1].IsStatusActionEnabled);
        Assert.True(viewModel.IsOwnCompletionVisible);
        Assert.Same(viewModel.DetailItems[0], viewModel.OwnCompletionItem);
        Assert.False(viewModel.IsQueueDetailExpanded);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        Assert.False(viewModel.DetailItems[0].IsStatusActionEnabled);
        Assert.True(viewModel.IsOwnCompletionVisible);

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

        Assert.Empty(viewModel.DetailItems[0].StatusText);
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
            Assert.Equal(SalesStatus.Completed, status);
            return response.Task;
        });
        Apply(viewModel, snapshot);

        var action = viewModel.ExecuteStatusActionAsync("30", SalesStatus.Completed);
        Assert.Equal("Confirming with Discord…", viewModel.DetailItems[0].StatusText);
        Assert.False(viewModel.DetailItems[0].SetCompletedCommand.CanExecute(null));
        Assert.True(viewModel.IsCompletionFeedbackVisible);
        Assert.Equal("Confirming with Discord…", viewModel.OwnCompletionItem!.StatusText);
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
                ["30"] = Observation(sold: true, botCompleted: true),
            },
            EffectiveSalesSource.RemotePrimary);
        Apply(viewModel, snapshot);
        await action;

        Assert.Equal("Sold", viewModel.DetailItems[0].StatusText);
        Assert.False(viewModel.OwnCompletionItem!.SetCompletedCommand.CanExecute(null));
        Assert.Equal("30", snapshot.ActiveItems[0].MessageId);
    }

    [Fact]
    public async Task MainBarCompletion_TargetsFirstOwnPostEvenWhenAnotherSellerIsCurrent()
    {
        var viewModel = CreateViewModel(out var original);
        var own = original.ActiveItems[0];
        var other = original.ActiveItems[1];
        var anotherOwn = Entry("32", "10", "Self");
        var snapshot = original with
        {
            ActiveItems = new[] { other, own, anotherOwn },
            CurrentSeller = other,
            NextWaitingEntry = own,
            CurrentSellerIsSelf = false,
            NextSellerIsSelf = true,
            ActiveCount = 3,
            WaitingCount = 2,
        };
        ulong? requested = null;
        viewModel.ConfigureStatusAction((id, status, _) =>
        {
            requested = id;
            Assert.Equal(SalesStatus.Completed, status);
            return Task.FromResult<SalesStatusActionResponse?>(null);
        });
        Apply(viewModel, snapshot);

        Assert.False(viewModel.IsQueueDetailExpanded);
        Assert.Equal("30", viewModel.OwnCompletionItem!.MessageId);
        Assert.Contains("#2", viewModel.OwnCompletionHint, StringComparison.Ordinal);
        viewModel.OwnCompletionItem.SetCompletedCommand.Execute(null);
        Assert.Equal(30UL, requested);
        await viewModel.ExecuteStatusActionAsync("31", SalesStatus.Completed);
        Assert.Equal(30UL, requested);
        Assert.Equal(new[] { "31", "30", "32" }, viewModel.DetailItems.Select(item => item.MessageId));
    }

    [Fact]
    public void NoOwnPost_HidesMainBarActionAndStaleCommandCannotSend()
    {
        var viewModel = CreateViewModel(out var original);
        var staleCommand = viewModel.OwnCompletionItem!.SetCompletedCommand;
        var calls = 0;
        viewModel.ConfigureStatusAction((_, _, _) =>
        {
            calls++;
            return Task.FromResult<SalesStatusActionResponse?>(null);
        });
        var other = original.ActiveItems[1];
        Apply(viewModel, original with
        {
            ActiveItems = new[] { other },
            CurrentSeller = other,
            NextWaitingEntry = null,
            CurrentSellerIsSelf = false,
            ActiveCount = 1,
            WaitingCount = 0,
        });

        Assert.Null(viewModel.OwnCompletionItem);
        Assert.False(viewModel.IsOwnCompletionVisible);
        Assert.False(viewModel.IsCompletionFeedbackVisible);
        staleCommand.Execute(null);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UltraCompactDoesNotRequireDetailExpansionForCompletion()
    {
        var viewModel = CreateViewModel(out _);
        viewModel.UpdateHudContext(true, true, true, true);

        Assert.False(viewModel.IsQueueDetailAvailable);
        Assert.True(viewModel.IsOwnCompletionVisible);
        Assert.True(viewModel.OwnCompletionItem!.SetCompletedCommand.CanExecute(null));
    }

    [Fact]
    public void IdentityMismatchDoesNotTrustStaleSelfPositionFlags()
    {
        var viewModel = CreateViewModel(out var snapshot);
        Apply(viewModel, snapshot with { AuthenticatedUserId = "someone-else" });

        Assert.False(viewModel.IsOwnCompletionVisible);
        Assert.All(viewModel.DetailItems, item => Assert.False(item.IsSelf));
    }

    [Theory]
    [InlineData(SalesStatus.Selling)]
    [InlineData(SalesStatus.Negotiating)]
    [InlineData(SalesStatus.Clear)]
    public async Task RetiredUiActions_DoNotDispatchOrChangeQueue(SalesStatus status)
    {
        var viewModel = CreateViewModel(out var snapshot);
        var calls = 0;
        viewModel.ConfigureStatusAction((_, _, _) =>
        {
            calls++;
            return Task.FromResult<SalesStatusActionResponse?>(null);
        });

        await viewModel.ExecuteStatusActionAsync("30", status);

        Assert.Equal(0, calls);
        Assert.Equal(snapshot.ActiveItems.Select(item => item.MessageId),
            viewModel.DetailItems.Select(item => item.MessageId));
        Assert.Empty(viewModel.DetailItems[0].StatusText);
        Assert.True(viewModel.DetailItems[0].SetCompletedCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(SalesStatusActionDisposition.RejectedUnavailable)]
    [InlineData(SalesStatusActionDisposition.Failed)]
    public async Task FailedCompletion_KeepsRowAndAllowsRetry(SalesStatusActionDisposition disposition)
    {
        var viewModel = CreateViewModel(out var snapshot);
        viewModel.ConfigureStatusAction((_, status, _) =>
        {
            Assert.Equal(SalesStatus.Completed, status);
            return Task.FromResult<SalesStatusActionResponse?>(new(
                OverlayTransportProtocol.Version, Guid.NewGuid(), disposition, false));
        });

        await viewModel.ExecuteStatusActionAsync("30", SalesStatus.Completed);

        Assert.Equal(snapshot.ActiveItems.Select(item => item.MessageId),
            viewModel.DetailItems.Select(item => item.MessageId));
        Assert.False(string.IsNullOrEmpty(viewModel.DetailItems[0].StatusText));
        Assert.True(viewModel.DetailItems[0].SetCompletedCommand.CanExecute(null));
    }

    [Fact]
    public void LegacyBotStatus_DoesNotAddRetiredStatusText()
    {
        var viewModel = CreateViewModel(out var snapshot);
        viewModel.ApplyRemoteStatusContext(
            new Dictionary<string, SalesCompletionObservation> { ["30"] = Observation(botSelling: true) },
            EffectiveSalesSource.RemotePrimary);
        Apply(viewModel, snapshot);

        Assert.Empty(viewModel.DetailItems[0].StatusText);
    }

    [Fact]
    public void BotCompletedOwnMessage_IsRemovedFromActiveQueueAfterCanonicalReadback()
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

        Assert.DoesNotContain(viewModel.DetailItems, item => item.MessageId == "30");
        Assert.Single(viewModel.DetailItems);
        Assert.Equal(other.MessageId, viewModel.DetailItems[0].MessageId);
        Assert.False(viewModel.IsOwnCompletionVisible);
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
        bool botSelling = false,
        bool botCompleted = false) => new(
            30,
            sold,
            false,
            SalesEvidenceCoverage.Complete,
            DateTimeOffset.UtcNow,
            botSelling,
            false,
            botCompleted);
}
