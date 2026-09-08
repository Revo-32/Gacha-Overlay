using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests;

public sealed class GtaWeeklyEventPresentationTests
{
    internal static GtaCompanionWeek RealWeek()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<GtaCompanionWeek>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Gta", "weekly-production-display-2026-09-08.json")), options)!;
    }

    [Fact]
    public void ActualProductionWeek_OneHeadingPerRate_All44ItemsAndConditionsPreserved()
    {
        var week = RealWeek();
        var before = JsonSerializer.Serialize(week);
        var groups = GtaWeeklyEventPresentation.Build(week);
        var rows = groups.SelectMany(group => group.Items).ToArray();
        var originals = week.Bonuses.Concat(week.Discounts).Concat(week.FreeItems).Concat(week.OtherEvents).ToArray();
        Assert.Equal(44, rows.Length);
        Assert.Equal(originals.Select(item => item.ItemKey).Order(), rows.Select(item => item.ItemKey).Order());
        Assert.Equal(before, JsonSerializer.Serialize(week));
        Assert.Equal(new[] { "4배 GTA$ · RP", "3배 GTA$ · RP", "2배 GTA$ · RP", "2배 속도" },
            groups.Take(4).Select(group => group.Heading));
        Assert.Equal(new[] { "마드라조 청부 살인", "혼합 상품 수출 임무 (임원 전용 오피스 비서)", "다이아몬드 적대적 모드 시리즈" },
            Assert.Single(groups, group => group.Heading == "2배 GTA$ · RP").Items.Select(item => item.DisplayText));
        Assert.Equal("직원 자원 확보 스페셜 패키지", Assert.Single(
            Assert.Single(groups, group => group.Heading == "2배 속도").Items).DisplayText);
        var discount30 = Assert.Single(groups, group => group.Heading == "30% 할인");
        Assert.Equal(16, discount30.Items.Count);
        Assert.All(discount30.Items, item => Assert.DoesNotContain("할인", item.DisplayText));
        Assert.Equal("Coil Cyclone II", Assert.Single(Assert.Single(groups, group => group.Heading == "70% 할인").Items).DisplayText);
        Assert.Equal("스턴 건", Assert.Single(Assert.Single(groups, group => group.Heading == "30% 할인 · GTA+ 회원").Items).DisplayText);
        Assert.Equal("프리시전 라이플", Assert.Single(Assert.Single(groups, group => group.Heading == "50% 할인").Items).DisplayText);
        Assert.All(week.OtherEvents, original => Assert.Equal(original.DisplayTextKo,
            Assert.Single(rows, row => row.ItemKey == original.ItemKey).DisplayText));
        // The actual repeated-label layout shrinks without dropping a single item.
        var oldLines = originals.Sum(item => item.DisplayTextKo.Split('\n').Length);
        var newLines = groups.Count + rows.Sum(item => item.DisplayText.Split('\n').Length);
        Assert.True(newLines < oldLines, $"Before: {oldLines}; after: {newLines}");
    }

    [Theory]
    [InlineData("활동 이름\n2배 GTA 달러와 RP", "활동 이름")]
    [InlineData("활동 이름: 2배 GTA 달러와 RP", "활동 이름")]
    [InlineData("2배 GTA$ + RP: 활동 이름", "활동 이름")]
    [InlineData("활동 이름 · 2배 GTA$ + RP", "활동 이름")]
    [InlineData("활동 이름\nGTA 달러와 RP 2배", "활동 이름")]
    [InlineData("활동 이름: 첫 완료 시만 GTA$ 200,000", "활동 이름: 첫 완료 시만 GTA$ 200,000")]
    [InlineData("활동 이름\n2배 GTA 달러와 RP (GTA+ 회원 전용)", "활동 이름\n2배 GTA 달러와 RP (GTA+ 회원 전용)")]
    public void OnlyKnownBoundaryHeadingIsRemoved(string display, string expected)
    {
        var week = RealWeek();
        var item = week.Bonuses[2] with { DisplayTextKo = display };
        var rows = GtaWeeklyEventPresentation.Build(Only(week, item)).SelectMany(group => group.Items);
        Assert.Equal(expected, Assert.Single(rows).DisplayText);
    }

    [Fact]
    public void NonConsecutiveRatesGroupTogether_ButDifferentDatesAndRestrictionsDoNotMerge()
    {
        var week = RealWeek();
        var normal = week.Bonuses[2];
        var restricted = normal with { ItemKey = "restricted", OriginalLabel = "2X GTA$ & RP (GTA+ ONLY)\nActivity", DisplayTextKo = "GTA+ 전용 활동 2배" };
        var later = normal with { ItemKey = "later", EffectiveFrom = week.EffectiveFrom.AddDays(1) };
        var groups = GtaWeeklyEventPresentation.Build(week with
        {
            Bonuses = new[] { normal, week.Bonuses[0], week.Bonuses[3], later, restricted },
            Discounts = [], FreeItems = [], OtherEvents = [],
        });
        Assert.Equal(2, groups.Count(group => group.Heading == "2배 GTA$ · RP"));
        Assert.Equal(2, groups.First(group => group.Heading == "2배 GTA$ · RP").Items.Count);
        Assert.Equal(restricted.DisplayTextKo, Assert.Single(groups.Single(group => group.Heading == "기타 보너스").Items).DisplayText);
    }

    [Fact]
    public void ActualXaml_NarrowAndWideLayoutsHaveSingleHeadingsAndWrappedRows()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var week = RealWeek();
            var window = new GtaCompanionWindow { AllowClose = true };
            try
            {
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources["GtaCompanionCardSurfaceBrush"] = new SolidColorBrush(Color.FromRgb(16, 20, 25));
                root.Resources["TextSecondaryBrush"] = Brushes.LightGray;
                root.Resources["TextPrimaryBrush"] = Brushes.White;
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
                style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
                root.Resources[typeof(TextBlock)] = style;
                root.DataContext = new
                {
                    WeeklyEventGroups = GtaWeeklyEventPresentation.Build(week),
                    ShowWeeklyEvents = true, ShowDaily = false, ShowWeekly = false,
                    HasCampaign = false, IsInteractive = false, ClientDetectionText = string.Empty,
                };
                foreach (var width in new[] { 360, 520 })
                {
                    root.Measure(new Size(width, 1100));
                    root.Arrange(new Rect(0, 0, width, 1100));
                    root.UpdateLayout();
                    var groupsControl = Descendants(root).OfType<ItemsControl>().Single(control =>
                        BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path == "WeeklyEventGroups");
                    var texts = Descendants(groupsControl).OfType<TextBlock>().ToArray();
                    Assert.Single(texts, text => text.Text == "30% 할인");
                    Assert.Single(texts, text => text.Text == "2배 GTA$ · RP");
                    Assert.Equal(44, texts.Count(text => text.Text.StartsWith("• ", StringComparison.Ordinal)));
                    Assert.All(texts, text =>
                    {
                        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                        Assert.True(text.ActualWidth > 0 && text.ActualWidth < width);
                        Assert.True(text.ActualHeight > 0);
                        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                    });
                    var before = new StackPanel();
                    var rowStyle = texts.First(text => text.Text.StartsWith("• ", StringComparison.Ordinal));
                    foreach (var item in week.Bonuses.Concat(week.Discounts).Concat(week.FreeItems).Concat(week.OtherEvents))
                        before.Children.Add(new TextBlock
                        {
                            Text = "• " + item.DisplayTextKo, FontSize = rowStyle.FontSize,
                            FontFamily = rowStyle.FontFamily, FontWeight = rowStyle.FontWeight,
                            FontStyle = rowStyle.FontStyle, FontStretch = rowStyle.FontStretch,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
                        });
                    before.Measure(new Size(groupsControl.ActualWidth, double.PositiveInfinity));
                    // Optional synthetic preview of the actual XAML; no running app, login, or user settings touched.
                    var directory = Environment.GetEnvironmentVariable("LSO_WEEKLY_PREVIEW_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                        var image = new RenderTargetBitmap(width, 1100, 96, 96, PixelFormats.Pbgra32);
                        image.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using var stream = File.Create(Path.Combine(directory, $"weekly-grouped-{width}.png"));
                        encoder.Save(stream);
                    }
                    Assert.True(groupsControl.DesiredSize.Height < before.DesiredSize.Height,
                        $"{width} DIP: grouped {groupsControl.DesiredSize.Height}, previous {before.DesiredSize.Height}");
                }
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void NullAndEmptyWeekHaveNoGroups()
    {
        Assert.Empty(GtaWeeklyEventPresentation.Build(null));
        Assert.Empty(GtaWeeklyEventPresentation.Build(RealWeek() with { Bonuses = [], Discounts = [], FreeItems = [], OtherEvents = [] }));
    }

    private static GtaCompanionWeek Only(GtaCompanionWeek week, GtaCompanionItem item) =>
        week with { Bonuses = new[] { item }, Discounts = [], FreeItems = [], OtherEvents = [] };
}
