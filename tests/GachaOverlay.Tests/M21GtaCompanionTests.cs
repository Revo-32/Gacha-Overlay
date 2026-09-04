using System.Text.Json;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Gta;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;
using LSOverlay.Backend.Gta;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests;

public sealed class M21GtaCompanionTests
{
    private static readonly DateTimeOffset Reference = DateTimeOffset.Parse("2026-08-27T03:00:00Z");
    private static readonly CanonicalEventDocumentBuilder Builder = new();

    [Fact]
    public void GoldenCorpus_ContainsFourteenSyntheticBoundedFixtures_WithExpectedClassification()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gta", "weekly-golden.json");
        var fixtures = JsonSerializer.Deserialize<GoldenFixture[]>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(14, fixtures.Length);
        foreach (var fixture in fixtures)
        {
            var document = Document(
                fixture.Text,
                publisher: fixture.Publisher,
                channelName: fixture.Channel,
                receivedAt: fixture.Name == "outside-posting-window"
                    ? DateTimeOffset.Parse("2026-08-27T12:43:00+09:00")
                    : Reference);
            var result = new GtaEventClassifier().Classify(document);
            var actual = JsonNamingPolicy.CamelCase.ConvertName(result.Kind.ToString());
            Assert.True(fixture.Expected == actual,
                $"Fixture {fixture.Name} expected {fixture.Expected}, actual {actual} ({result.Reason}).");
        }
    }

    [Fact]
    public void CanonicalDocument_PrioritizesForward_DeduplicatesBody_AndNormalizesMarkdownDash()
    {
        var source = Input(
            content: "## **WEEKLY CHALLENGE**\r\nAUG 27—SEP 2",
            forwards: [new GtaEventForwardInput("## **WEEKLY CHALLENGE**\nAUG 27—SEP 2", [])]);

        var document = Builder.Build(source);

        Assert.True(document.IsForwarded);
        Assert.Single(document.CanonicalBlocks);
        Assert.Equal("ForwardContent", document.CanonicalBlocks[0].Kind);
        Assert.Equal("WEEKLY CHALLENGE\nAUG 27-SEP 2", document.CanonicalText);
    }

    [Fact]
    public void CanonicalDocument_UsesEmbedOnly_AndPreservesDistinctField()
    {
        var document = Builder.Build(Input(
            embeds: [new GtaEventEmbedInput(
                "Weekly Bulletin", "Body", [new("DISCOUNTS", "30% OFF BUNKERS")],
                "GTA Series Videos") ]));

        Assert.Equal(3, document.CanonicalBlocks.Count);
        Assert.Contains("DISCOUNTS\n30% OFF BUNKERS", document.CanonicalText, StringComparison.Ordinal);
        Assert.Equal("GTA Series Videos", document.SourcePublisher);
    }

    [Fact]
    public void WeeklyParser_ParsesGenericRewardsDiscountFreeDateAndUnknownEntity()
    {
        var document = Document("""
            A new GTA Online event starts on AUG 27-SEP 2
            WEEKLY CHALLENGE
            Complete 3 Future Raids to earn GTA$100,000
            BONUSES
            2X GTA$, RP & CASINO CHIPS ON Diamond Adversary
            2X GTA$, RP & RESEARCH PROGRESS ON Bunker Research
            2X GTA$ FIRST TIME COMPLETION ON Future Mission
            2X SPEED ON Future Production
            DISCOUNTS
            30% OFF Future Hypercar
            Future Bike - 25% OFF
            FREE ITEMS
            FREE Future Hat
            Future Shirt - FREE
            """);
        var classifier = new GtaEventClassifier();
        var parsed = new GtaEventParser().Parse(document, classifier.Classify(document));

        Assert.NotNull(parsed.Week);
        Assert.Contains(parsed.Week!.Bonuses, item => item.Multiplier == 2 &&
            item.RewardTypes.Contains(GtaRewardType.CasinoChips));
        Assert.Contains(parsed.Week.Bonuses, item => item.RewardTypes.Contains(GtaRewardType.ResearchProgress));
        Assert.Contains(parsed.Week.Bonuses, item => item.RewardTypes.Contains(GtaRewardType.FirstTimeCompletion));
        Assert.Contains(parsed.Week.Bonuses, item => item.RewardTypes.Contains(GtaRewardType.Speed));
        Assert.Contains(parsed.Week.Discounts, item => item.DiscountPercent == 30 && item.Activity!.Contains("Future Hypercar"));
        Assert.Contains(parsed.Week.Discounts, item => item.DiscountPercent == 25 && item.Activity!.Contains("Future Bike"));
        Assert.Contains(parsed.Week.FreeItems, item => item.Activity!.Contains("Future Hat"));
        Assert.Contains(parsed.Week.FreeItems, item => item.Activity!.Contains("Future Shirt"));
        Assert.NotNull(parsed.Week.WeeklyChallenge);
        Assert.Equal(3, parsed.Week.WeeklyChallenge!.Count);
    }

    [Fact]
    public void WeeklyParser_FutureSubRange_DoesNotBecomeWeeklyIdentity()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-03T05:08:00+09:00");
        var week = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "6X GTA$ & RP ON SPECIAL CARGO SALES (SEP 24-30)"), receivedAt);

        Assert.Equal("2026-09-03", week.WeekKey);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T18:00:00+09:00"), week.EffectiveFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T18:00:00+09:00"), week.EffectiveTo);
        Assert.NotEqual("2026-09-24", week.WeekKey);
    }

    [Fact]
    public void WeeklyParser_MultipleSecondaryRanges_UsesReceivedAtWeeklyCycle()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-03T05:08:00+09:00");
        var week = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "2X GTA$ & RP ON SPECIAL CARGO SALES (AUG 28-30)",
            "6X GTA$ & RP ON BUNKER RESEARCH PROGRESS (SEP 24-30)"), receivedAt);

        Assert.Equal("2026-09-03", week.WeekKey);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T18:00:00+09:00"), week.EffectiveFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T18:00:00+09:00"), week.EffectiveTo);
    }

    [Fact]
    public void WeeklyParser_PreResetBulletin_StagesCorrectUpcomingWeek()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-03T05:08:00+09:00");
        var week = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "6X GTA$ & RP ON SPECIAL CARGO SALES (SEP 24-30)"), receivedAt);
        var resolver = new GtaEventResolver();

        Assert.True(resolver.ApplyWeek(week, receivedAt));
        Assert.Null(resolver.TrustedState.ActiveWeek);
        Assert.Equal("2026-09-03", resolver.TrustedState.StagedWeek!.WeekKey);
    }

    [Fact]
    public void WeeklyParser_PostResetBulletin_BecomesCurrentActiveWeek()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-03T18:08:00+09:00");
        var week = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "6X GTA$ & RP ON SPECIAL CARGO SALES (SEP 24-30)"), receivedAt);
        var resolver = new GtaEventResolver();

        Assert.True(resolver.ApplyWeek(week, receivedAt));
        Assert.Equal("2026-09-03", resolver.TrustedState.ActiveWeek!.WeekKey);
        Assert.Null(resolver.TrustedState.StagedWeek);
        Assert.Equal(GtaResolvedAvailability.Available, resolver.Resolve(receivedAt).Availability);
    }

    [Fact]
    public void WeeklyParser_SecondaryRange_RemainsOnItemDateScope()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-03T05:08:00+09:00");
        var week = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "6X GTA$ & RP ON SPECIAL CARGO SALES (SEP 24-30)"), receivedAt);

        var bonus = Assert.Single(week.Bonuses, item => item.Multiplier == 6);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T18:00:00+09:00"), bonus.DateScope!.StartAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T18:00:00+09:00"), bonus.DateScope.EndAt);
        Assert.Equal("2026-09-03", week.WeekKey);
    }

    [Fact]
    public void Resolver_SameSourceMessage_ReconcilesPersistedBadFutureWeekKey()
    {
        var schedule = new KstResetSchedule();
        var now = DateTimeOffset.Parse("2026-09-04T12:00:00+09:00");
        var previousWeek = Week(
            "2026-08-27",
            DateTimeOffset.Parse("2026-08-27T18:00:00+09:00")) with { SourceMessageId = 41 };
        var badFuture = Week(
            "2026-09-24",
            DateTimeOffset.Parse("2026-09-24T18:00:00+09:00"));
        var resolver = new GtaEventResolver(schedule);
        resolver.Restore(new GtaTrustedEventState(1, previousWeek, badFuture, [], now.AddDays(-1)), now);
        var corrected = ParseTrustedWeek(WeeklyWithSecondaryRanges(
            "6X GTA$ & RP ON SPECIAL CARGO SALES (SEP 24-30)"),
            DateTimeOffset.Parse("2026-09-03T05:08:00+09:00"));

        Assert.True(resolver.ApplyWeek(corrected, now));
        Assert.Equal("2026-09-03", resolver.TrustedState.ActiveWeek!.WeekKey);
        Assert.Equal(corrected.SourceMessageId, resolver.TrustedState.ActiveWeek.SourceMessageId);
        Assert.Null(resolver.TrustedState.StagedWeek);
    }

    [Theory]
    [InlineData("AUG 28-30", 8, 28, 8, 31)]
    [InlineData("AUG 28-SEP 2", 8, 28, 9, 3)]
    [InlineData("DEC 29-JAN 4", 12, 29, 1, 5)]
    public void DateParser_HandlesSameMonthCrossMonthAndCrossYear(
        string text, int startMonth, int startDay, int endMonth, int endDay)
    {
        var range = Assert.Single(GtaEventDateParser.FindRanges(text, Reference));
        Assert.Equal(startMonth, range.StartAt.Month);
        Assert.Equal(startDay, range.StartAt.Day);
        Assert.Equal(endMonth, range.EndAt.Month);
        Assert.Equal(endDay, range.EndAt.Day);
        if (startMonth == 12) Assert.Equal(range.StartAt.Year + 1, range.EndAt.Year);
    }

    [Fact]
    public void Resolver_StagesFutureWeek_PromotesOnlyAtThursday1800Kst_AndRecoversMissedReset()
    {
        var schedule = new KstResetSchedule();
        var before = DateTimeOffset.Parse("2026-08-27T08:59:00Z"); // Thu 17:59 KST
        var after = DateTimeOffset.Parse("2026-08-27T09:01:00Z");  // Thu 18:01 KST
        var nextKey = schedule.GetWeeklyCycleKey(after);
        var resolver = new GtaEventResolver(schedule);
        var week = Week(nextKey, schedule.GetWeeklyCycleStart(after));

        Assert.True(resolver.ApplyWeek(week, before));
        Assert.Null(resolver.TrustedState.ActiveWeek);
        Assert.Equal(week, resolver.TrustedState.StagedWeek);
        Assert.False(resolver.EvaluateTransitions(DateTimeOffset.Parse("2026-08-27T04:00:00+09:00")));
        Assert.True(resolver.EvaluateTransitions(after));
        Assert.Equal(week, resolver.TrustedState.ActiveWeek);
        Assert.Null(resolver.TrustedState.StagedWeek);

        var restarted = new GtaEventResolver(schedule);
        restarted.Restore(new GtaTrustedEventState(1, null, week, [], before), after);
        Assert.Equal(week, restarted.TrustedState.ActiveWeek);
    }

    [Fact]
    public void Resolver_ReturnsPreparingForSixHoursThenUnavailable_WithoutClearingLastGood()
    {
        var schedule = new KstResetSchedule();
        var reset = DateTimeOffset.Parse("2026-08-27T09:00:00Z");
        var resolver = new GtaEventResolver(schedule);

        Assert.Equal(GtaResolvedAvailability.Preparing, resolver.Resolve(reset.AddHours(5)).Availability);
        Assert.Equal(GtaResolvedAvailability.Unavailable, resolver.Resolve(reset.AddHours(7)).Availability);
    }

    [Fact]
    public void ResetSchedule_Uses1500KstDailyAnd1800KstThursdayWeeklyKeys()
    {
        var schedule = new KstResetSchedule();
        var beforeDaily = DateTimeOffset.Parse("2026-08-28T05:59:00Z");
        var afterDaily = beforeDaily.AddMinutes(2);
        Assert.NotEqual(schedule.GetDailyCycleKey(beforeDaily), schedule.GetDailyCycleKey(afterDaily));
        Assert.Equal(15, schedule.GetDailyCycleStart(afterDaily).Hour);
        Assert.Equal(15, schedule.GetNextDailyReset(afterDaily).Hour);
        Assert.Equal(schedule.GetWeeklyCycleKey(beforeDaily), schedule.GetWeeklyCycleKey(afterDaily));
        Assert.Equal(DayOfWeek.Thursday, schedule.GetWeeklyCycleStart(afterDaily).DayOfWeek);
        Assert.Equal(18, schedule.GetWeeklyCycleStart(afterDaily).Hour);
        Assert.Equal(18, schedule.GetNextWeeklyReset(afterDaily).Hour);
        Assert.Contains(schedule.TimeZone.Id, new[] { "Asia/Seoul", "Korea Standard Time", "LSOverlay-KST" });
    }

    [Fact]
    public void LocalState_DefaultsAndDailyOperationsPersist_RejectDuplicates_AndReset()
    {
        var store = new MemoryCompanionStore();
        var before = DateTimeOffset.Parse("2026-08-28T05:50:00Z");
        var manager = new GtaCompanionStateManager(store, before);
        Assert.Equal(3, manager.Current.DailySlots.Count);
        Assert.True(manager.SelectDaily(1, "participate_race", null, before));
        Assert.False(manager.SelectDaily(2, "participate_race", null, before));
        Assert.True(manager.SelectDaily(2, GtaDailyChallengeCatalog.CustomChallengeId, "나만의 도전", before));
        Assert.True(manager.ToggleDailyCompletion(1, before));
        Assert.True(manager.Current.DailySlots[0].Completed);

        var restarted = new GtaCompanionStateManager(store, before);
        Assert.Equal("participate_race", restarted.Current.DailySlots[0].ChallengeId);
        restarted.ApplyTime(DateTimeOffset.Parse("2026-08-28T06:01:00Z"));
        Assert.All(restarted.Current.DailySlots, slot => Assert.Null(slot.ChallengeId));
    }

    [Fact]
    public void LocalState_WeeklyCompletionPreservesSemanticKey_AndChangesForNewChallenge()
    {
        var now = Reference;
        var manager = new GtaCompanionStateManager(new MemoryCompanionStore(), now);
        Assert.True(manager.ObserveWeeklyChallenge("same-key", now));
        Assert.True(manager.ToggleWeeklyCompletion(now));
        Assert.True(manager.ObserveWeeklyChallenge("same-key", now));
        Assert.True(manager.Current.WeeklyCompleted);
        Assert.True(manager.ObserveWeeklyChallenge("new-key", now));
        Assert.False(manager.Current.WeeklyCompleted);
    }

    [Fact]
    public void BackendLastGoodStore_RoundTripsAtomically_IsBounded_AndFallsBackFromCorruption()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("gta-companion-events.json");
        var store = new JsonGtaEventStore(path, NullLogger<JsonGtaEventStore>.Instance);
        var state1 = new GtaTrustedEventState(1, Week("2026-08-27", Reference), null, [], Reference);
        var campaigns = Enumerable.Range(1, 12).Select(index => Campaign($"c{index}")).ToArray();
        var state2 = state1 with { RelevantCampaigns = campaigns, LastUpdatedAt = Reference.AddMinutes(1) };

        Assert.True(store.Save(state1));
        Assert.True(store.Save(state2));
        Assert.Equal(GtaEventResolver.MaximumCampaigns, store.Load().RelevantCampaigns.Count);
        File.WriteAllText(path, "not-json");
        Assert.Equal(state1.ActiveWeek!.WeekKey, store.Load().ActiveWeek!.WeekKey);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void LocalJsonStateStore_UsesBackupAndCorruptPrimaryDoesNotCrash()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("gta-companion-state.json");
        var schedule = new KstResetSchedule();
        var store = new JsonGtaCompanionStateStore(path);
        var first = GtaCompanionLocalState.CreateDefault(schedule, Reference);
        var second = first with { WeeklyChallengeKey = "challenge" };
        Assert.True(store.Save(first));
        Assert.True(store.Save(second));
        File.WriteAllText(path, "broken");
        var recovered = store.Load()!;
        Assert.Equal(first.DailyCycleKey, recovered.DailyCycleKey);
        Assert.Equal(first.WeeklyCycleKey, recovered.WeeklyCycleKey);
        Assert.Equal(first.DailySlots.Select(item => item.ChallengeId),
            recovered.DailySlots.Select(item => item.ChallengeId));
    }

    [Fact]
    public void SettingsV21_MigratesMasterOffChildrenOnAndPersistsIndependentGeometryAndHotkey()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":20,\"gtaCompanionEnabled\":true,\"gtaCompanionDailyEnabled\":false}");
        var migrated = new JsonSettingsStore(path).Load();
        Assert.Equal(21, migrated.SchemaVersion);
        Assert.False(migrated.GtaCompanionEnabled);
        Assert.True(migrated.GtaCompanionDailyEnabled);
        Assert.True(migrated.GtaCompanionWeeklyEnabled);
        Assert.True(migrated.GtaCompanionWeeklyEventsEnabled);

        var store = new JsonSettingsStore(path);
        _ = store.Load();
        var geometry = new FloatingHudGeometry(10, 20, 390, 650, "display", 120);
        Assert.True(store.Update(settings => settings with
        {
            GtaCompanionEnabled = true,
            GtaCompanionWindowGeometry = geometry,
            GtaCompanionVisibilityHotkey = new HotkeySetting { Key = "F8" },
        }));
        var loaded = new JsonSettingsStore(path).Load();
        Assert.True(loaded.GtaCompanionEnabled);
        Assert.Equal(geometry, loaded.GtaCompanionWindowGeometry);
        Assert.Equal("F8", loaded.GtaCompanionVisibilityHotkey.Key);
    }

    [Fact]
    public void UiCorrective_UsesDisplayTextTemplateCompactGripAndNoDeveloperChrome()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "GachaOverlay.App", "Presentation", "GtaCompanionWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root, "src", "GachaOverlay.App", "Presentation", "GtaCompanionWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "GachaOverlay.App", "Presentation", "GtaCompanionViewModel.cs"));
        var settingsXaml = File.ReadAllText(Path.Combine(
            root, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        var controller = File.ReadAllText(Path.Combine(
            root, "src", "GachaOverlay.App", "Services", "GtaCompanionWindowController.cs"));

        Assert.Contains("DailyChallengeOptionTemplate", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemTemplate=\"{StaticResource DailyChallengeOptionTemplate}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextSearch.TextPath=\"DisplayText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DragHandle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"{Binding IsInteractive}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"360\" Height=\"470\" MinWidth=\"300\" MinHeight=\"180\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GtaCompanionCardSurfaceBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSettingsClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LockText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("편집 가능", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"GTA 컴패니언\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AvailabilityText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("이벤트 정보를 기다리는 중", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetSurfaceOpacity", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding GtaCompanionSurfaceOpacity, Mode=TwoWay}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("DefaultWidth: 360", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsRequested", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void UiCorrective_CompanionOpacityIsIndependentNormalizedAndPersisted()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":21,\"hudSurfaceOpacity\":0.3,\"gtaCompanionSurfaceOpacity\":2}");
        var store = new JsonSettingsStore(path);

        var normalized = store.Load();
        Assert.Equal(0.3, normalized.HudSurfaceOpacity, 3);
        Assert.Equal(1, normalized.GtaCompanionSurfaceOpacity, 3);
        Assert.Equal(HudSettingsDefaults.SurfaceOpacity, AppSettings.CreateDefault().GtaCompanionSurfaceOpacity, 3);

        Assert.True(store.Update(settings => settings with { GtaCompanionSurfaceOpacity = 0.45 }));
        var reloaded = new JsonSettingsStore(path).Load();
        Assert.Equal(0.3, reloaded.HudSurfaceOpacity, 3);
        Assert.Equal(0.45, reloaded.GtaCompanionSurfaceOpacity, 3);
    }

    [Fact]
    public void Protocol_OldAndNewJsonShapesRemainAdditivelyCompatible()
    {
        var oldResume = JsonSerializer.Deserialize<StreamClientMessage>(
            "{\"protocolVersion\":1,\"type\":\"resume\"}", OverlayProtocolJson.Options)!;
        Assert.Null(oldResume.Capabilities);
        Assert.False(BackendWebSocketSession.SupportsGtaCompanion(oldResume));

        var newResume = new StreamClientMessage(1, "resume", Capabilities: [OverlayTransportProtocol.GtaCompanionV1Capability]);
        var legacyResume = JsonSerializer.Deserialize<LegacyClient>(
            JsonSerializer.Serialize(newResume, OverlayProtocolJson.Options), OverlayProtocolJson.Options)!;
        Assert.Equal("resume", legacyResume.Type);
        Assert.True(BackendWebSocketSession.SupportsGtaCompanion(newResume));

        var newFrame = new StreamServerMessage(1, OverlayTransportProtocol.GtaCompanionSnapshot,
            GtaCompanion: new GtaCompanionSnapshot(1, 1, GtaCompanionDataState.Preparing, Reference, null, null));
        var legacyFrame = JsonSerializer.Deserialize<LegacyServer>(
            JsonSerializer.Serialize(newFrame, OverlayProtocolJson.Options), OverlayProtocolJson.Options)!;
        Assert.Equal(OverlayTransportProtocol.GtaCompanionSnapshot, legacyFrame.Type);
    }

    [Fact]
    public void FloatingHudFoundation_RecoversOffScreenAndUsesIndependentWindowIdentity()
    {
        var engine = new FloatingHudPlacementEngine();
        var result = engine.Resolve(
            new FloatingHudGeometry(9000, 9000, 390, 650, "gone", 96),
            [new DisplayWorkingArea("primary", new HudRectangle(0, 0, 1920, 1080), 96, true)],
            new FloatingHudPlacementOptions(390, 650, 310, 220));
        Assert.True(result.WasCorrected);
        Assert.Equal("primary", result.Geometry.DisplayId);

        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "GachaOverlay.App", "Services", "GtaCompanionWindowController.cs"));
        Assert.Contains("WindowId = \"gta-companion\"", source, StringComparison.Ordinal);
        Assert.Contains("FloatingHudPlacementEngine", source, StringComparison.Ordinal);
        Assert.Contains("HudStateService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRouting_UsesExactEventChannel_AndDoesNotExposeItAsNormalChat()
    {
        Assert.Equal(1417898156187713577UL, GtaCompanionProtocolPolicy.ProductionEventChannelId);
        var root = FindRepositoryRoot();
        var authorization = File.ReadAllText(Path.Combine(root, "src", "LSOverlay.Backend", "Chat", "ChatAuthorizationService.cs"));
        Assert.Contains("ProductionEventChannelId", authorization, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(root, "src", "GachaOverlay.App", "Lifecycle", "ApplicationHost.cs"));
        Assert.DoesNotContain("1417898156187713577", app, StringComparison.Ordinal);
    }

    [Fact]
    public void VocabularyAndCatalog_AreBoundedAndExposeHonestCoverageAndAnalyzerSeam()
    {
        Assert.Equal(12, GtaDailyChallengeCatalog.Entries.Count(item => item.Status == GtaDailyChallengeStatus.Active));
        Assert.Equal(3, GtaDailyChallengeCatalog.Entries.Count(item => item.Status == GtaDailyChallengeStatus.Legacy));
        Assert.Equal(4, GtaDailyChallengeCatalog.Entries.Count(item => item.Status == GtaDailyChallengeStatus.Unverified));
        Assert.Equal(13, GtaEventVocabulary.HeadingFamilyCount);
        Assert.Equal(16, GtaEventVocabulary.ChallengeActions.Count);
        Assert.Equal(15, GtaEventVocabulary.RewardModifierTerms.Count);
        Assert.Equal(22, GtaEventVocabulary.Glossary.Count);
        Assert.Equal(26, GtaEventVocabulary.KnownActivityAliasCount);

        var unknown = new GtaUnknownVocabularyReport();
        foreach (var index in Enumerable.Range(0, 100)) unknown.Observe("entity", $"Future {index}");
        Assert.Equal(GtaUnknownVocabularyReport.MaximumEntries, unknown.Snapshot().Count);

        var analysis = new GtaEventCorpusAnalyzer().Analyze([Input(content: "unrelated")]);
        Assert.Equal(1, analysis.MessageCount);
        Assert.Equal(0, analysis.WeeklyCount);
        Assert.Equal(0, analysis.CampaignCount);
    }

    [Fact]
    public async Task BackendService_CandidatePromotesOnUpdate_AndMalformedEditOrDeletePreservesLastGood()
    {
        using var directory = new TemporaryDirectory();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-27T09:01:00Z"));
        var source = new FakeEventSource();
        var store = new MemoryEventStore(directory.File("gta-companion-events.json"));
        var service = CreateService(directory.Path, clock, source, store);
        var candidate = Document("WEEKLY CHALLENGE\nComplete 3 Missions\nBONUSES\n2X GTA$ ON MISSIONS\nDISCOUNTS\n30% OFF VEHICLES", publisher: "GTA Series Videos");
        service.ProcessDocument(candidate);
        Assert.Single(service.CaptureDiagnostics().Candidates);
        Assert.Null(service.CaptureSnapshot().CurrentWeek);

        var trusted = Document("""
            A new GTA Online event starts on AUG 27-SEP 2
            WEEKLY CHALLENGE
            Complete 3 Contact Missions
            BONUSES
            2X GTA$ & RP ON CONTACT MISSIONS
            DISCOUNTS
            30% OFF BUNKERS
            GUN VAN
            TEST RIDES
            """);
        source.Message = trusted;
        await service.ReceiveUpdateAsync(123, GtaCompanionProtocolPolicy.ProductionEventChannelId, 42);
        var lastGood = service.CaptureSnapshot();
        Assert.Equal(GtaCompanionDataState.Available, lastGood.State);
        Assert.NotNull(lastGood.CurrentWeek);

        var changedTrusted = Document("""
            A new GTA Online event starts on AUG 27-SEP 2
            WEEKLY CHALLENGE
            Complete 4 Contact Missions
            BONUSES
            3X GTA$ & RP ON CONTACT MISSIONS
            DISCOUNTS
            40% OFF BUNKERS
            GUN VAN
            TEST RIDES
            """);
        source.Message = changedTrusted;
        await service.ReceiveUpdateAsync(123, GtaCompanionProtocolPolicy.ProductionEventChannelId, 42);
        lastGood = service.CaptureSnapshot();
        Assert.Contains("4", lastGood.CurrentWeek!.WeeklyChallenge!.DisplayTextKo, StringComparison.Ordinal);

        source.Message = candidate;
        await service.ReceiveUpdateAsync(123, GtaCompanionProtocolPolicy.ProductionEventChannelId, 42);
        Assert.Equal(lastGood.CurrentWeek, service.CaptureSnapshot().CurrentWeek);
        service.ReceiveDelete(123, GtaCompanionProtocolPolicy.ProductionEventChannelId, 42);
        Assert.Equal(lastGood.CurrentWeek, service.CaptureSnapshot().CurrentWeek);
    }

    [Fact]
    public async Task BackendHydration_IsBoundedNewestFirstIdempotentAndStormGuarded()
    {
        using var directory = new TemporaryDirectory();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-27T09:01:00Z"));
        var source = new FakeEventSource
        {
            Recent = Enumerable.Range(1, 130).Reverse().Select(index =>
                Document($"unrelated {index}")).ToArray(),
        };
        var service = CreateService(directory.Path, clock, source,
            new MemoryEventStore(directory.File("gta-companion-events.json")));

        await service.HydrateAsync(force: false, CancellationToken.None);
        await service.HydrateAsync(force: false, CancellationToken.None);

        Assert.Equal(GtaEventService.MaximumHydrationMessages, source.LastLimit);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, service.CaptureDiagnostics().HydrationSuccesses);
    }

    private static CanonicalEventDocument Document(
        string text,
        string? publisher = null,
        string? channelName = null,
        DateTimeOffset? receivedAt = null) => Builder.Build(Input(
            content: text,
            publisher: publisher,
            channelName: channelName,
            receivedAt: receivedAt));

    private static GtaEventWeek ParseTrustedWeek(string text, DateTimeOffset receivedAt)
    {
        var document = Document(text, receivedAt: receivedAt);
        var classifier = new GtaEventClassifier();
        var classification = classifier.Classify(document);
        Assert.Equal(GtaEventClassificationKind.WeeklyBulletin, classification.Kind);
        return Assert.IsType<GtaEventWeek>(new GtaEventParser().Parse(document, classification).Week);
    }

    private static string WeeklyWithSecondaryRanges(params string[] bonuses) => $$"""
        THE LATEST GTA ONLINE EVENT IS STILL LIVE
        WEEKLY CHALLENGE
        Complete 3 Contact Missions
        BONUSES
        {{string.Join('\n', bonuses)}}
        DISCOUNTS
        30% OFF BUNKERS
        FREE ITEMS
        FREE FUTURE HAT
        """;

    private static GtaEventSourceInput Input(
        string? content = null,
        IReadOnlyList<GtaEventEmbedInput>? embeds = null,
        IReadOnlyList<GtaEventForwardInput>? forwards = null,
        string? publisher = null,
        string? channelName = null,
        DateTimeOffset? receivedAt = null) => new(
            42,
            GtaCompanionProtocolPolicy.ProductionEventChannelId,
            receivedAt ?? Reference,
            null,
            content,
            embeds ?? [],
            forwards ?? [],
            publisher,
            channelName);

    private static GtaEventWeek Week(string key, DateTimeOffset effective) => new(
        key, effective, effective.AddDays(7), null, null, [], [], [], [], 42, effective);

    private static GtaEventCampaign Campaign(string key) => new(
        key, key, Reference, Reference.AddDays(30), [], [], [], 42, Reference);

    private static GtaEventService CreateService(
        string directory,
        TimeProvider clock,
        IGtaEventDiscordSource source,
        IGtaEventStore store)
    {
        var vocabulary = new GtaEventVocabulary();
        var unknown = new GtaUnknownVocabularyReport();
        return new GtaEventService(
            new LSOverlay.Backend.Configuration.BackendConfiguration(
                new LSOverlay.Backend.Configuration.BackendBotCredential("synthetic"),
                123,
                [],
                directory),
            source,
            store,
            new GtaEventClassifier(vocabulary),
            new GtaEventParser(vocabulary, unknown),
            new GtaEventResolver(),
            new GtaKoreanFormatter(vocabulary),
            unknown,
            clock,
            NullLogger<GtaEventService>.Instance);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GachaOverlay.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record GoldenFixture(
        string Name,
        string Expected,
        string Text,
        string? Publisher = null,
        string? Channel = null);

    private sealed record LegacyClient(int ProtocolVersion, string Type);
    private sealed record LegacyServer(int ProtocolVersion, string Type);

    private sealed class MemoryCompanionStore : IGtaCompanionStateStore
    {
        public GtaCompanionLocalState? Value { get; private set; }
        public GtaCompanionLocalState? Load() => Value;
        public bool Save(GtaCompanionLocalState state) { Value = state; return true; }
    }

    private sealed class MemoryEventStore(string path) : IGtaEventStore
    {
        private GtaTrustedEventState _state = GtaTrustedEventState.Empty;
        public string Path { get; } = path;
        public GtaTrustedEventState Load() => _state;
        public bool Save(GtaTrustedEventState state) { _state = state; return true; }
    }

    private sealed class FakeEventSource : IGtaEventDiscordSource
    {
        public CanonicalEventDocument? Message { get; set; }
        public IReadOnlyList<CanonicalEventDocument> Recent { get; set; } = [];
        public int Calls { get; private set; }
        public int LastLimit { get; private set; }
        public CanonicalEventDocument Build(global::Discord.IMessage message) => throw new NotSupportedException();
        public Task<GtaEventHydrationSourceResult> GetRecentAsync(int limit, CancellationToken cancellationToken)
        {
            Calls++;
            LastLimit = limit;
            return Task.FromResult(new GtaEventHydrationSourceResult(
                GtaEventSourceStatus.Available,
                Recent.Take(limit).ToArray()));
        }
        public Task<GtaEventMessageSourceResult> GetMessageAsync(ulong messageId, CancellationToken cancellationToken) =>
            Task.FromResult(new GtaEventMessageSourceResult(
                Message is null ? GtaEventSourceStatus.TemporarilyUnavailable : GtaEventSourceStatus.Available,
                Message));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
