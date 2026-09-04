using System.Text.Json;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Tests.Sales;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests;

public sealed class M21FoundationTests
{
    [Fact]
    public void SettingsV20_MigratesToKoreanAndNewPresentationDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 19,
              "language": "ja",
              "chatMessageSpacing": 31,
              "remoteSelectedChannelId": "123"
            }
            """);

        var settings = new JsonSettingsStore(path).Load();

        Assert.Equal(21, settings.SchemaVersion);
        Assert.Equal("ko", settings.Language);
        Assert.Equal(RoleIconPosition.Left, settings.ChatRoleIconPosition);
        Assert.Equal(18, settings.ChatReactionSize);
        Assert.Equal(31, settings.ChatMessageSpacing);
        Assert.Equal("123", settings.RemoteSelectedChannelId);
    }

    [Fact]
    public void SettingsV20_ClampsMalformedPresentationValues()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 20, "chatRoleIconPosition": 999, "chatReactionSize": 99 }
            """);

        var settings = new JsonSettingsStore(path).Load();

        Assert.Equal(RoleIconPosition.Left, settings.ChatRoleIconPosition);
        Assert.Equal(42, settings.ChatReactionSize);
    }

    [Fact]
    public void SettingsV20_InterpretsLegacyRightValueAsAdjacentRight()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 20, "chatRoleIconPosition": 1 }
            """);

        var settings = new JsonSettingsStore(path).Load();

        Assert.Equal(RoleIconPosition.AdjacentRight, settings.ChatRoleIconPosition);
    }

    [Theory]
    [InlineData(14, 14, 12.44, 10.4)]
    [InlineData(18, 18, 16, 12)]
    [InlineData(42, 42, 37.33, 21)]
    public void ReactionMasterSize_MapsPresentationMetrics(
        double input,
        double image,
        double unicode,
        double count)
    {
        var metrics = ChatReactionMetrics.FromMasterSize(input);

        Assert.Equal(image, metrics.ImageExtent, 2);
        Assert.Equal(unicode, metrics.UnicodeFontSize, 2);
        Assert.Equal(count, metrics.CountFontSize, 2);
    }

    [Fact]
    public void ChatPresentation_AppliesAllRolePositionsAndReactionMasterSizeImmediately()
    {
        var presentation = new ChatMessagePresentation(
            "m1",
            "VeryVeryLongNickname",
            DateTimeOffset.UtcNow,
            [new ChatToken(ChatTokenKind.Text, "message")],
            "message",
            [],
            [],
            0,
            false,
            1,
            1)
        {
            AuthorStyle = new DiscordAuthorStyle(
                null,
                null,
                "role",
                new DiscordRoleIcon("unicode", "⭐")),
            Reactions =
            [
                new DiscordMessageReaction(
                    new DiscordCustomEmoji(string.Empty, "👍", false),
                    2),
            ],
        };
        using var viewModel = new ChatMessageViewModel(
            presentation,
            new ResourceLocalizationService(),
            _ => { });
        var typography = new ChatTypographyResolver(GachaOverlay.Core.Logging.NullAppLogger.Instance)
            .Resolve(ChatFontPreset.Kimm);

        foreach (var responsive in new[]
                 {
                     ChatResponsiveLevel.Full,
                     ChatResponsiveLevel.Reduced,
                     ChatResponsiveLevel.UltraCompact,
                 })
        {
            viewModel.ApplySettings(
                AppSettings.CreateDefault() with
                {
                    ChatRoleIconPosition = RoleIconPosition.Left,
                    ChatReactionSize = 42,
                },
                responsive,
                typography);
            Assert.True(viewModel.ShowRoleIconLeft);
            Assert.False(viewModel.ShowRoleIconAdjacentRight);
            Assert.False(viewModel.ShowRoleIconFarRight);
            Assert.Equal(
                System.Windows.GridUnitType.Star,
                viewModel.RoleIconNicknameColumnWidth.GridUnitType);

            viewModel.ApplySettings(
                AppSettings.CreateDefault() with
                {
                    ChatRoleIconPosition = RoleIconPosition.AdjacentRight,
                    ChatReactionSize = 42,
                },
                responsive,
                typography);
            Assert.False(viewModel.ShowRoleIconLeft);
            Assert.True(viewModel.ShowRoleIconAdjacentRight);
            Assert.False(viewModel.ShowRoleIconFarRight);
            Assert.True(viewModel.RoleIconNicknameColumnWidth.IsAuto);

            viewModel.ApplySettings(
                AppSettings.CreateDefault() with
                {
                    ChatRoleIconPosition = RoleIconPosition.FarRight,
                    ChatReactionSize = 42,
                },
                responsive,
                typography);
            Assert.False(viewModel.ShowRoleIconLeft);
            Assert.False(viewModel.ShowRoleIconAdjacentRight);
            Assert.True(viewModel.ShowRoleIconFarRight);
            Assert.Equal(
                System.Windows.GridUnitType.Star,
                viewModel.RoleIconNicknameColumnWidth.GridUnitType);
        }

        var reaction = Assert.Single(viewModel.Reactions);
        Assert.Equal(42, reaction.ImageExtent);
        Assert.Equal(37.33, reaction.UnicodeFontSize, 2);
        Assert.Equal(21, reaction.CountFontSize);

        viewModel.Update(presentation with { AuthorStyle = null, Reactions = [] });
        Assert.False(viewModel.ShowRoleIconLeft);
        Assert.False(viewModel.ShowRoleIconAdjacentRight);
        Assert.False(viewModel.ShowRoleIconFarRight);
        Assert.False(viewModel.HasReactions);
        Assert.Equal(new[] { true, false, true },
            ChatAuthorGrouping.ResolveHeaders(["author", "author", "other"]));
    }

    [Fact]
    public void SettingsUi_UsesRequiredRoleAndReactionControlsAndHidesLanguageSelector()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "FoundationWindow.xaml"));

        Assert.Contains("SettingsRoleIconPosition", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedRoleIconPosition", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsRoleIconFarRight", File.ReadAllText(Path.Combine(
            root,
            "src",
            "GachaOverlay.Infrastructure",
            "Localization",
            "Resources",
            "Strings.ko.resx")), StringComparison.Ordinal);
        Assert.Contains("SettingsReactionSize", xaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"14\" Maximum=\"42\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SalesHistoryTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SalesHistory.ResetCommand}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesHistory.ResetCommand", xaml.AsSpan(
            0,
            xaml.IndexOf("x:Key=\"SalesHistoryTemplate\"", StringComparison.Ordinal)).ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("OnboardingLanguageTitle", File.ReadAllText(Path.Combine(
            root,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "OnboardingWindow.xaml")), StringComparison.Ordinal);
        Assert.DoesNotContain("Localization[Language]", xaml, StringComparison.Ordinal);
        var chat = File.ReadAllText(Path.Combine(
            root,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "ChatMessageView.xaml"));
        Assert.Contains("RoleIconNicknameColumnWidth", chat, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", chat, StringComparison.Ordinal);
        Assert.Contains("MaxWidth\" Value=\"360\"", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void SalesHistoryStore_IsCanonicalAtomicAndCorruptionTolerant()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("sales-history.json");
        var store = new JsonSalesHistoryStore(path, ["p1", "p2"]);
        var soldAt = DateTimeOffset.Parse("2026-09-04T03:04:05+09:00");

        Assert.True(store.RecordSold(["p1", "unknown", "p1"], soldAt));
        var saved = Assert.Single(store.Snapshot());
        Assert.Equal("p1", saved.ProductId);
        Assert.Equal(soldAt.ToUniversalTime(), saved.LastSoldAt);
        Assert.DoesNotContain("unknown", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(saved, Assert.Single(
            new JsonSalesHistoryStore(path, ["p1", "p2"]).Snapshot()));

        File.WriteAllText(path, "not-json");
        Assert.Empty(new JsonSalesHistoryStore(path, ["p1", "p2"]).Snapshot());
    }

    [Theory]
    [InlineData("2026-09-04T03:42:00Z", "2026-09-04T05:00:00Z", "오늘 12:42")]
    [InlineData("2026-09-03T14:18:00Z", "2026-09-04T05:00:00Z", "어제 23:18")]
    [InlineData("2026-09-02T10:35:00Z", "2026-09-04T05:00:00Z", "9월 2일 19:35")]
    [InlineData("2025-07-18T12:30:00Z", "2026-09-04T05:00:00Z", "2025년 7월 18일 21:30")]
    public void SalesHistoryDisplay_AlwaysUsesKoreaTime(
        string soldAt,
        string now,
        string expected)
    {
        Assert.Equal(expected, SalesHistoryViewModel.FormatLocalTime(
            DateTimeOffset.Parse(soldAt),
            DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void SalesHistoryViewModel_RefreshesImmediatelyWhenStoreChanges()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("p1", "e1", "one", korean: "벙커"));
        var store = new MemorySalesHistoryStore();
        using var viewModel = new SalesHistoryViewModel(
            store,
            catalog,
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            () => true);
        Assert.Equal("기록 없음", Assert.Single(viewModel.Rows).LastSoldText);

        store.RecordSold(["p1"], DateTimeOffset.UtcNow);

        Assert.NotEqual("기록 없음", Assert.Single(viewModel.Rows).LastSoldText);
    }

    [Fact]
    public void SalesHistoryRecorder_RecordsOnlyOwnAuthoritativePendingToSoldReadback()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("p1", "e1", "one"),
            SalesTestFactory.Product("p2", "e2", "two"));
        var engine = SalesTestFactory.Engine(catalog);
        engine.SetAuthenticatedUser("me");
        engine.ApplySourceSnapshot([
            SalesTestFactory.Message(
                "m1",
                authorId: "me",
                content: "decorative",
                emojis:
                [
                    new GachaOverlay.Core.Discord.Messages.DiscordCustomEmoji("e1", "one", false),
                    new GachaOverlay.Core.Discord.Messages.DiscordCustomEmoji("e2", "two", false),
                ])
        ]);
        Assert.Equal(SaleParseStatus.PartiallyParsed, Assert.Single(engine.Records).ParseStatus);
        var store = new MemorySalesHistoryStore();
        var recorder = new SalesHistoryTransitionRecorder(store);
        var sold = SalesTestFactory.Batch(
            1,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("m1", SaleReactionOutcome.Sold, 1));

        Assert.Empty(recorder.CapturePendingOwn(
            enabled: false,
            sold,
            engine.Records,
            engine.Current.AuthenticatedUserId));
        var candidates = recorder.CapturePendingOwn(
            enabled: true,
            sold,
            engine.Records,
            engine.Current.AuthenticatedUserId);
        engine.ApplyObservationBatch(sold);
        recorder.RecordConfirmedSold(candidates, engine.Records);

        Assert.Equal(2, store.Values.Count);
        Assert.All(store.Values.Values, value =>
            Assert.Equal(SalesTestFactory.Epoch.AddMinutes(1), value));
        Assert.Empty(recorder.CapturePendingOwn(
            enabled: true,
            sold,
            engine.Records,
            engine.Current.AuthenticatedUserId));

        var notSold = SalesTestFactory.Batch(
            2,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("m1", SaleReactionOutcome.NotSold, 2));
        engine.ApplyObservationBatch(notSold);
        var soldAgain = SalesTestFactory.Batch(
            3,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("m1", SaleReactionOutcome.Sold, 3));
        var secondCandidates = recorder.CapturePendingOwn(
            enabled: true,
            soldAgain,
            engine.Records,
            engine.Current.AuthenticatedUserId);
        engine.ApplyObservationBatch(soldAgain);
        recorder.RecordConfirmedSold(secondCandidates, engine.Records);
        Assert.All(store.Values.Values, value =>
            Assert.Equal(SalesTestFactory.Epoch.AddMinutes(3), value));

        engine.ApplySourceSnapshot([
            SalesTestFactory.Message(
                "m2",
                authorId: "other",
                content: string.Empty,
                emojis:
                [
                    new DiscordCustomEmoji("e1", "one", false),
                ])
        ]);
        var otherSold = SalesTestFactory.Batch(
            4,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("m2", SaleReactionOutcome.Sold, 4));
        Assert.Empty(recorder.CapturePendingOwn(
            enabled: true,
            otherSold,
            engine.Records,
            engine.Current.AuthenticatedUserId));
    }

    [Fact]
    public void ProcessMetrics_UsesExplicitMemoryClassificationsAndBoundedTrend()
    {
        var clock = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var sampler = new ProcessMetricsSampler(() =>
        {
            clock = clock.AddMinutes(1);
            return clock;
        });
        using var trend = new ProcessMetricsTrendSampler(
            sampler,
            TimeSpan.FromMinutes(1),
            capacity: 3,
            startTimer: false);

        trend.Capture();
        trend.Capture();
        var capture = trend.Capture();

        Assert.Equal(3, capture.Samples.Count);
        Assert.Equal(3, capture.Summary.SampleCount);
        Assert.True(capture.Current.TotalWorkingSetBytes > 0);
        Assert.True(capture.Current.PrivateCommitBytes > 0);
        Assert.True(capture.Current.ManagedHeapBytes > 0);
        Assert.Contains("PrivateWorkingSetBytes", capture.Summary.Metrics.Keys);
        Assert.Contains("TotalWorkingSetBytes", capture.Summary.Metrics.Keys);
        Assert.Contains("PrivateCommitBytes", capture.Summary.Metrics.Keys);
        Assert.Contains("ManagedHeapBytes", capture.Summary.Metrics.Keys);
        Assert.Equal(3, capture.Summary.Metrics["TotalWorkingSetBytes"].SampleCount);
        Assert.Equal(2 * 60, capture.Summary.Metrics["TotalWorkingSetBytes"].ObservationDurationSeconds);
    }

    [Fact]
    public void SharedTimerFoundation_SeparatesWallClockAndPositiveOnlinePlaytime()
    {
        var store = new MemoryTimerStore();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        var registry = new SharedTimerRegistry(store, time);
        var completed = new List<string>();
        registry.Completed += item => completed.Add(item.TimerId);
        registry.Start("wall", TimerClockMode.WallClock, TimeSpan.FromSeconds(10));
        registry.Start("online", TimerClockMode.OnlinePlaytime, TimeSpan.FromSeconds(10));

        registry.Update(OnlinePlaytimeAvailability.Online);
        time.Advance(TimeSpan.FromSeconds(6));
        registry.Update(OnlinePlaytimeAvailability.Online);
        registry.Update(OnlinePlaytimeAvailability.Unknown);
        time.Advance(TimeSpan.FromSeconds(20));
        var paused = registry.Update(OnlinePlaytimeAvailability.Unknown);

        Assert.Equal(SharedTimerState.Ready, paused.Single(item => item.TimerId == "wall").State);
        Assert.Equal(TimeSpan.FromSeconds(4), paused.Single(item => item.TimerId == "online").Remaining);
        Assert.Equal(["wall"], completed);

        time.Advance(TimeSpan.FromSeconds(30));
        registry.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Equal(["wall"], completed);
        registry.Update(OnlinePlaytimeAvailability.Online);
        time.Advance(TimeSpan.FromSeconds(4));
        registry.Update(OnlinePlaytimeAvailability.Online);
        registry.Update(OnlinePlaytimeAvailability.Online);
        Assert.Equal(["wall", "online"], completed);

        var restarted = new SharedTimerRegistry(store, time);
        var restartCompletions = new List<string>();
        restarted.Completed += item => restartCompletions.Add(item.TimerId);
        var restored = restarted.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Equal(SharedTimerState.Ready, restored.Single(item => item.TimerId == "wall").State);
        Assert.Equal(SharedTimerState.Ready, restored.Single(item => item.TimerId == "online").State);
        Assert.Empty(restartCompletions);
    }

    [Fact]
    public void FloatingHudFoundation_RestoresDpiAndAppliesGlobalLockPolicy()
    {
        var engine = new FloatingHudPlacementEngine();
        var result = engine.Resolve(
            new FloatingHudGeometry(5000, 5000, 300, 200, "display", 96),
            [new DisplayWorkingArea("display", new HudRectangle(0, 0, 1920, 1080), 144, true)],
            new FloatingHudPlacementOptions(300, 200, 160, 100));

        Assert.True(result.WasCorrected);
        Assert.Equal(450, result.Geometry.Width);
        Assert.Equal(300, result.Geometry.Height);
        Assert.True(result.Geometry.X < 1920);
        Assert.True(new FloatingHudInteractionState(true).IsClickThrough);
        Assert.False(new FloatingHudInteractionState(true).IsInteractive);

        var editor = new FloatingHudGeometryEditor();
        var moved = editor.Move(result.Geometry, -50, 25);
        var resized = editor.Resize(
            moved,
            -1000,
            -1000,
            new FloatingHudPlacementOptions(300, 200, 160, 100));
        Assert.Equal(result.Geometry.X - 50, moved.X);
        Assert.Equal(result.Geometry.Y + 25, moved.Y);
        Assert.Equal(160, resized.Width);
        Assert.Equal(100, resized.Height);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "GachaOverlay.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class MemorySalesHistoryStore : ISalesHistoryStore
    {
        public event Action? Changed;
        public Dictionary<string, DateTimeOffset> Values { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<SalesHistoryEntry> Snapshot() =>
            Values.Select(pair => new SalesHistoryEntry(pair.Key, pair.Value)).ToArray();
        public bool RecordSold(IReadOnlyCollection<string> productIds, DateTimeOffset soldAt)
        {
            foreach (var id in productIds)
            {
                Values[id] = soldAt;
            }
            Changed?.Invoke();
            return true;
        }
        public bool Clear() { Values.Clear(); Changed?.Invoke(); return true; }
    }

    private sealed class MemoryTimerStore : ISharedTimerStore
    {
        private IReadOnlyList<SharedTimerPersistedEntry> _entries = [];
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => _entries;
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries)
        {
            _entries = entries.ToArray();
            return true;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;
        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
