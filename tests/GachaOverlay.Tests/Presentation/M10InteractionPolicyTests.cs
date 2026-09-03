using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;

namespace GachaOverlay.Tests.Presentation;

public sealed class M10InteractionPolicyTests
{
    [Fact]
    public void ReadingHistoryNeverChangesToFollowingOnNewMessagesAndCounterIsBounded()
    {
        var state = new ChatScrollState();
        state.ObserveUserOffset(20, 100);
        for (var i = 0; i < 100; i++) state.ReceiveNewMessage();
        Assert.False(state.IsFollowingLatest);
        Assert.Equal(20, state.UnreadCount);
        state.ObserveUserOffset(99, 100);
        Assert.True(state.IsFollowingLatest);
        Assert.Equal(0, state.UnreadCount);
        state.ReceiveNewMessage();
        Assert.Equal(0, state.UnreadCount);
    }

    [Fact]
    public void LockedChatHidesJumpButKeepsUnreadAndCommitResetsIt()
    {
        var vm = new ChatViewModel { IsHudUnlocked = true };
        vm.ObserveUserScroll(0, 100);
        vm.NotifyNewMessage();
        Assert.True(vm.IsJumpVisible);
        vm.IsHudUnlocked = false;
        Assert.False(vm.IsJumpVisible);
        Assert.Equal(1, vm.ScrollState.UnreadCount);
        vm.IsHudUnlocked = true;
        Assert.True(vm.IsJumpVisible);
        string? feedback = null;
        vm.ChannelFeedbackRequested += value => feedback = value;
        vm.NotifyCommittedChannel("1호실");
        Assert.Equal("#1호실", feedback);
        Assert.True(vm.ScrollState.IsFollowingLatest);
        Assert.Equal(0, vm.ScrollState.UnreadCount);
    }

    [Fact]
    public void TConsumesRepeatsAndKeyUpAfterForegroundChangesButActivatesOnlyOnce()
    {
        var policy = new DiscordQuickFocusPolicy();
        Assert.Equal(new QuickFocusDecision(true, true), policy.HandleT(true, true, false, false, true));
        for (var i = 0; i < 10; i++)
            Assert.Equal(new QuickFocusDecision(true, false), policy.HandleT(true, false, false, false, true));
        Assert.Equal(new QuickFocusDecision(true, false), policy.HandleT(false, false, false, false, true));
        Assert.Equal(default, policy.HandleT(true, false, false, false, true));
        Assert.Equal(default, policy.HandleT(false, false, false, false, true));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void TOutsideAllowedContextPassesThrough(bool gta, bool modifiers, bool injected, bool enabled)
    {
        var policy = new DiscordQuickFocusPolicy();
        Assert.Equal(default, policy.HandleT(true, gta, modifiers, injected, enabled));
        Assert.Equal(default, policy.HandleT(false, gta, modifiers, injected, enabled));
    }

    [Fact]
    public void HookEnabledWhileTHeldDoesNotSynthesizeActivation()
    {
        var policy = new DiscordQuickFocusPolicy();
        policy.Reset(alreadyDown: true);
        Assert.Equal(default, policy.HandleT(true, true, false, false, true));
        policy.HandleT(false, true, false, false, true);
        Assert.True(policy.HandleT(true, true, false, false, true).RequestFocus);
    }

    [Fact]
    public void AllowlistMatchesIdsOnlyRetainsServerNamesAndWrapsAccessibleSubset()
    {
        var ordered = MainChannelPolicy.Ordered;
        var accessible = ordered.Reverse().Where((_, i) => i % 2 == 0)
            .Select(item => new RemoteChannelOption(item.Id, "server-" + item.Label, "guild", 500, false))
            .Append(new("999", "메인", "guild", 0, false));
        var result = MainChannelPolicy.Apply(accessible);
        Assert.Equal(new[] { ordered[0].Id, ordered[2].Id, ordered[4].Id, ordered[6].Id, ordered[8].Id },
            result.Select(item => item.ChannelId));
        Assert.All(result, item => Assert.StartsWith("server-", item.Name));
        Assert.Equal(result[^1].ChannelId, MainChannelPolicy.Step(result, result[0].ChannelId, -1));
        Assert.Equal(result[0].ChannelId, MainChannelPolicy.Step(result, result[^1].ChannelId, 1));
        Assert.Equal(result[0].ChannelId, MainChannelPolicy.Step(result, "outside", 1));
        Assert.Null(MainChannelPolicy.Step(Array.Empty<RemoteChannelOption>(), null, 1));
        Assert.Equal(ordered[0].Label, Assert.Single(MainChannelPolicy.Apply(new[]
            { new RemoteChannelOption(ordered[0].Id, "", "guild", 0, false) })).Name);
    }

    [Fact]
    public void Schema17MigratesOnlyMinimalDefaultAndPreservesSafeUserChoices()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{"SchemaVersion":17,"MinimalHudMode":false,"ShowGtaSession":false,"SelectedSessionHost":2,"HudSurfaceOpacity":0.4,"RemoteBackendBaseUrl":"https://example.test","QuickDiscordFocusEnabled":false}""");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.True(settings.MinimalHudMode);
            Assert.False(settings.ShowGtaSession);
            Assert.Equal(0.4, settings.HudSurfaceOpacity);
            Assert.False(settings.QuickDiscordFocusEnabled);
            Assert.Equal("", settings.PreviousMainChannelHotkey.Key);
            Assert.Equal("", settings.NextMainChannelHotkey.Key);
            Assert.True(store.Update(value => value with { MinimalHudMode = false }));
            Assert.False(new JsonSettingsStore(path).Load().MinimalHudMode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DetailAgeIsMinuteLevelAndDepartingRowCannotInvokeStatusButtons()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new SalesQueueDetailItem(1, "1", "name", "벙커", true, true, true,
            "현재", "나", true, "", "판매완료", _ => Task.CompletedTask,
            now.AddMinutes(-3), "raw", false);
        row.RefreshAge(now, new ResourceLocalizationService("ko"));
        Assert.Equal("3분 전", row.RelativeAge);
        Assert.True(row.IsCurrentSelf);
        row.MarkDeparting();
        Assert.False(row.IsStatusActionEnabled);
        Assert.False(row.IsStatusActionVisible);
        Assert.False(row.SetCompletedCommand.CanExecute(null));
    }
}
