using System.Windows.Media;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Tests.Presentation;

public sealed class M757ScrollBarTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void ScrollBarTemplates_AreSeparatedByOrientationWithSafeDimensions()
    {
        var source = ModernControls();

        Assert.Contains("x:Key=\"Template.ScrollBar.Vertical\"", source);
        Assert.Contains("x:Key=\"Template.ScrollBar.Horizontal\"", source);
        Assert.Contains("x:Key=\"Template.ScrollBar.VerticalThumb\"", source);
        Assert.Contains("x:Key=\"Template.ScrollBar.HorizontalThumb\"", source);
        Assert.Contains("Orientation=\"Vertical\"", source);
        Assert.Contains("Orientation=\"Horizontal\"", source);
        Assert.Contains("x:Name=\"ModernVerticalThumb\"", source);
        Assert.Contains("MinHeight=\"28\"", source);
        Assert.Contains("x:Name=\"ModernHorizontalThumb\"", source);
        Assert.Contains("MinWidth=\"28\"", source);
        Assert.Contains("Property=\"Height\" Value=\"Auto\"", source);
        Assert.Contains("Property=\"Width\" Value=\"Auto\"", source);
        Assert.DoesNotContain("MinHeight=\"24\" MinWidth=\"24\"", source);
    }

    [Fact]
    public void RepeatButtons_HaveTransparentHitSurfacesWithoutDefaultChrome()
    {
        var source = ModernControls();
        var pageButtonStart = source.IndexOf(
            "x:Key=\"Style.ScrollBar.PageButton\"",
            StringComparison.Ordinal);
        var pageButtonEnd = source.IndexOf(
            "x:Key=\"Template.ScrollBar.VerticalThumb\"",
            StringComparison.Ordinal);
        var pageButton = source[pageButtonStart..pageButtonEnd];

        Assert.Contains("Background\" Value=\"Transparent\"", pageButton);
        Assert.Contains("BorderThickness\" Value=\"0\"", pageButton);
        Assert.Contains("<ControlTemplate TargetType=\"RepeatButton\">", pageButton);
        Assert.Contains("<Border Background=\"Transparent\" BorderThickness=\"0\" />", pageButton);
        Assert.DoesNotContain("ButtonChrome", pageButton, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SystemColors.ControlBrush", pageButton, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollBarSemanticResources_ResolveForEveryThemeWithoutRawBlack()
    {
        foreach (var theme in ColorThemeCatalog.All)
        {
            var resources = ColorThemeManager.CreateResources(theme);
            var track = Brush(resources, SemanticColorToken.ScrollTrack);
            var thumb = Brush(resources, SemanticColorToken.ScrollThumb);
            var hover = Brush(resources, SemanticColorToken.ScrollThumbHover);
            var dragging = Brush(resources, SemanticColorToken.ScrollThumbDragging);

            Assert.NotEqual(Color.FromRgb(0, 0, 0), thumb.Color);
            Assert.NotEqual(Color.FromRgb(0, 0, 0), hover.Color);
            Assert.Equal(
                Brush(resources, SemanticColorToken.AccentPrimary).Color,
                dragging.Color);
            Assert.InRange(track.Color.A, (byte)1, (byte)254);
            Assert.All(new[] { track, thumb, hover, dragging }, brush => Assert.True(brush.IsFrozen));
        }
    }

    [Fact]
    public void SharedModernScrollbarSource_HasNoRawBlackAndCoversAllSettingsCategories()
    {
        var controls = ModernControls();
        Assert.DoesNotContain("#000000", controls, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Black", controls, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SystemColors.ControlBrush", controls, StringComparison.Ordinal);
        Assert.Contains("ScrollThumbBrush", controls);
        Assert.Contains("ScrollThumbHoverBrush", controls);
        Assert.Contains("ScrollThumbDraggingBrush", controls);
        Assert.Contains("ScrollTrackBrush", controls);

        var foundation = File.ReadAllText(Source(
            "Presentation",
            "FoundationWindow.xaml"));
        foreach (var template in new[]
                 {
                     "GeneralTemplate",
                     "DiscordTemplate",
                     "HudTemplate",
                     "ChatTemplate",
                     "MediaTemplate",
                     "SalesTemplate",
                     "HotkeysTemplate",
                     "DiagnosticsTemplate",
                 })
        {
            Assert.Contains($"x:Key=\"{template}\"", foundation);
        }

        Assert.Contains("x:Name=\"CategoryScrollViewer\"", foundation);
        Assert.Contains("x:Name=\"QueueDetailScroller\"", File.ReadAllText(Source(
            "Presentation",
            "SalesQueueView.xaml")));
        Assert.Contains("<ListBox", File.ReadAllText(Source(
            "Presentation",
            "ProductMappingManagerWindow.xaml")));
    }

    private static SolidColorBrush Brush(
        System.Windows.ResourceDictionary resources,
        SemanticColorToken token) =>
        Assert.IsType<SolidColorBrush>(resources[$"{token}Brush"]);

    private static string ModernControls() => File.ReadAllText(Source(
        "Themes",
        "ModernControls.xaml"));

    private static string Source(params string[] segments) => Path.Combine(
        new[]
        {
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
        }.Concat(segments).ToArray());
}
