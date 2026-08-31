using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Presentation;

public sealed class M75SalesQueueDetailTests
{
    [Fact]
    public void UnlockedLiveQueue_ExposesOrderedDetailRows()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.IsQueueDetailAvailable);
        Assert.True(viewModel.IsQueueDetailInteractive);
        Assert.Equal(new[] { "Current", "Next", "Third" },
            viewModel.DetailItems.Select(item => item.DisplayName));
        Assert.True(viewModel.DetailItems[0].IsCurrent);
        Assert.True(viewModel.DetailItems[0].IsExactGuildNickname);
    }

    [Fact]
    public void AuthenticatedUser_DeeperInQueue_IsMarkedAsSelf()
    {
        var viewModel = CreateViewModel("user-3");

        Assert.False(viewModel.DetailItems[0].IsSelf);
        Assert.False(viewModel.DetailItems[1].IsSelf);
        Assert.True(viewModel.DetailItems[2].IsSelf);
    }

    [Fact]
    public void Toggle_ExpandsAndLockPreservesVisibilityWhileDisablingInteraction()
    {
        var viewModel = CreateViewModel();

        viewModel.ToggleDetailCommand.Execute(null);
        Assert.True(viewModel.IsQueueDetailPanelVisible);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);
        Assert.True(viewModel.IsQueueDetailExpanded);
        Assert.True(viewModel.IsQueueDetailPanelVisible);
        Assert.False(viewModel.IsQueueDetailInteractive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void HiddenOrUltraCompact_DisablesDetail(bool hudVisible, bool ultraCompact)
    {
        var viewModel = CreateViewModel();

        viewModel.UpdateHudContext(hudVisible, ultraCompact, true, true);

        Assert.False(viewModel.IsQueueDetailAvailable);
        Assert.False(viewModel.IsQueueDetailPanelVisible);
    }

    [Fact]
    public void EmptyQueue_CannotExpand()
    {
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        var viewModel = new SalesQueueViewModel(localization);
        viewModel.Apply(
            SalesQueueSnapshot.Empty with
            {
                IsObservationSourceAvailable = true,
                ObservationStatus = SalesObservationStatus.Live,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            AppSettings.CreateDefault(),
            LiveHealth(0),
            "#sales",
            SalesQueueChangeContext.None);

        Assert.False(viewModel.IsQueueDetailAvailable);
    }

    private static SalesQueueViewModel CreateViewModel(string? authenticatedUserId = null)
    {
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        var entries = new[]
        {
            Entry("1", "Current"),
            Entry("2", "Next"),
            Entry("3", "Third"),
        };
        var snapshot = new SalesQueueSnapshot(
            1,
            true,
            entries,
            entries[0],
            3,
            2,
            entries[1],
            false,
            false,
            false,
            true,
            SalesObservationStatus.Live,
            DateTimeOffset.UtcNow,
            authenticatedUserId);
        var viewModel = new SalesQueueViewModel(localization);
        viewModel.UpdateHudContext(true, false, true, true);
        viewModel.Apply(
            snapshot,
            AppSettings.CreateDefault(),
            LiveHealth(3),
            "#sales",
            SalesQueueChangeContext.None);
        return viewModel;
    }

    private static SalesQueueEntry Entry(string id, string name) => new(
        id,
        "guild",
        $"user-{id}",
        DateTimeOffset.UtcNow.AddMinutes(int.Parse(id)),
        name,
        DiscordDisplayNameSource.GuildNickname,
        true,
        new SaleProduct($"product-{id}", $"Product {id}", $"emoji-{id}", $"emoji_{id}"),
        SaleObservationTrust.Trusted);

    private static SalesFeatureHealthSnapshot LiveHealth(int count) => new(
        SalesFeatureHealthState.Live,
        SalesFeatureHealthReason.None,
        SalesObservationReason.None,
        SalesObservationStatus.Live,
        SalesCoverageState.Complete,
        true,
        DateTimeOffset.UtcNow,
        count,
        count);
}
