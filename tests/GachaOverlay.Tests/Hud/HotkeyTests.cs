using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.App.Services;

namespace GachaOverlay.Tests.Hud;

public sealed class HotkeyTests
{
    [Fact]
    public void Parser_RoundTripsSupportedMultiModifierGesture()
    {
        Assert.True(HotkeyGesture.TryParseDisplayText(
            "cOnTrOl+shift+ALT+L",
            out var gesture));

        Assert.Equal("Control+Shift+Alt+L", gesture.ToString());
        Assert.True(gesture.IsValid);
    }

    [Theory]
    [InlineData("F9")]
    [InlineData("f10")]
    [InlineData("K")]
    [InlineData("1")]
    public void Parser_AllowsBareSupportedKeys(string text)
    {
        Assert.True(HotkeyGesture.TryParseDisplayText(text, out var gesture));
        Assert.Equal(text.ToUpperInvariant(), gesture.ToString());
        Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        Assert.True(gesture.IsValid);
    }

    [Theory]
    [InlineData("Control+Mouse1")]
    [InlineData("Control+")]
    public void Parser_RejectsUnsafeOrUnsupportedGesture(string text)
    {
        Assert.False(HotkeyGesture.TryParseDisplayText(text, out _));
    }

    [Fact]
    public void Rebind_UnregistersOldBindingBeforeRegisteringNewOne()
    {
        var registrar = new FakeRegistrar();
        using var manager = new HotkeyBindingManager(registrar);
        var first = Parse("Control+Shift+L");
        var second = Parse("Control+Shift+H");
        manager.Rebind(1, first);

        var result = manager.Rebind(1, second);

        Assert.True(result.Success);
        Assert.Equal(second, manager.GetActiveGesture(1));
        Assert.Equal(new[] { "register:1", "unregister:1", "register:1" }, registrar.Calls);
    }

    [Fact]
    public void FailedRebind_RestoresPreviousRegistrationAndState()
    {
        var registrar = new FakeRegistrar();
        using var manager = new HotkeyBindingManager(registrar);
        var first = Parse("Control+Shift+L");
        var second = Parse("Control+Shift+H");
        manager.Rebind(1, first);
        registrar.FailGesture = second;

        var result = manager.Rebind(1, second);

        Assert.False(result.Success);
        Assert.True(result.PreviousBindingRestored);
        Assert.Equal(first, result.ActiveGesture);
        Assert.Equal(first, manager.GetActiveGesture(1));
    }

    [Fact]
    public void FailedUnregister_KeepsPreviousRegistrationState()
    {
        var registrar = new FakeRegistrar();
        using var manager = new HotkeyBindingManager(registrar);
        var first = Parse("Control+Shift+L");
        manager.Rebind(1, first);
        registrar.FailUnregister = true;

        var result = manager.Rebind(1, Parse("Control+Shift+H"));

        Assert.False(result.Success);
        Assert.True(result.PreviousBindingRestored);
        Assert.Equal(first, manager.GetActiveGesture(1));
    }

    [Fact]
    public void Dispose_UnregistersEveryActiveBinding()
    {
        var registrar = new FakeRegistrar();
        var manager = new HotkeyBindingManager(registrar);
        manager.Rebind(1, Parse("Control+Shift+L"));
        manager.Rebind(2, Parse("Control+Shift+H"));

        manager.Dispose();

        Assert.Contains("unregister:1", registrar.Calls);
        Assert.Contains("unregister:2", registrar.Calls);
    }

    [Fact]
    public void TwentyReassignmentCycles_LeaveExactlyOneActiveRegistration()
    {
        var registrar = new FakeRegistrar();
        using var manager = new HotkeyBindingManager(registrar);
        var f9 = Parse("F9");
        var controlF9 = Parse("Control+F9");
        var alt1 = Parse("Alt+1");
        Assert.True(manager.Rebind(1, f9).Success);

        for (var cycle = 0; cycle < 20; cycle++)
        {
            Assert.True(manager.Rebind(1, controlF9).Success);
            Assert.True(manager.Rebind(1, alt1).Success);
            Assert.True(manager.Rebind(1, f9).Success);
        }

        Assert.Equal(f9, manager.GetActiveGesture(1));
        Assert.Equal(61, registrar.Calls.Count(call => call == "register:1"));
        Assert.Equal(60, registrar.Calls.Count(call => call == "unregister:1"));
    }

    [Fact]
    public void RegistrationPlan_MapsVisibilityToBareF9AndLockToBareF10()
    {
        var plan = GlobalHotkeyService.CreateRegistrationPlan(
            HotkeySetting.DefaultLockToggle,
            HotkeySetting.DefaultVisibilityToggle);

        Assert.Equal("F9", plan.VisibilityToggle!.Value.ToString());
        Assert.Equal(HotkeyModifiers.None, plan.VisibilityToggle.Value.Modifiers);
        Assert.Equal("F10", plan.LockToggle!.Value.ToString());
        Assert.Equal(HotkeyModifiers.None, plan.LockToggle.Value.Modifiers);
    }

    [Fact]
    public void RegistrationPlan_DoesNotSwapCustomVisibilityAndLockGestures()
    {
        var plan = GlobalHotkeyService.CreateRegistrationPlan(
            Parse("Control+F8").ToSetting(),
            Parse("Alt+F7").ToSetting());

        Assert.Equal("Alt+F7", plan.VisibilityToggle!.Value.ToString());
        Assert.Equal("Control+F8", plan.LockToggle!.Value.ToString());
    }

    [Fact]
    public void RegistrationPlan_PreservesExplicitlyUnassignedHudHotkeys()
    {
        var unassigned = new HotkeySetting { Key = string.Empty };

        var plan = GlobalHotkeyService.CreateRegistrationPlan(unassigned, unassigned);

        Assert.Null(plan.VisibilityToggle);
        Assert.Null(plan.LockToggle);
    }

    private static HotkeyGesture Parse(string value)
    {
        Assert.True(HotkeyGesture.TryParseDisplayText(value, out var gesture));
        return gesture;
    }

    private sealed class FakeRegistrar : IGlobalHotkeyRegistrar
    {
        public List<string> Calls { get; } = new();

        public HotkeyGesture? FailGesture { get; set; }

        public bool FailUnregister { get; set; }

        public bool TryRegister(int id, HotkeyGesture gesture)
        {
            Calls.Add($"register:{id}");
            return gesture != FailGesture;
        }

        public bool TryUnregister(int id)
        {
            Calls.Add($"unregister:{id}");
            return !FailUnregister;
        }
    }
}
