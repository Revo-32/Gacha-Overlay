using GachaOverlay.Core.Hud;

namespace GachaOverlay.Tests.Hud;

public sealed class HudStateServiceTests
{
    [Fact]
    public void Defaults_AreLockedUserEnabledAlwaysAndInitiallyHidden()
    {
        var state = new HudStateService().Current;

        Assert.True(state.IsLocked);
        Assert.True(state.IsClickThrough);
        Assert.True(state.UserHudEnabled);
        Assert.Equal(HudVisibilityMode.Always, state.VisibilityMode);
        Assert.False(state.HasInitialConnectionReady);
        Assert.False(state.EffectiveVisible);
    }

    [Fact]
    public void ToggleLock_KeepsClickThroughDerivedFromLock()
    {
        var service = new HudStateService();

        service.ToggleLock();
        Assert.False(service.Current.IsLocked);
        Assert.False(service.Current.IsClickThrough);

        service.ToggleLock();
        Assert.True(service.Current.IsLocked);
        Assert.True(service.Current.IsClickThrough);
    }

    [Fact]
    public void InitialConnectionReady_OpensAlwaysVisibilityGate()
    {
        var service = new HudStateService();

        service.MarkInitialConnectionReady();

        Assert.True(service.Current.HasInitialConnectionReady);
        Assert.True(service.Current.EffectiveVisible);
    }

    [Fact]
    public void RemoteInitialConnectionReady_OpensGate()
    {
        var service = new HudStateService();

        service.MarkInitialConnectionReady();

        Assert.True(service.Current.HasInitialConnectionReady);
        Assert.True(service.Current.EffectiveVisible);
    }

    [Fact]
    public void AlwaysMode_IgnoresAbsentGameAndFalseForegroundAfterReady()
    {
        var service = new HudStateService(HudVisibilityMode.Always);
        service.SetTargetGameForeground(false);
        service.MarkInitialConnectionReady();

        Assert.True(service.Current.EffectiveVisible);

        service.SetTargetGameForeground(false);
        Assert.True(service.Current.EffectiveVisible);
    }

    [Fact]
    public void RepeatedReadySignal_DoesNotResetLatchedInitialReadiness()
    {
        var service = new HudStateService();
        service.MarkInitialConnectionReady();
        service.MarkInitialConnectionReady();

        Assert.True(service.Current.HasInitialConnectionReady);
        Assert.True(service.Current.EffectiveVisible);
    }

    [Fact]
    public void GameOnlyMode_TracksForegroundWithoutChangingUserIntent()
    {
        var service = new HudStateService(HudVisibilityMode.GameForegroundOnly);
        service.MarkInitialConnectionReady();

        Assert.False(service.Current.EffectiveVisible);

        service.SetTargetGameForeground(true);
        Assert.True(service.Current.EffectiveVisible);
        Assert.True(service.Current.UserHudEnabled);

        service.SetTargetGameForeground(false);
        Assert.False(service.Current.EffectiveVisible);
        Assert.True(service.Current.UserHudEnabled);
    }

    [Fact]
    public void ManualHide_IsNeverOverriddenByGameForegroundChanges()
    {
        var service = new HudStateService(HudVisibilityMode.GameForegroundOnly);
        service.MarkInitialConnectionReady();
        service.SetTargetGameForeground(true);
        service.SetUserHudEnabled(false);

        service.SetTargetGameForeground(false);
        service.SetTargetGameForeground(true);

        Assert.False(service.Current.UserHudEnabled);
        Assert.False(service.Current.EffectiveVisible);
    }

    [Fact]
    public void ManualHide_IsRespectedByAlwaysMode()
    {
        var service = new HudStateService(HudVisibilityMode.Always);
        service.MarkInitialConnectionReady();

        service.SetUserHudEnabled(false);
        service.SetTargetGameForeground(true);

        Assert.False(service.Current.EffectiveVisible);
    }

    [Fact]
    public void GameOnlyToAlways_ImmediatelyShowsWithoutGame()
    {
        var service = new HudStateService(HudVisibilityMode.GameForegroundOnly);
        service.MarkInitialConnectionReady();
        service.SetTargetGameForeground(false);
        Assert.False(service.Current.EffectiveVisible);

        service.SetVisibilityMode(HudVisibilityMode.Always);

        Assert.True(service.Current.EffectiveVisible);
        Assert.True(service.Current.UserHudEnabled);
    }

    [Fact]
    public void AlwaysToGameOnly_ImmediatelyHidesWithoutChangingUserIntent()
    {
        var service = new HudStateService(HudVisibilityMode.Always);
        service.MarkInitialConnectionReady();
        Assert.True(service.Current.EffectiveVisible);

        service.SetVisibilityMode(HudVisibilityMode.GameForegroundOnly);

        Assert.False(service.Current.EffectiveVisible);
        Assert.True(service.Current.UserHudEnabled);
    }

    [Fact]
    public void AlwaysMode_RemainsVisibleWhenMonitorPublishesFalse()
    {
        var service = new HudStateService(HudVisibilityMode.Always);
        service.MarkInitialConnectionReady();

        service.SetTargetGameForeground(true);
        service.SetTargetGameForeground(false);

        Assert.True(service.Current.EffectiveVisible);
    }

    [Fact]
    public void InvalidModeInDirectState_DoesNotUseForegroundAsVisibilityBypass()
    {
        var state = HudSessionState.CreateDefault() with
        {
            HasInitialConnectionReady = true,
            VisibilityMode = (HudVisibilityMode)999,
            IsTargetGameForeground = true,
        };

        Assert.False(state.EffectiveVisible);
    }

    [Fact]
    public void InvalidVisibilityMode_FallsBackToAlways()
    {
        var service = new HudStateService((HudVisibilityMode)999);

        Assert.Equal(HudVisibilityMode.Always, service.Current.VisibilityMode);
    }
}
