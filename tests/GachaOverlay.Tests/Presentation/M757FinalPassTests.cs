using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

public sealed class M757FinalPassTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Settings_HasExactlyTenCategoriesWithServerThirdAndDeveloperLast()
    {
        var values = Enum.GetValues<SettingsCategory>();
        Assert.Equal(10, values.Length);
        Assert.Equal(SettingsCategory.Server, values[2]);
        Assert.Equal(SettingsCategory.Developer, values[^1]);
    }

    [Theory]
    [InlineData("en", "Developer", "Developer Tools")]
    [InlineData("ko", "개발자", "개발자 도구")]
    [InlineData("ja", "開発者", "開発者ツール")]
    public void DeveloperLabels_ResolveInEverySupportedLocale(
        string locale,
        string category,
        string title)
    {
        var localization = new ResourceLocalizationService(locale);
        Assert.Equal(category, localization["SettingsCategoryDeveloper"]);
        Assert.Equal(title, localization["SettingsDeveloperTitle"]);
    }

    [Fact]
    public void DeveloperCategoryAndScrollPosition_PersistWithoutSchemaBump()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = new JsonSettingsStore(path);
        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            LastSettingsCategory = SettingsCategory.Developer,
            SettingsCategoryScrollPositions = new Dictionary<string, double>
            {
                [SettingsCategory.Developer.ToString()] = 217.5,
            },
        }));

        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(SettingsCategory.Developer, loaded.LastSettingsCategory);
        Assert.Equal(217.5, loaded.SettingsCategoryScrollPositions["Developer"]);
    }

    [Fact]
    public void SalesAndDeveloperTemplates_KeepAdvancedToolsInDeveloperOnly()
    {
        var path = Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml");
        var document = XDocument.Parse(File.ReadAllText(path));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var templates = document.Descendants(presentation + "DataTemplate")
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToDictionary(
                element => (string?)element.Attribute(xaml + "Key") ?? string.Empty,
                element => element.ToString());

        var sales = templates["SalesTemplate"];
        var developer = templates["DeveloperTemplate"];
        Assert.DoesNotContain("OpenProductMappingManagerCommand", sales);
        Assert.DoesNotContain("ExportProductMappingsCommand", sales);
        Assert.DoesNotContain("OpenSalesPreviewCommand", sales);
        Assert.Contains("OpenProductMappingManagerCommand", developer);
        Assert.Contains("ExportProductMappingsCommand", developer);
        Assert.Contains("OpenSalesPreviewCommand", developer);
        Assert.Contains("ResetProductOverridesCommand", developer);
        Assert.Contains("ShowCreditsCommand", developer);
        Assert.Contains("OpenLicenseNoticesCommand", developer);
    }

    [Fact]
    public void EveryTheme_ProvidesTheSameOpaqueBlackOutline()
    {
        foreach (var theme in ColorThemeCatalog.All)
        {
            Assert.Equal("#FF000000", theme.Colors[SemanticColorToken.ChatOutline]);
            var resources = ColorThemeManager.CreateResources(theme);
            var brush = Assert.IsType<SolidColorBrush>(
                resources[$"{SemanticColorToken.ChatOutline}Brush"]);
            Assert.Equal(Color.FromArgb(0xFF, 0, 0, 0), brush.Color);
        }
    }

    [Fact]
    public void OutlineRenderer_UsesOneGlyphLayoutForStrokeAndFillWithoutShadow()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "CrispOutlinedText.cs"));
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "ChatMessageView.xaml"));

        Assert.Contains("BuildGeometry", renderer);
        Assert.Contains("GetIndexedGlyphRuns", renderer);
        Assert.Contains("line.Line.Draw", renderer);
        Assert.Contains("OutlineThickness * 2", renderer);
        Assert.DoesNotContain("BlurEffect", renderer);
        Assert.DoesNotContain("DropShadowEffect", renderer);
        Assert.DoesNotContain("MessageOutlineBlurRadius", view);
        Assert.DoesNotContain("NicknameOutlineBlurRadius", view);
        Assert.DoesNotContain("DropShadowEffect", view);
        Assert.Equal(6, view.Split("<local:CrispOutlinedText", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void OutlineThickness_DoesNotChangeLogicalTextOriginOrWrappingWidth(double thickness)
    {
        RunSta(() =>
        {
            var text = new CrispOutlinedText
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Text = "layout origin",
                TextWrapping = TextWrapping.Wrap,
                OutlineThickness = 0,
            };
            text.Measure(new Size(180, 100));
            var before = text.DesiredSize;
            var builds = text.LayoutBuildCount;

            text.OutlineEnabled = thickness > 0;
            text.OutlineThickness = thickness;
            text.Measure(new Size(180, 100));

            Assert.Equal(before, text.DesiredSize);
            Assert.Equal(builds, text.LayoutBuildCount);
            Assert.Equal(11, ChatPaintSafety.CalculateViewportPadding(AppSettings.CreateDefault()).Left);
        });
    }

    [Fact]
    public void RenderedBlackPerimeter_ExpandsMonotonicallyWithVisibleThickness()
    {
        RunSta(() =>
        {
            var counts = new[] { 0d, 1.5, 3, 6, 10 }
                .Select(thickness => RenderOutline(thickness))
                .ToArray();

            Assert.True(counts[0].OpaquePixels > 0);
            Assert.True(counts[1].OpaquePixels > counts[0].OpaquePixels);
            Assert.True(counts[2].OpaquePixels > counts[1].OpaquePixels);
            Assert.True(counts[3].OpaquePixels > counts[2].OpaquePixels);
            Assert.True(counts[4].OpaquePixels > counts[3].OpaquePixels);
            Assert.All(counts.Skip(1), result =>
            {
                Assert.True(result.Left > 0);
                Assert.True(result.Top > 0);
                Assert.True(result.Right < result.PixelWidth - 1);
                Assert.True(result.Bottom < result.PixelHeight - 1);
            });
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void OutlineRenderer_HighDpiRenderTargetSmokeHasNoClippedEdge(double dpi)
    {
        RunSta(() =>
        {
            var result = RenderOutline(10, dpi);
            Assert.True(result.OpaquePixels > 0);
            Assert.True(result.Left > 0 && result.Top > 0);
            Assert.True(
                result.Right < result.PixelWidth - 1 &&
                result.Bottom < result.PixelHeight - 1);
        });
    }

    private static PixelBounds RenderOutline(double thickness, double dpi = 96)
    {
        var outline = new CrispOutlinedText
        {
            Margin = new Thickness(16),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            OutlineBrush = Brushes.Black,
            OutlineEnabled = true,
            OutlineThickness = thickness,
            Text = "Gacha 윤곽",
            TextWrapping = TextWrapping.NoWrap,
        };
        var root = new Grid { Width = 320, Height = 100 };
        root.Children.Add(outline);
        root.Measure(new Size(320, 100));
        root.Arrange(new Rect(0, 0, 320, 100));
        root.UpdateLayout();

        var pixelWidth = (int)Math.Ceiling(320 * dpi / 96);
        var pixelHeight = (int)Math.Ceiling(100 * dpi / 96);
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        var count = 0;
        var left = pixelWidth;
        var top = pixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < pixelHeight; y++)
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                if (pixels[((y * pixelWidth) + x) * 4 + 3] == 0)
                {
                    continue;
                }

                count++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return new PixelBounds(
            count,
            left,
            top,
            right,
            bottom,
            pixelWidth,
            pixelHeight);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    private sealed record PixelBounds(
        int OpaquePixels,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int PixelWidth,
        int PixelHeight);
}
