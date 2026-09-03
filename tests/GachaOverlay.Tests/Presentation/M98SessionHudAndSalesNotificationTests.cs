using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using System.Text.Json;
using System.Windows.Threading;

namespace GachaOverlay.Tests.Presentation;

public sealed class M98SessionHudAndSalesNotificationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionBootstrap_ShowsStructuredPartyImmediately()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));

        Assert.True(viewModel.IsVisible);
        var item = Assert.Single(viewModel.Items);
        Assert.Empty(item.Label);
        Assert.Equal("11 / 30", item.Value);
        Assert.DoesNotContain("132987", item.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionHosts_DisplayOnlyExplicitlySelectedSlot()
    {
        var viewModel = Session();
        var hosts = Enumerable.Range(1, 20)
            .Reverse()
            .Select(slot => Host(slot, HostPresenceState.GtaOnline, slot, 32))
            .ToArray();

        viewModel.ApplyBootstrap(Bootstrap(hosts));

        var item = Assert.Single(viewModel.Items);
        Assert.Equal(1, item.HostSlot);
        Assert.Empty(item.Label);
    }

    [Theory]
    [InlineData(-1, 32)]
    [InlineData(33, 32)]
    [InlineData(1, 0)]
    public void InvalidStructuredParty_NeverDisplaysClampedOccupancy(int current, int maximum)
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.GtaOnline, current, maximum)));

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void AuthoritativeOffline_ClearsPreviouslyVisibleCount()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));

        viewModel.ApplyPresence(Host(1, HostPresenceState.Offline, null, null));

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void ZeroCurrent_IsAValidStructuredPartyOccupancy()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 0, 32)));

        var item = Assert.Single(viewModel.Items);
        Assert.True(item.IsAvailable);
        Assert.Equal("0 / 30", item.Value);
    }

    [Fact]
    public void ActivityRemoval_ClearsPreviouslyVisibleCount()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));

        viewModel.ApplyPresence(Host(
            1,
            HostPresenceState.OnlineButNotGtaOnline,
            null,
            null));

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void OccupancyChange_ProducesOnePresentationReplacement()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));
        var changes = 0;
        viewModel.Items.CollectionChanged += (_, _) => changes++;

        viewModel.ApplyPresence(Host(1, HostPresenceState.GtaOnline, 12, 32));

        Assert.Equal(1, changes);
        Assert.Equal("12 / 30", Assert.Single(viewModel.Items).Value);
    }

    [Fact]
    public void Reconnect_IsTransientAndCanonicalBootstrapRestoresWithoutFakeOffline()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));

        viewModel.UpdateRemoteState(true, SessionRemoteState.Reconnecting);
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);

        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 12, 32)));
        viewModel.UpdateRemoteState(true, SessionRemoteState.Live);
        Assert.Equal("12 / 30", Assert.Single(viewModel.Items).Value);
    }

    [Fact]
    public void CanonicalOfflineAfterReconnect_RemainsAuthoritativeOffline()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));
        viewModel.UpdateRemoteState(true, SessionRemoteState.Reconnecting);

        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.Offline, null, null)));
        viewModel.UpdateRemoteState(true, SessionRemoteState.Live);

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void CanonicalBootstrap_RemovesDeletedHostSlotAndStaleCount()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.GtaOnline, 11, 32),
            Host(2, HostPresenceState.GtaOnline, 8, 32)));

        viewModel.ApplyBootstrap(Bootstrap(Host(2, HostPresenceState.Offline, null, null)));

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void Host2Selection_UsesOnlyHost2Snapshot()
    {
        var viewModel = Session(AppSettings.CreateDefault() with
        {
            SelectedSessionHost = SessionHostSelection.Host2,
        });
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.GtaOnline, 11, 32),
            Host(2, HostPresenceState.GtaOnline, 20, 32)));

        var item = Assert.Single(viewModel.Items);
        Assert.Equal(2, item.HostSlot);
        Assert.Empty(item.Label);
        Assert.Equal("20 / 30", item.Value);
    }

    [Fact]
    public void SelectedOfflineHost_NeverFallsBackToOnlineOtherHost()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.Offline, null, null),
            Host(2, HostPresenceState.GtaOnline, 20, 32)));

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void SwitchingHost_UsesCachedSnapshotWithoutRemoteRestart()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.GtaOnline, 11, 32),
            Host(2, HostPresenceState.GtaOnline, 20, 32)));

        viewModel.ApplySettings(AppSettings.CreateDefault() with
        {
            SelectedSessionHost = SessionHostSelection.Host2,
        });

        var item = Assert.Single(viewModel.Items);
        Assert.Equal(2, item.HostSlot);
        Assert.Equal("20 / 30", item.Value);
    }

    [Fact]
    public void SeparateClients_CanSelectDifferentHostSlots()
    {
        var host1Client = Session();
        var host2Client = Session(AppSettings.CreateDefault() with
        {
            SelectedSessionHost = SessionHostSelection.Host2,
        });
        var bootstrap = Bootstrap(
            Host(1, HostPresenceState.GtaOnline, 11, 32),
            Host(2, HostPresenceState.GtaOnline, 20, 32));

        host1Client.ApplyBootstrap(bootstrap);
        host2Client.ApplyBootstrap(bootstrap);

        Assert.Equal(1, Assert.Single(host1Client.Items).HostSlot);
        Assert.Equal(2, Assert.Single(host2Client.Items).HostSlot);
    }

    [Fact]
    public void SessionHostSelection_HasOnlyHost1AndHost2()
    {
        Assert.Equal(
            new[] { "Host1", "Host2" },
            Enum.GetNames<SessionHostSelection>());
        Assert.Equal(SessionHostSelection.Host1,
            AppSettings.CreateDefault().SelectedSessionHost);
    }

    [Fact]
    public void SettingsHostSelector_OffersExactlyTwoSlotsAndAppliesLocally()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var applied = new List<AppSettings>();
            using var viewModel = new FoundationViewModel(
                store,
                new ResourceLocalizationService("en"),
                NullAppLogger.Instance,
                new ChatTypographyResolver(NullAppLogger.Instance),
                () => { },
                settings => applied.Add(settings),
                () => { });

            Assert.Equal(
                new[] { SessionHostSelection.Host1, SessionHostSelection.Host2 },
                viewModel.SessionHostOptions.Select(option => option.Value).ToArray());
            Assert.Equal(
                new[] { "DE-SSANTA", "-TheFirstStar-" },
                viewModel.SessionHostOptions.Select(option => option.DisplayText).ToArray());
            viewModel.SelectedSessionHost = SessionHostSelection.Host2;

            Assert.Single(applied);
            Assert.Equal(SessionHostSelection.Host2, applied[0].SelectedSessionHost);
            Assert.Equal(SessionHostSelection.Host2, store.Current.SelectedSessionHost);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MinimalMode_ShowsOnlyValidCompactOccupancy()
    {
        var viewModel = Session(AppSettings.CreateDefault() with { MinimalHudMode = true });
        viewModel.ApplyBootstrap(Bootstrap(
            Host(1, HostPresenceState.GtaOnline, 11, 32),
            Host(2, HostPresenceState.Offline, null, null)));

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("11 / 30", item.Value);
        Assert.False(item.IsLabelVisible);

        viewModel.ApplyPresence(Host(1, HostPresenceState.Offline, null, null));
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void RepeatedEquivalentOccupancy_DoesNotChurnCollection()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 12, 32)));
        var changes = 0;
        viewModel.Items.CollectionChanged += (_, _) => changes++;

        viewModel.ApplyPresence(Host(
            1,
            HostPresenceState.GtaOnline,
            12,
            32,
            Now.AddSeconds(1)));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void SessionToggleAndLegacyMode_HideRemoteOnlyPresentation()
    {
        var viewModel = Session();
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 12, 32)));
        viewModel.ApplySettings(AppSettings.CreateDefault() with { ShowGtaSession = false });
        Assert.False(viewModel.IsVisible);

        viewModel.ApplySettings(AppSettings.CreateDefault());
        viewModel.UpdateRemoteState(false, SessionRemoteState.Unavailable);
        Assert.False(viewModel.IsVisible);
    }

    [Theory]
    [InlineData("en", "Full Session")]
    [InlineData("ko", "풀세션")]
    [InlineData("ja", "満員")]
    public void FullSessionStatus_IsLocalized(string locale, string expected)
    {
        var viewModel = new SessionHudViewModel(
            new ResourceLocalizationService(locale),
            AppSettings.CreateDefault());
        viewModel.UpdateRemoteState(true, SessionRemoteState.Live);
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 31, 32)));
        Assert.Equal(expected, Assert.Single(viewModel.Items).Value);
    }

    [Theory]
    [InlineData(ChatLayoutMode.Balanced, false)]
    [InlineData(ChatLayoutMode.Compact, false)]
    [InlineData(ChatLayoutMode.Balanced, true)]
    public void SessionHud_LayoutModesRemainCompactAndStable(
        ChatLayoutMode layout,
        bool ultraCompact)
    {
        var settings = AppSettings.CreateDefault() with { ChatLayoutMode = layout };
        var viewModel = Session(settings);
        viewModel.UpdateLayout(ultraCompact);
        viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("11 / 30", item.Value);
        Assert.True(viewModel.IsCompactDisplay);
        Assert.False(item.IsLabelVisible);
    }

    [Fact]
    public void SessionHud_UsesThemeResourcesAndNeverInterceptsInput()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "HudWindow.xaml"));
        var sessionStart = xaml.IndexOf("x:Name=\"SessionBadge\"", StringComparison.Ordinal);
        var sessionEnd = xaml.IndexOf("</Border>", sessionStart, StringComparison.Ordinal);
        var sessionMarkup = xaml[sessionStart..sessionEnd];

        Assert.Contains("IsHitTestVisible=\"False\"", sessionMarkup, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource TextPrimaryBrush}", sessionMarkup, StringComparison.Ordinal);

        foreach (var theme in ColorThemeCatalog.All)
        {
            var viewModel = Session(AppSettings.CreateDefault() with { ColorTheme = theme.Id });
            viewModel.ApplyBootstrap(Bootstrap(Host(1, HostPresenceState.GtaOnline, 11, 32)));
            Assert.Equal("11 / 30", Assert.Single(viewModel.Items).Value);
        }
    }

    [Fact]
    public void ValidationHelper_PromptsForTwoExplicitHostSlots()
    {
        var wrapper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m98-local.ps1"));

        Assert.Contains("-not $PSBoundParameters.ContainsKey('SessionHost1Id')", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("GTA Session Host 1 Discord User ID", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("GTA Session Host 2 Discord User ID", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("$helperArguments.SessionHost1Id = $SessionHost1Id", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("$helperArguments.SessionHost2Id = $SessionHost2Id", wrapper,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TrackedHostIds", wrapper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InitialCurrentPosition_IsSilent()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);
        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void InitialNextPosition_IsSilent()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.NextTurnSelf), false);
        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void WaitingToNextToCurrent_PlaysExactlyOneConfiguredSoundEach()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.NextTurnSelf), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.NextTurnSelf), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);

        Assert.Equal(
            new[] { SalesTurnNotificationKind.Next, SalesTurnNotificationKind.Current },
            fixture.Sound.Played.Select(item => item.Kind));
    }

    [Fact]
    public void ReconnectAndProviderHandoff_AreSilentAndRefreshBaseline()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.Observe(
            Presentation(SalesQueueContentMode.Normal, trustworthy: false),
            false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), true);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);

        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void NewCurrentAfterAuthoritativeQueueAdvance_PlaysOnce()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);

        var played = Assert.Single(fixture.Sound.Played);
        Assert.Equal(SalesTurnNotificationKind.Current, played.Kind);
        Assert.Equal(50, played.Volume);
    }

    [Fact]
    public void CurrentToWaiting_DoesNotPlayAReverseNotification()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);

        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void AuthenticationRefresh_ReestablishesSilentStartupBaseline()
    {
        var fixture = Notification();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.ResetBaseline();
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);

        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void NotifyCurrentOff_SuppressesDirectCurrentTransition()
    {
        var fixture = Notification(AppSettings.CreateDefault() with
        {
            NotifySalesCurrent = false,
        });
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.CurrentTurnSelf), false);

        Assert.Empty(fixture.Sound.Played);
    }

    [Theory]
    [InlineData(false, true, true, 50)]
    [InlineData(true, false, true, 50)]
    [InlineData(true, true, true, 0)]
    public void SoundSettings_AreRespected(
        bool enabled,
        bool notifyNext,
        bool notifyCurrent,
        double volume)
    {
        var settings = AppSettings.CreateDefault() with
        {
            SalesTurnSoundEnabled = enabled,
            NotifySalesNext = notifyNext,
            NotifySalesCurrent = notifyCurrent,
            SalesTurnSoundVolume = volume,
        };
        var fixture = Notification(settings);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.Normal), false);
        fixture.Coordinator.Observe(Presentation(SalesQueueContentMode.NextTurnSelf), false);

        Assert.Empty(fixture.Sound.Played);
    }

    [Fact]
    public void OriginalSyntheticWav_IsSmallValidAndDistinct()
    {
        var next = SalesNotificationTone.CreateWave(SalesTurnNotificationKind.Next);
        var current = SalesNotificationTone.CreateWave(SalesTurnNotificationKind.Current);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(next, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(next, 8, 4));
        Assert.InRange(next.Length, 45, 64 * 1024);
        Assert.InRange(current.Length, 45, 64 * 1024);
        Assert.False(next.SequenceEqual(current));
    }

    [Fact]
    public async Task AudioPreparationFailure_IsContainedAndLoggedOnlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Dispatcher? dispatcher = null;
        using var dispatcherReady = new ManualResetEventSlim(false);
        var dispatcherThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.Set();
            Dispatcher.Run();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Assert.True(dispatcherReady.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var logger = new RecordingLogger();
            var factoryCalls = 0;
            var service = new SalesNotificationSoundService(
                dispatcher!,
                directory,
                logger,
                _ =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    throw new IOException("Synthetic playback preparation failure.");
                });

            service.Play(SalesTurnNotificationKind.Next, 50);
            var firstAttempt = service.LastPlaybackTask;
            await firstAttempt;
            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, logger.WarningCount);
            service.Play(SalesTurnNotificationKind.Current, 50);
            await service.LastPlaybackTask;

            Assert.Equal(1, logger.WarningCount);
            service.Dispose();
        }
        finally
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(2)));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisposedAudioService_IgnoresLatePlaybackRequests()
    {
        var service = new SalesNotificationSoundService(
            Dispatcher.CurrentDispatcher,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            NullAppLogger.Instance);

        service.Dispose();
        service.Dispose();
        service.Play(SalesTurnNotificationKind.Current, 50);
    }

    [Fact]
    public void Schema15_PersistsSessionHostAndClampsInvalidVolume()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            Assert.True(store.Save(AppSettings.CreateDefault() with
            {
                ShowGtaSession = false,
                SelectedSessionHost = SessionHostSelection.Host2,
                SalesTurnSoundEnabled = false,
                SalesTurnSoundVolume = 150,
                NotifySalesNext = false,
                NotifySalesCurrent = true,
            }));

            var loaded = new JsonSettingsStore(path).Load();
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.False(loaded.ShowGtaSession);
            Assert.Equal(SessionHostSelection.Host2, loaded.SelectedSessionHost);
            Assert.False(loaded.SalesTurnSoundEnabled);
            Assert.Equal(100, loaded.SalesTurnSoundVolume);
            Assert.False(loaded.NotifySalesNext);
            Assert.True(loaded.NotifySalesCurrent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Schema14Migration_DefaultsHost1AndPreservesUnknownFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 14,
                  "Language": "en",
                  "SelectedSessionHost": "Both",
                  "SalesTurnSoundVolume": -25,
                  "FutureM99Setting": { "enabled": true }
                }
                """);

            var store = new JsonSettingsStore(path);
            var loaded = store.Load();

            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.True(loaded.ShowGtaSession);
            Assert.Equal(SessionHostSelection.Host1, loaded.SelectedSessionHost);
            Assert.True(loaded.SalesTurnSoundEnabled);
            Assert.Equal(0, loaded.SalesTurnSoundVolume);
            Assert.NotNull(loaded.ExtensionData);
            Assert.True(loaded.ExtensionData!.ContainsKey("FutureM99Setting"));
            Assert.True(store.Save(loaded));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(json.RootElement.TryGetProperty("FutureM99Setting", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TestSoundCommand_OnlyInvokesInjectedAudioPreview()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var before = store.Load();
            var previews = 0;
            using var viewModel = new FoundationViewModel(
                store,
                new ResourceLocalizationService("en"),
                NullAppLogger.Instance,
                new ChatTypographyResolver(NullAppLogger.Instance),
                () => { },
                _ => { },
                () => { },
                testSalesTurnSound: () => previews++);

            viewModel.TestSalesTurnSoundCommand.Execute(null);

            Assert.Equal(1, previews);
            Assert.Equal(before, store.Current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RemoteClientContract_ExposesExistingPresenceStreamEvent()
    {
        var presenceEvent = typeof(ILSOverlayRemoteClient).GetEvent("HostPresenceChanged");
        Assert.NotNull(presenceEvent);
        Assert.Equal(typeof(Action<HostPresenceSnapshot>), presenceEvent!.EventHandlerType);
    }

    private static SessionHudViewModel Session(AppSettings? settings = null)
    {
        var viewModel = new SessionHudViewModel(
            new ResourceLocalizationService("en"),
            settings ?? AppSettings.CreateDefault());
        viewModel.UpdateRemoteState(true, SessionRemoteState.Live);
        return viewModel;
    }

    private static BootstrapResponse Bootstrap(params HostPresenceSnapshot[] hosts) => new(
        OverlayTransportProtocol.Version,
        "generation",
        0,
        123,
        hosts);

    private static HostPresenceSnapshot Host(
        int slot,
        HostPresenceState state,
        int? current,
        int? maximum,
        DateTimeOffset? observedAt = null) => new(
        slot,
        state,
        current,
        maximum,
        observedAt ?? Now);

    private static SalesQueuePresentationState Presentation(
        SalesQueueContentMode mode,
        bool trustworthy = true) => new(
        mode,
        SalesHealthVisualMode.Live,
        SalesStatusIconKind.LiveDot,
        SalesQueueAccentKind.Standard,
        SalesQueueAnimationRequest.None,
        true,
        false,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        SalesQueueVisibleFields.None,
        mode == SalesQueueContentMode.CurrentTurnSelf ? "current" : "other",
        mode == SalesQueueContentMode.NextTurnSelf ? "next" : null,
        trustworthy);

    private static NotificationFixture Notification(AppSettings? settings = null)
    {
        var current = settings ?? AppSettings.CreateDefault();
        var sound = new RecordingSoundService();
        return new NotificationFixture(
            new SalesTurnNotificationCoordinator(() => current, sound, NullAppLogger.Instance),
            sound);
    }

    private sealed record NotificationFixture(
        SalesTurnNotificationCoordinator Coordinator,
        RecordingSoundService Sound);

    private sealed class RecordingSoundService : ISalesNotificationSoundService
    {
        public List<(SalesTurnNotificationKind Kind, double Volume)> Played { get; } = new();

        public void Play(SalesTurnNotificationKind kind, double volumePercent) =>
            Played.Add((kind, volumePercent));

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private int _errorCount;
        private int _warningCount;

        public int ErrorCount => Volatile.Read(ref _errorCount);

        public int WarningCount => Volatile.Read(ref _warningCount);

        public void Information(string category, string message)
        {
        }

        public void Warning(string category, string message)
        {
            Interlocked.Increment(ref _warningCount);
        }

        public void Error(string category, string message, Exception? exception = null)
        {
            Interlocked.Increment(ref _errorCount);
        }
    }

}
