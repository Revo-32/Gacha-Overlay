using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

public sealed class M81OnboardingViewModelTests
{
    [Fact]
    public async Task ServerSelector_LatePreviousResultCannotOverrideNewerSelection()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        Assert.True(store.Update(settings => settings with
        {
            DiscordMainChannelId = "original",
        }));
        var localization = new ResourceLocalizationService("en");
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<MainChannelSwitchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new ServerSettingsViewModel(
            store,
            localization,
            (_, _) => Task.FromResult(new DiscordServerDiscoverySnapshot(
                DiscordServerDiscoveryState.Ready,
                "Production Guild",
                ProductionServerProfile.SalesChannelName,
                [
                    new DiscordMainChannelOption("original", "Original"),
                    new DiscordMainChannelOption("b", "B"),
                    new DiscordMainChannelOption("c", "C"),
                ],
                1)),
            async (channel, _) =>
            {
                if (channel.ChannelId == "b")
                {
                    bStarted.SetResult();
                    var result = await releaseB.Task;
                    bReturned.SetResult();
                    return result;
                }

                Assert.True(store.Update(current => current with
                {
                    DiscordMainChannelId = channel.ChannelId,
                }));
                return new MainChannelSwitchResult(
                    MainChannelSwitchStatus.Succeeded,
                    channel.ChannelId,
                    channel.Name);
            });
        await server.LoadAsync(forceRefresh: false);

        server.SelectedMainChannel = server.MainChannels.Single(channel => channel.ChannelId == "b");
        await bStarted.Task;
        server.SelectedMainChannel = server.MainChannels.Single(channel => channel.ChannelId == "c");
        releaseB.SetResult(new MainChannelSwitchResult(MainChannelSwitchStatus.Superseded));
        await bReturned.Task;
        await WaitUntilAsync(() => !server.IsBusy);

        Assert.Equal("c", server.SelectedMainChannel?.ChannelId);
        Assert.Equal("c", store.Current.DiscordMainChannelId);
        Assert.Equal(localization["SettingsServerSwitchSucceeded"], server.StatusText);
    }

    [Fact]
    public void Resume_WithClientIdButMissingProtectedSecret_StopsAtDiscordStep()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        Assert.True(store.Update(settings => settings with { DiscordClientId = "client-id" }));
        var localization = new ResourceLocalizationService("en");
        using var foundation = CreateFoundation(
            store,
            localization,
            ProtectedCredentialStatus.Missing,
            SalesFeatureHealthSnapshot.Disabled);
        using var onboarding = new OnboardingViewModel(
            foundation,
            store,
            localization,
            () => { },
            restartFromBeginning: false);

        Assert.Equal(1, onboarding.StepIndex);
        Assert.True(onboarding.IsDiscordStep);
    }

    [Fact]
    public void AccessibilityUnavailable_BlocksSalesOnButSalesOffCanContinueWithoutReset()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        Assert.True(store.Update(settings => settings with
        {
            DiscordClientId = "client-id",
            DiscordMainChannelId = "selected-main",
            SalesTrackingEnabled = true,
        }));
        var localization = new ResourceLocalizationService("en");
        var unavailable = SalesFeatureHealthSnapshot.Disabled with
        {
            State = SalesFeatureHealthState.Error,
            Reason = SalesFeatureHealthReason.AccessibilityUnavailable,
            SensorReason = SalesObservationReason.AccessibilityTreeUnavailable,
            SensorStatus = SalesObservationStatus.AccessibilityUnavailable,
        };
        using var foundation = CreateFoundation(
            store,
            localization,
            ProtectedCredentialStatus.Available,
            unavailable);
        using var onboarding = new OnboardingViewModel(
            foundation,
            store,
            localization,
            () => { },
            restartFromBeginning: false);

        Assert.Equal(4, onboarding.StepIndex);
        onboarding.NextCommand.Execute(null);
        Assert.Equal(4, onboarding.StepIndex);
        Assert.NotEmpty(onboarding.ValidationMessage);
        Assert.Equal("selected-main", store.Current.DiscordMainChannelId);

        foundation.SalesTrackingEnabled = false;
        onboarding.NextCommand.Execute(null);
        Assert.Equal(5, onboarding.StepIndex);
        Assert.Equal("selected-main", store.Current.DiscordMainChannelId);
    }

    [Fact]
    public void ReRun_StartsAtLanguageAndCompletionPreservesExistingSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        Assert.True(store.Update(settings => settings with
        {
            DiscordClientId = "client-id",
            DiscordMainChannelId = "selected-main",
            ChatShowTime = false,
            WindowsAutoStart = true,
        }));
        var localization = new ResourceLocalizationService("ko");
        using var foundation = CreateFoundation(
            store,
            localization,
            ProtectedCredentialStatus.Available,
            SalesFeatureHealthSnapshot.Disabled);
        var completed = false;
        using var onboarding = new OnboardingViewModel(
            foundation,
            store,
            localization,
            () => completed = true,
            restartFromBeginning: true);

        Assert.Equal(0, onboarding.StepIndex);
        for (var step = 0; step < OnboardingViewModel.StepCount - 1; step++)
        {
            onboarding.NextCommand.Execute(null);
        }

        onboarding.FinishCommand.Execute(null);

        Assert.True(completed);
        Assert.Equal(AppSettings.CurrentOnboardingVersion, store.Current.OnboardingVersion);
        Assert.Equal("selected-main", store.Current.DiscordMainChannelId);
        Assert.False(store.Current.ChatShowTime);
        Assert.True(store.Current.WindowsAutoStart);
    }

    private static FoundationViewModel CreateFoundation(
        ISettingsStore store,
        ResourceLocalizationService localization,
        ProtectedCredentialStatus clientSecretStatus,
        SalesFeatureHealthSnapshot salesHealth) =>
        new(
            store,
            localization,
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { },
            getDiscordSetupSnapshot: () => new DiscordConnectionSetupSnapshot(
                true,
                clientSecretStatus,
                ProtectedCredentialStatus.Available,
                true,
                !string.IsNullOrWhiteSpace(store.Current.DiscordMainChannelId),
                true),
            getSalesHealthSnapshot: () => salesHealth,
            discoverServer: (_, _) => Task.FromResult(new DiscordServerDiscoverySnapshot(
                DiscordServerDiscoveryState.Ready,
                "Production Guild",
                ProductionServerProfile.SalesChannelName,
                [new DiscordMainChannelOption("selected-main", "🏠메인")],
                1)));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }
}
