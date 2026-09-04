using System.Windows.Input;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Business;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests;

public sealed class M3BusinessHotkeyUxCorrectiveTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-05T00:00:00Z");
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData(Key.F9, ModifierKeys.None, "F9")]
    [InlineData(Key.F9, ModifierKeys.Control, "Control+F9")]
    [InlineData(Key.F9, ModifierKeys.Control | ModifierKeys.Shift, "Control+Shift+F9")]
    [InlineData(Key.D1, ModifierKeys.Alt, "Alt+1")]
    public void Capture_CommitsFinalSingleOrModifierChord(
        Key key,
        ModifierKeys modifiers,
        string expected)
    {
        var model = new HotkeyCaptureModel();
        model.Begin(string.Empty);
        Assert.Equal(HotkeyCaptureResultKind.None, model.Press(key, modifiers).Kind);
        var result = model.Release(key);

        Assert.Equal(HotkeyCaptureResultKind.Commit, result.Kind);
        Assert.Equal(expected, result.Value);
        Assert.False(model.IsCapturing);
    }

    [Fact]
    public void ModifierOnly_DoesNotCommit()
    {
        var model = new HotkeyCaptureModel();
        model.Begin("F9");
        var result = model.Press(Key.LeftCtrl, ModifierKeys.Control);

        Assert.Equal(HotkeyCaptureResultKind.None, result.Kind);
        Assert.True(model.IsCapturing);
        Assert.Contains("Ctrl", model.DisplayText);
    }

    [Fact]
    public void ModifierOrder_DoesNotChangeFinalChord()
    {
        Assert.Equal(CaptureControlShift(controlFirst: true), CaptureControlShift(controlFirst: false));

        static string CaptureControlShift(bool controlFirst)
        {
            var model = new HotkeyCaptureModel();
            model.Begin(string.Empty);
            if (controlFirst)
            {
                model.Press(Key.LeftCtrl, ModifierKeys.Control);
                model.Press(Key.LeftShift, ModifierKeys.Control | ModifierKeys.Shift);
            }
            else
            {
                model.Press(Key.LeftShift, ModifierKeys.Shift);
                model.Press(Key.LeftCtrl, ModifierKeys.Control | ModifierKeys.Shift);
            }
            model.Press(Key.F9, ModifierKeys.Control | ModifierKeys.Shift);
            return model.Release(Key.F9).Value;
        }
    }

    [Fact]
    public void EscapeCancelsAndClearProducesUnassigned()
    {
        var cancel = new HotkeyCaptureModel();
        cancel.Begin("Control+F9");
        var cancelled = cancel.Press(Key.Escape, ModifierKeys.None);
        Assert.Equal(HotkeyCaptureResultKind.Cancel, cancelled.Kind);
        Assert.Equal("Control+F9", cancelled.Value);

        var clear = new HotkeyCaptureModel();
        clear.Begin("F10");
        var cleared = clear.Press(Key.Delete, ModifierKeys.None);
        Assert.Equal(HotkeyCaptureResultKind.Clear, cleared.Kind);
        Assert.Equal(string.Empty, cleared.Value);
        Assert.Equal(HotkeyCaptureBox.UnassignedText, clear.DisplayText);
    }

    [Fact]
    public void ExistingSimpleSettingStillParsesAndFinalChordConflictIsDetected()
    {
        Assert.True(HotkeyGesture.TryParse(new HotkeySetting { Key = "F9" }, out var migrated));
        Assert.Equal(HotkeyModifiers.None, migrated.Modifiers);

        var captured = Capture(Key.F9, ModifierKeys.Control);
        Assert.True(HotkeyGesture.TryParseDisplayText(captured, out var first));
        Assert.True(FoundationViewModel.HasHotkeyConflict([first, first]));
        Assert.False(FoundationViewModel.HasHotkeyConflict([
            first,
            new HotkeyGesture(HotkeyModifiers.None, 0x79),
        ]));
    }

    [Fact]
    public void GlobalDispatch_IsSuppressedOnlyDuringCapture()
    {
        Assert.True(GlobalHotkeyService.ShouldDispatch);
        using (GlobalHotkeyDispatchGate.Enter())
            Assert.False(GlobalHotkeyService.ShouldDispatch);
        Assert.True(GlobalHotkeyService.ShouldDispatch);
    }

    [Theory]
    [InlineData(BusinessHeistKind.CayoGroup)]
    [InlineData(BusinessHeistKind.Kortz)]
    public void HardTracking_IsPermanentAutomaticAccentedAndExpires(BusinessHeistKind kind)
    {
        var time = new ManualTimeProvider(Epoch);
        var settings = AppSettings.CreateDefault() with
        {
            BusinessCayoHeistEnabled = kind == BusinessHeistKind.CayoGroup,
            BusinessKortzHeistEnabled = kind == BusinessHeistKind.Kortz,
        };
        var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        var readyCount = 0;
        engine.Ready += _ => readyCount++;
        using var general = new GtaoTimerHudViewModel(
            new ResourceLocalizationService(SupportedLocales.Korean), settings,
            Dispatcher.CurrentDispatcher);
        using var business = new BusinessManagerViewModel(
            engine, new RemoteOnlinePlaytimeStatusSource(settings), settings,
            Dispatcher.CurrentDispatcher, general);
        business.SetUnlocked(true);
        var hardId = kind == BusinessHeistKind.Kortz
            ? BusinessTimerIds.KortzHardMode
            : BusinessTimerIds.CayoHardMode;
        var hard = FindRow(business, hardId);
        Assert.Equal("대기", hard.Status);

        var heist = business.Sections.Single().Rows.Single(row => row.TimerId == BusinessTimerIds.Heist(kind));
        heist.PrimaryCommand.Execute(null);
        hard = FindRow(business, hardId);
        Assert.EndsWith("후 가능", hard.Status, StringComparison.Ordinal);
        Assert.False(hard.HasAvailabilityAccent);

        time.Advance(BusinessMechanicCatalog.HeistCooldown(kind));
        business.Refresh();
        hard = FindRow(business, hardId);
        Assert.Equal("가능 ·", hard.AvailabilityAccentText);
        Assert.True(hard.IsAttentionActive);
        Assert.Equal(1, readyCount);

        var window = kind == BusinessHeistKind.Kortz
            ? BusinessMechanicCatalog.KortzHardModeWindow
            : BusinessMechanicCatalog.CayoHardModeWindow;
        time.Advance(window);
        business.Refresh();
        hard = FindRow(business, hardId);
        Assert.Equal("종료", hard.Status);
        Assert.False(hard.HasAvailabilityAccent);
        Assert.Equal(1, readyCount);
    }

    [Fact]
    public void Settings_UseCaptureControlForEveryDisplayedHotkey()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        Assert.Equal(7, Count(xaml, "<local:HotkeyCaptureBox"));
        Assert.DoesNotContain("<TextBox Text=\"{Binding VisibilityHotkeyText", xaml);
        Assert.DoesNotContain("<TextBox Text=\"{Binding LockHotkeyText", xaml);
        Assert.DoesNotContain("<TextBox Text=\"{Binding GeneralTimerHotkeyText", xaml);
        Assert.Contains("TargetType=\"{x:Type local:HotkeyCaptureBox}\" BasedOn=\"{StaticResource Style.Button.Secondary}\"", xaml);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource SurfaceRaisedBrush}\"/>", xaml);
    }

    [Fact]
    public void TimerSettings_AreConsolidatedIntoBusinessManager()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationViewModel.cs"));

        Assert.DoesNotContain("x:Key=\"TimersTemplate\"", xaml);
        Assert.DoesNotContain("CreateCategory(SettingsCategory.Timers", viewModel);
        Assert.Contains("x:Key=\"BusinessManagerTemplate\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding GeneralTimerPresets}\"", xaml);
        Assert.Contains("HotkeyText=\"{Binding GeneralTimerHotkeyText, Mode=TwoWay}\"", xaml);
        Assert.Equal(1, Count(xaml, "IsChecked=\"{Binding TimerCompletionSoundEnabled, Mode=TwoWay}\""));
        Assert.Contains("Content=\"타이머 완료 알림음\"", xaml);
    }

    private static string Capture(Key key, ModifierKeys modifiers)
    {
        var model = new HotkeyCaptureModel();
        model.Begin(string.Empty);
        model.Press(key, modifiers);
        return model.Release(key).Value;
    }

    private static BusinessTimerRowViewModel FindRow(BusinessManagerViewModel viewModel, string timerId) =>
        viewModel.Sections.SelectMany(section => section.Rows).Single(row => row.TimerId == timerId);

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
            count++;
        return count;
    }

    private sealed class MemoryTimerStore : ISharedTimerStore
    {
        private IReadOnlyList<SharedTimerPersistedEntry> _items = [];
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => _items;
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries)
        {
            _items = entries.ToArray();
            return true;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc;
        private long _timestamp;
        public ManualTimeProvider(DateTimeOffset utc) => _utc = utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed)
        {
            _utc += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
