using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Business;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests;

public sealed class M3BusinessManagerUiCorrectiveTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void BusinessManagerLayout_IsCompactAndDragHandlerIsGripOnly()
    {
        var xaml = ReadPresentation("BusinessManagerWindow.xaml");

        Assert.Contains("x:Name=\"DragGrip\"", xaml);
        Assert.Equal(1, Count(xaml, "MouseLeftButtonDown=\"OnDragMouseLeftButtonDown\""));
        Assert.Contains("Padding=\"6,4\"", xaml);
        Assert.Contains("MinHeight=\"22\" Padding=\"5,1\"", xaml);
        Assert.Contains("Foreground=\"{DynamicResource TextPrimaryBrush}\"", xaml);
        Assert.Contains("Width=\"460\"", xaml);
        Assert.Contains("MinWidth=\"420\"", xaml);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", xaml);
        Assert.Contains("x:Key=\"BusinessRowText\"", xaml);
        Assert.Contains("BasedOn=\"{StaticResource BusinessRowText}\"", xaml);
        Assert.Contains("Style=\"{StaticResource BusinessRowText}\"", xaml);
        Assert.Contains("Visibility=\"{Binding IsInteractive", xaml);
        Assert.Contains("Visibility=\"{Binding CanUseSecondary", xaml);
        Assert.Contains("Visibility=\"{Binding CanStop", xaml);
    }

    [Fact]
    public void GeneralTimerPresentation_ExistsOnlyInBusinessManager()
    {
        var main = ReadPresentation("HudWindow.xaml");
        var business = ReadPresentation("BusinessManagerWindow.xaml");
        var timerViewModel = ReadPresentation("GtaoTimerHudViewModel.cs");

        Assert.DoesNotContain("Timers.Items", main);
        Assert.DoesNotContain("Timers.IsVisible", main);
        Assert.Contains("StartGeneral12Command", business);
        Assert.Contains("StartGeneral24Command", business);
        Assert.Contains("StartGeneral48Command", business);
        Assert.Contains("public void StartGeneral(int minutes)", timerViewModel);
    }

    [Fact]
    public void OneSecondRefresh_PreservesBoundRowAndCommandInstances()
    {
        var settings = AppSettings.CreateDefault() with
        {
            BusinessAcidEnabled = true,
            BusinessAcidUpgraded = true,
        };
        using var engine = new BusinessManagerEngine(
            new SharedTimerRegistry(new MemoryTimerStore()));
        using var generalTimers = new GtaoTimerHudViewModel(
            new ResourceLocalizationService("ko"), settings, Dispatcher.CurrentDispatcher);
        using var viewModel = new BusinessManagerViewModel(
            engine,
            new RemoteOnlinePlaytimeStatusSource(settings),
            settings,
            Dispatcher.CurrentDispatcher,
            generalTimers);
        viewModel.SetUnlocked(true);

        var row = viewModel.Sections.Single().Rows[0];
        var command = row.PrimaryCommand;
        var initialRebuilds = viewModel.PresentationRebuildCount;
        for (var cycle = 0; cycle < 20; cycle++) viewModel.Refresh();

        Assert.Same(row, viewModel.Sections.Single().Rows[0]);
        Assert.Same(command, viewModel.Sections.Single().Rows[0].PrimaryCommand);
        Assert.Equal(initialRebuilds, viewModel.PresentationRebuildCount);

        command.Execute(null);
        Assert.Same(row, viewModel.Sections.Single().Rows[0]);
        Assert.NotEqual("대기", row.Status);
        Assert.True(row.CanStop);

        viewModel.SetPresentationActive(false);
        for (var cycle = 0; cycle < 20; cycle++) viewModel.Refresh();
        Assert.Equal(initialRebuilds, viewModel.PresentationRebuildCount);

        viewModel.SetPresentationActive(true);
        Assert.Equal(initialRebuilds + 1, viewModel.PresentationRebuildCount);
    }

    private static string ReadPresentation(string fileName) => File.ReadAllText(
        Path.Combine(RepositoryRoot, "src", "GachaOverlay.App", "Presentation", fileName));

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
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => [];
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries) => true;
    }
}
