using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Presentation;

public sealed class M754QueueDetailLockPersistenceTests
{
    [Fact]
    public void UnlockedExpanded_Lock_PreservesExpandedState()
    {
        var viewModel = ExpandedViewModel();

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        Assert.True(viewModel.IsQueueDetailExpanded);
        Assert.True(viewModel.IsQueueDetailPanelVisible);
    }

    [Fact]
    public void LockedExpanded_IsNotInteractive()
    {
        var viewModel = ExpandedViewModel();

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        Assert.False(viewModel.IsQueueDetailInteractive);
        Assert.False(viewModel.ToggleDetailCommand.CanExecute(null));
    }

    [Fact]
    public void LockedExpanded_Unlock_PreservesExpandedState()
    {
        var viewModel = ExpandedViewModel();
        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);

        Assert.True(viewModel.IsQueueDetailExpanded);
        Assert.True(viewModel.IsQueueDetailPanelVisible);
    }

    [Fact]
    public void UnlockAfterLock_RestoresInteraction()
    {
        var viewModel = ExpandedViewModel();
        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);

        Assert.True(viewModel.IsQueueDetailInteractive);
        Assert.True(viewModel.ToggleDetailCommand.CanExecute(null));
    }

    [Fact]
    public void SalesTrackingOff_StillCollapsesDetail()
    {
        var viewModel = ExpandedViewModel();

        viewModel.Apply(
            QueueSnapshot(),
            AppSettings.CreateDefault() with { SalesTrackingEnabled = false },
            SalesFeatureHealthSnapshot.Disabled,
            "#sales",
            SalesQueueChangeContext.None);

        Assert.False(viewModel.IsQueueDetailExpanded);
        Assert.False(viewModel.IsQueueDetailAvailable);
    }

    [Fact]
    public void QueueEmpty_StillCollapsesDetail()
    {
        var viewModel = ExpandedViewModel();

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

        Assert.False(viewModel.IsQueueDetailExpanded);
        Assert.False(viewModel.IsQueueDetailAvailable);
    }

    [Fact]
    public void UltraCompact_StillCollapsesDetail()
    {
        var viewModel = ExpandedViewModel();

        viewModel.UpdateHudContext(true, true, true, isHudUnlocked: true);

        Assert.False(viewModel.IsQueueDetailExpanded);
        Assert.False(viewModel.IsQueueDetailAvailable);
    }

    [Fact]
    public void NewSession_DefaultsToCollapsed()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsQueueDetailExpanded);
        Assert.False(viewModel.IsQueueDetailPanelVisible);
    }

    [Fact]
    public void Lock_KeepsGlobalClickThroughDerivedFromHudState()
    {
        var state = new HudStateService();
        state.SetLocked(false);
        Assert.False(state.Current.IsClickThrough);

        state.SetLocked(true);

        Assert.True(state.Current.IsLocked);
        Assert.True(state.Current.IsClickThrough);
    }

    [Fact]
    public void LockUnlock_PreservesQueueOrderAndContent()
    {
        var viewModel = ExpandedViewModel();
        var before = DetailContent(viewModel);

        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: false);
        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);

        Assert.Equal(before, DetailContent(viewModel));
    }

    [Fact]
    public void LockedDetail_XamlDisablesAllDetailHitTestingWithoutHidingIt()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "GachaOverlay.App",
            "Presentation",
            "SalesQueueView.xaml")));

        Assert.Contains(
            "Visibility=\"{Binding IsQueueDetailPanelVisible, Converter={StaticResource BoolVisibility}}\"",
            source);
        Assert.Contains("IsHitTestVisible=\"{Binding IsQueueDetailInteractive}\"", source);
        Assert.Contains("IsEnabled=\"{Binding IsQueueDetailInteractive}\"", source);
    }

    private static SalesQueueViewModel ExpandedViewModel()
    {
        var viewModel = CreateViewModel();
        viewModel.ToggleDetailCommand.Execute(null);
        Assert.True(viewModel.IsQueueDetailExpanded);
        return viewModel;
    }

    private static SalesQueueViewModel CreateViewModel()
    {
        var viewModel = new SalesQueueViewModel(
            new ResourceLocalizationService(SupportedLocales.English));
        viewModel.UpdateHudContext(true, false, true, isHudUnlocked: true);
        viewModel.Apply(
            QueueSnapshot(),
            AppSettings.CreateDefault(),
            LiveHealth(3),
            "#sales",
            SalesQueueChangeContext.None);
        return viewModel;
    }

    private static SalesQueueSnapshot QueueSnapshot()
    {
        var entries = new[]
        {
            Entry("1", "Current"),
            Entry("2", "Next"),
            Entry("3", "Third"),
        };
        return new SalesQueueSnapshot(
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
            "user-3");
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

    private static string[] DetailContent(SalesQueueViewModel viewModel) =>
        viewModel.DetailItems
            .Select(item => $"{item.Position}|{item.DisplayName}|{item.ProductName}")
            .ToArray();
}
