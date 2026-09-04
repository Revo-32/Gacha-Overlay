using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Globalization;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class OptionDisplayTests
{
    [Fact]
    public void LanguageOptions_UseRequiredHumanReadableLabels()
    {
        using var fixture = new ViewModelFixture(SupportedLocales.English);

        Assert.Collection(
            fixture.ViewModel.Languages,
            option => Assert.Equal("English", option.DisplayText),
            option => Assert.Equal("한국어", option.DisplayText),
            option => Assert.Equal("日本語", option.DisplayText));
    }

    [Theory]
    [InlineData(SupportedLocales.English, "Always show", "Show only while GTA5 is active")]
    [InlineData(SupportedLocales.Korean, "항상 표시", "GTA5가 활성화되어 있을 때만 표시")]
    [InlineData(SupportedLocales.Japanese, "常に表示", "GTA5 がアクティブなときのみ表示")]
    public void VisibilityOptions_AreLocalizedHumanReadableLabels(
        string locale,
        string always,
        string gameOnly)
    {
        using var fixture = new ViewModelFixture(locale);

        Assert.Collection(
            fixture.ViewModel.VisibilityModes,
            option => Assert.Equal(always, option.DisplayText),
            option => Assert.Equal(gameOnly, option.DisplayText));
    }

    [Fact]
    public void RuntimeLanguageChange_RebuildsEveryLocalizedOptionCollection()
    {
        using var fixture = new ViewModelFixture(SupportedLocales.English);
        var oldVisibility = fixture.ViewModel.VisibilityModes;
        var oldLayouts = fixture.ViewModel.ChatLayoutModes;
        var oldFonts = fixture.ViewModel.ChatFontPresets;
        var oldImages = fixture.ViewModel.ChatImageModes;

        fixture.ViewModel.SelectedLanguage = SupportedLocales.Korean;

        Assert.NotSame(oldVisibility, fixture.ViewModel.VisibilityModes);
        Assert.NotSame(oldLayouts, fixture.ViewModel.ChatLayoutModes);
        Assert.NotSame(oldFonts, fixture.ViewModel.ChatFontPresets);
        Assert.NotSame(oldImages, fixture.ViewModel.ChatImageModes);
        Assert.Equal("항상 표시", fixture.ViewModel.VisibilityModes[0].DisplayText);
        Assert.Equal("컴팩트", fixture.ViewModel.ChatLayoutModes[0].DisplayText);
        Assert.Equal("썸네일만", fixture.ViewModel.ChatImageModes[0].DisplayText);
        Assert.Contains("현재 적용", fixture.ViewModel.ChatFontPresets[0].ResolutionStatus);
    }

    [Fact]
    public void AllOptionModels_ExposeDisplayTextWithoutTechnicalRepresentations()
    {
        using var fixture = new ViewModelFixture(SupportedLocales.English);
        var labels = fixture.ViewModel.Languages.Select(option => option.DisplayText)
            .Concat(fixture.ViewModel.VisibilityModes.Select(option => option.DisplayText))
            .Concat(fixture.ViewModel.ChatLayoutModes.Select(option => option.DisplayText))
            .Concat(fixture.ViewModel.ChatFontPresets.Select(option => option.DisplayText))
            .Concat(fixture.ViewModel.ChatImageModes.Select(option => option.DisplayText))
            .Concat(fixture.ViewModel.ChatMaxLineOptions.Select(option => option.DisplayText));

        foreach (var label in labels)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain("Option", label, StringComparison.Ordinal);
            Assert.DoesNotContain("{", label, StringComparison.Ordinal);
            Assert.DoesNotContain("pack://", label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Settings", label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvalidVisibilityValue_NormalizesToAlwaysWithReadableLabel()
    {
        using var fixture = new ViewModelFixture(SupportedLocales.English);

        fixture.ViewModel.SelectedVisibilityMode = (HudVisibilityMode)999;

        Assert.Equal(HudVisibilityMode.Always, fixture.ViewModel.SelectedVisibilityMode);
        Assert.Equal("Always show", fixture.ViewModel.VisibilityModes[0].DisplayText);
    }

    [Theory]
    [InlineData(0.9, "Percent", "90%")]
    [InlineData(12.5, "Points", "12.5 pt")]
    [InlineData(1.5, null, "1.50")]
    [InlineData(1.5, "Decimal", "1.50")]
    [InlineData(280, "Dip", "280 DIP")]
    [InlineData(1.25, "DipDecimal", "1.25 DIP")]
    public void SliderValueFormatter_UsesConsistentUserFacingFormats(
        double value,
        string? mode,
        string expected)
    {
        var converter = new SliderValueTextConverter();

        Assert.Equal(
            expected,
            converter.Convert(value, typeof(string), mode!, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void ComboBoxItemAndSelectionTemplates_RenderDisplayTextAndM81OnboardingSmoke()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var fixture = new ViewModelFixture(SupportedLocales.Korean);
                Assert.Null(System.Windows.Application.Current);
                using var application = new WpfTestApplicationScope();
                M9141ClientCorrectiveTests.AssertSettingsReuse();
                AssertM81OnboardingSmoke(application);
                var window = new FoundationWindow
                {
                    DataContext = fixture.ViewModel,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowInTaskbar = false,
                };
                window.Show();
                window.UpdateLayout();
                AssertOptionTemplate(window.LanguageComboBox, "한국어", SupportedLocales.Korean);
                AssertOptionTemplate(
                    window.VisibilityModeComboBox,
                    "항상 표시",
                    HudVisibilityMode.Always);
                fixture.ViewModel.OpenCategory(SettingsCategory.General);
                window.UpdateLayout();
                var visibilityMode = Descendants<ComboBox>(window)
                    .Single(comboBox =>
                        comboBox.Visibility == Visibility.Visible &&
                        ReferenceEquals(comboBox.ItemsSource, fixture.ViewModel.VisibilityModes));
                Assert.Equal(HudVisibilityMode.Always, visibilityMode.SelectedValue);
                Assert.Equal(
                    "항상 표시",
                    ((HudVisibilityModeOption)visibilityMode.SelectedItem).DisplayText);
                AssertOptionTemplate(visibilityMode, "항상 표시", HudVisibilityMode.Always);
                var generalHeader = Descendants<TextBlock>(window)
                    .Single(text => text.Text == fixture.ViewModel.Localization["SettingsGeneralTitle"]);
                Assert.Equal(window.FindResource("TextPrimaryBrush"), generalHeader.Foreground);
                var themeCards = Descendants<Button>(window)
                    .Where(button => button.DataContext is ColorThemeOption)
                    .ToArray();
                Assert.Equal(5, themeCards.Length);
                Assert.Single(themeCards, button => Equals(button.Tag, true));
                Assert.All(themeCards, button =>
                {
                    var option = Assert.IsType<ColorThemeOption>(button.DataContext);
                    Assert.Contains(Descendants<TextBlock>(button), text => text.Text == option.DisplayName);
                    Assert.Contains(Descendants<TextBlock>(button), text => text.Text == option.Description);
                    Assert.Equal(
                        5,
                        Descendants<Border>(button).Count(border =>
                            border.DataContext is SolidColorBrush));
                });
                var nordCard = themeCards.Single(button =>
                    Assert.IsType<ColorThemeOption>(button.DataContext).Value == ColorThemeId.Nord);
                nordCard.Command.Execute(nordCard.CommandParameter);
                window.UpdateLayout();
                Assert.Equal(ColorThemeId.Nord, fixture.ViewModel.SelectedColorTheme);
                Assert.Single(themeCards, button => Equals(button.Tag, true));
                var wideFirst = themeCards[0].TranslatePoint(new Point(), window);
                var wideSecond = themeCards[1].TranslatePoint(new Point(), window);
                Assert.Equal(wideFirst.Y, wideSecond.Y, precision: 1);

                window.Width = window.MinWidth;
                window.UpdateLayout();
                var narrowFirst = themeCards[0].TranslatePoint(new Point(), window);
                var narrowSecond = themeCards[1].TranslatePoint(new Point(), window);
                Assert.True(narrowSecond.Y > narrowFirst.Y);
                foreach (var category in Enum.GetValues<SettingsCategory>())
                {
                    fixture.ViewModel.OpenCategory(category);
                    window.UpdateLayout();
                    Assert.Equal(category, fixture.ViewModel.SelectedSettingsCategory);
                    AssertModernScrollBars(window.CategoryScrollViewer, window);
                    foreach (var slider in Descendants<Slider>(window)
                                 .Where(slider => slider.Visibility == Visibility.Visible))
                    {
                        slider.ApplyTemplate();
                        Assert.True(slider.ActualHeight >= 36);
                        Assert.False(string.IsNullOrWhiteSpace(slider.Tag?.ToString()));
                        Assert.Contains(
                            Descendants<TextBlock>(slider),
                            text => text.Text == slider.Tag?.ToString());
                        var track = Assert.Single(Descendants<Track>(slider));
                        var thumb = Assert.IsType<Thumb>(track.Thumb);
                        var thumbBounds = thumb.TransformToAncestor(slider).TransformBounds(
                            new Rect(new Point(), thumb.RenderSize));
                        Assert.True(thumbBounds.Top >= -0.1, $"Thumb top={thumbBounds.Top}");
                        Assert.True(
                            thumbBounds.Bottom <= slider.ActualHeight + 0.1,
                            $"Thumb bottom={thumbBounds.Bottom}, slider={slider.ActualHeight}");
                        foreach (var repeatButton in new[]
                                 {
                                     track.DecreaseRepeatButton,
                                     track.IncreaseRepeatButton,
                                 })
                        {
                            Assert.Equal(0, repeatButton.BorderThickness.Left);
                            Assert.Equal(0, Assert.IsType<SolidColorBrush>(repeatButton.Background).Color.A);
                        }

                        Assert.Equal(Slider.DecreaseLarge, track.DecreaseRepeatButton.Command);
                        Assert.Equal(Slider.IncreaseLarge, track.IncreaseRepeatButton.Command);
                    }
                }

                window.Height = window.MinHeight;
                fixture.ViewModel.OpenCategory(SettingsCategory.Chat);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var categoryScroller = window.CategoryScrollViewer;
                Assert.True(categoryScroller.ScrollableHeight > 0);
                var verticalBar = Descendants<ScrollBar>(categoryScroller)
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Vertical);
                AssertModernVerticalScrollBar(verticalBar, window);

                categoryScroller.ScrollToTop();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var scrollInfo = Descendants<ScrollContentPresenter>(categoryScroller)
                    .OfType<IScrollInfo>()
                    .Single();
                scrollInfo.MouseWheelDown();
                categoryScroller.InvalidateScrollInfo();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(categoryScroller.VerticalOffset > 0);

                var verticalTrack = Assert.Single(Descendants<Track>(verticalBar));
                var verticalThumb = Assert.IsType<Thumb>(verticalTrack.Thumb);
                var valueBeforeDrag = verticalBar.Value;
                var dragValue = verticalTrack.ValueFromDistance(0, 48);
                Assert.True(double.IsFinite(dragValue));
                Assert.NotEqual(0, dragValue);
                verticalBar.Value = Math.Clamp(
                    valueBeforeDrag + dragValue,
                    verticalBar.Minimum,
                    verticalBar.Maximum);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.NotEqual(valueBeforeDrag, verticalBar.Value);

                var horizontalBar = new ScrollBar
                {
                    Orientation = Orientation.Horizontal,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 40,
                    ViewportSize = 20,
                    Width = 320,
                };
                var horizontalWindow = new Window
                {
                    Content = horizontalBar,
                    Width = 360,
                    Height = 80,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowInTaskbar = false,
                };
                horizontalWindow.Show();
                horizontalWindow.UpdateLayout();
                horizontalBar.ApplyTemplate();
                horizontalWindow.UpdateLayout();
                Assert.Same(
                    horizontalWindow.FindResource("Template.ScrollBar.Horizontal"),
                    horizontalBar.Template);
                var horizontalTrack = Assert.Single(Descendants<Track>(horizontalBar));
                var horizontalThumb = Assert.IsType<Thumb>(horizontalTrack.Thumb);
                Assert.Equal(Orientation.Horizontal, horizontalTrack.Orientation);
                Assert.Equal(28, horizontalThumb.MinWidth);
                Assert.Equal(0, horizontalThumb.MinHeight);
                Assert.True(horizontalThumb.ActualWidth > horizontalThumb.ActualHeight);
                AssertNoDefaultRepeatButtonChrome(horizontalTrack);
                horizontalWindow.Close();

                fixture.ViewModel.OpenCategory(SettingsCategory.Hud);
                window.UpdateLayout();
                var opacitySlider = Descendants<Slider>(window).Single(slider =>
                    System.Windows.Data.BindingOperations.GetBinding(
                        slider,
                        Slider.ValueProperty)?.Path.Path == nameof(FoundationViewModel.HudSurfaceOpacity));
                fixture.ViewModel.HudSurfaceOpacity = 0.55;
                window.UpdateLayout();
                var opacityValueText = Assert.IsType<string>(opacitySlider.Tag);
                Assert.Contains("55", opacityValueText, StringComparison.Ordinal);
                Assert.Contains(
                    Descendants<TextBlock>(opacitySlider),
                    text => text.Text == opacityValueText);

                using var preview = new SalesPreviewViewModel(
                    fixture.ViewModel.Localization,
                    AppSettings.CreateDefault());
                var previewWindow = new SalesPreviewWindow { DataContext = preview };
                previewWindow.Show();
                previewWindow.UpdateLayout();
                Assert.IsType<SalesPreviewScenarioOption>(previewWindow.ScenarioComboBox.SelectedItem);
                Assert.NotNull(previewWindow.ScenarioComboBox.ItemTemplate);
                var scenarioText = Assert.IsType<TextBlock>(
                    previewWindow.ScenarioComboBox.ItemTemplate.LoadContent());
                Assert.Equal(
                    "DisplayText",
                    System.Windows.Data.BindingOperations.GetBinding(
                        scenarioText,
                        TextBlock.TextProperty)?.Path.Path);
                Assert.DoesNotContain(
                    nameof(SalesPreviewScenarioOption),
                    previewWindow.ScenarioComboBox.Text,
                    StringComparison.Ordinal);
                using var mappingDirectory = new TemporaryDirectory();
                var mappingViewModel = new ProductMappingManagerViewModel(
                    new JsonSalesProductCatalogStore(mappingDirectory.File("products.json")),
                    () => new[]
                    {
                        new SalesEmojiInventoryItem(
                            "100",
                            "GTA_Bunker",
                            "guild",
                            false,
                            2,
                            false),
                    },
                    _ => { },
                    fixture.ViewModel.Localization);
                var mappingWindow = new ProductMappingManagerWindow
                {
                    DataContext = mappingViewModel,
                };
                mappingWindow.Show();
                mappingWindow.UpdateLayout();
                Assert.NotEmpty(Descendants<ListBox>(mappingWindow));
                AssertModernScrollBars(mappingWindow, mappingWindow);
                mappingViewModel.SelectedInventory = mappingViewModel.Inventory[0];
                mappingViewModel.AddSelectedCommand.Execute(null);
                mappingWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                mappingWindow.UpdateLayout();

                var hudWindow = new HudWindow { Left = -10000, Top = -10000 };
                var chatProbe = new TextBlock { Text = "Theme resource probe" };
                chatProbe.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "ChatMessageBrush");
                var probeWindow = new Window
                {
                    Content = chatProbe,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowInTaskbar = false,
                };
                probeWindow.Show();
                foreach (var theme in ColorThemeCatalog.All)
                {
                    application.ApplyTheme(theme.Id);
                    hudWindow.RefreshTheme();
                    previewWindow.RefreshTheme();
                    window.UpdateLayout();
                    previewWindow.UpdateLayout();
                    mappingWindow.UpdateLayout();
                    AssertModernScrollBars(previewWindow, previewWindow);
                    AssertModernScrollBars(mappingWindow, mappingWindow);
                    Assert.Equal(
                        ParseColor(theme.Colors[SemanticColorToken.ScrollThumb]),
                        Assert.IsType<SolidColorBrush>(
                            Assert.IsType<Border>(verticalThumb.Template.FindName(
                                "ThumbSurface",
                                verticalThumb)).Background).Color);

                    var expectedBackground = ParseColor(
                        theme.Colors[SemanticColorToken.AppBackground]);
                    Assert.Equal(
                        expectedBackground,
                        Assert.IsType<SolidColorBrush>(window.Background).Color);
                    Assert.Equal(
                        expectedBackground,
                        Assert.IsType<SolidColorBrush>(previewWindow.Background).Color);
                    Assert.Equal(
                        expectedBackground,
                        Assert.IsType<SolidColorBrush>(mappingWindow.Background).Color);
                    var hudSurface = Assert.IsType<Border>(hudWindow.FindName("HudSurface"));
                    var expectedHud = ParseColor(theme.Colors[SemanticColorToken.SurfaceBase]);
                    var actualHud = Assert.IsType<SolidColorBrush>(hudSurface.Background).Color;
                    Assert.Equal(expectedHud.R, actualHud.R);
                    Assert.Equal(expectedHud.G, actualHud.G);
                    Assert.Equal(expectedHud.B, actualHud.B);
                    Assert.Equal(
                        ParseColor(theme.Colors[SemanticColorToken.ChatMessage]),
                        Assert.IsType<SolidColorBrush>(chatProbe.Foreground).Color);
                }

                probeWindow.Close();
                hudWindow.AllowClose = true;
                hudWindow.Close();
                previewWindow.Close();
                Assert.True(mappingViewModel.IsDraftMapping);
                Assert.True(mappingWindow.ProductNameTextBox.IsVisible);
                Assert.True(mappingWindow.ProductNameTextBox.Focusable);
                Assert.Empty(mappingViewModel.Mappings);
                mappingViewModel.CancelDraftCommand.Execute(null);
                mappingWindow.Close();

                window.AllowClose = true;
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Theory]
    [InlineData(SupportedLocales.English)]
    [InlineData(SupportedLocales.Korean)]
    [InlineData(SupportedLocales.Japanese)]
    public void NewLocalizationKeys_ExistInEveryLocale(string locale)
    {
        var localization = new ResourceLocalizationService(locale);
        foreach (var key in new[]
                 {
                     "SettingsVisibilityAlways",
                     "SettingsVisibilityGameOnly",
                     "SettingsFontResolvedBundled",
                     "SettingsFontResolvedSystem",
                     "SettingsFontResolvedFallback",
                     "RemoteHealthLoginRequired",
                     "DiscordStatusAuthenticationRequired",
                     "SettingsRemoteLogin",
                     "TrayDiscordConnectionSetup",
                     "TrayDiscordReconnect",
                     "SettingsResetHotkeys",
                     "SettingsProductManagerDescription",
                     "SettingsProductNameLabel",
                     "SettingsProductGroupHint",
                     "SettingsColorTheme",
                     "ColorThemeGitHubDarkDescription",
                     "ColorThemeOneDarkProDescription",
                     "ColorThemeNordDescription",
                     "ColorThemeTokyoNightDescription",
                     "ColorThemeMonokaiDescription",
                 })
        {
            Assert.NotEqual(key, localization[key]);
        }
    }

    private static void AssertM81OnboardingSmoke(WpfTestApplicationScope application)
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();
        var localization = new ResourceLocalizationService(SupportedLocales.English);
        var remoteSettings = new RemoteChatSettingsViewModel(
            localization,
            new RemoteChatSnapshot(
                "http://127.0.0.1:5188",
                RemoteChatHealthState.Live,
                "Live",
                true,
                null,
                [new RemoteChannelOption("100", "🏠메인", "1", 0, false)],
                "100"),
            _ => Task.FromResult(true),
            () => Task.CompletedTask,
            () => { },
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            _ => Task.FromResult(true));
        using var settings = new FoundationViewModel(
            store,
            localization,
            NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance),
            () => { },
            _ => { },
            () => { },
            getSalesHealthSnapshot: () => SalesFeatureHealthSnapshot.Disabled,
            remoteChatSettings: remoteSettings);
        var completed = false;
        using var onboarding = new OnboardingViewModel(
            settings,
            store,
            localization,
            () => completed = true,
            restartFromBeginning: true);
        var window = new OnboardingWindow
        {
            DataContext = onboarding,
            Left = -10000,
            Top = -10000,
            Opacity = 0,
            ShowInTaskbar = false,
        };
        window.Show();

        foreach (var theme in ColorThemeCatalog.All)
        {
            application.ApplyTheme(theme.Id);
            window.UpdateLayout();
            Assert.NotNull(window.FindResource("SurfaceRaisedBrush"));
        }

        settings.SelectedLanguage = SupportedLocales.Korean;
        Assert.Equal(0, onboarding.StepIndex);
        onboarding.NextCommand.Execute(null);
        Assert.Equal(1, onboarding.StepIndex);
        onboarding.FinishCommand.Execute(null);

        Assert.True(completed);
        Assert.Equal(AppSettings.CurrentOnboardingVersion, store.Current.OnboardingVersion);
        application.ApplyTheme(ColorThemeCatalog.DefaultTheme);
        window.Close();
    }

    private static void AssertOptionTemplate(
        ComboBox comboBox,
        string expected,
        object selectedValue)
    {
        comboBox.SelectedValue = selectedValue;
        comboBox.ApplyTemplate();
        Assert.NotNull(comboBox.ItemTemplate);
        var presenter = Assert.IsType<ContentPresenter>(
            comboBox.Template.FindName("SelectedContentPresenter", comboBox));
        Assert.Same(comboBox.ItemTemplate, presenter.ContentTemplate);
        var option = comboBox.Items.Cast<object>().First(item =>
        {
            var property = item.GetType().GetProperty("Code") ??
                item.GetType().GetProperty("Mode") ??
                item.GetType().GetProperty("Value");
            return Equals(property?.GetValue(item), selectedValue);
        });
        var text = Assert.IsType<TextBlock>(presenter.ContentTemplate.LoadContent());
        var binding = System.Windows.Data.BindingOperations.GetBinding(
            text,
            TextBlock.TextProperty);
        Assert.Equal("DisplayText", binding?.Path.Path);
        Assert.Equal(
            expected,
            option.GetType().GetProperty("DisplayText")?.GetValue(option));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertModernScrollBars(
        DependencyObject root,
        FrameworkElement resourceOwner)
    {
        foreach (var scrollViewer in Descendants<ScrollViewer>(root).Prepend(root as ScrollViewer)
                     .Where(scrollViewer => scrollViewer is not null)
                     .Cast<ScrollViewer>())
        {
            scrollViewer.ApplyTemplate();
        }

        var scrollBars = Descendants<ScrollBar>(root).ToArray();
        Assert.NotEmpty(scrollBars);
        foreach (var scrollBar in scrollBars)
        {
            scrollBar.ApplyTemplate();
            var expected = scrollBar.Orientation == Orientation.Vertical
                ? "Template.ScrollBar.Vertical"
                : "Template.ScrollBar.Horizontal";
            Assert.Same(resourceOwner.FindResource(expected), scrollBar.Template);
            var track = Assert.Single(Descendants<Track>(scrollBar));
            Assert.Equal(scrollBar.Orientation, track.Orientation);
            AssertNoDefaultRepeatButtonChrome(track);
        }
    }

    private static void AssertModernVerticalScrollBar(
        ScrollBar scrollBar,
        Window window)
    {
        scrollBar.ApplyTemplate();
        Assert.Equal(Orientation.Vertical, scrollBar.Orientation);
        Assert.Same(window.FindResource("Template.ScrollBar.Vertical"), scrollBar.Template);
        Assert.True(scrollBar.ActualHeight > scrollBar.ActualWidth);
        var track = Assert.Single(Descendants<Track>(scrollBar));
        Assert.Equal(Orientation.Vertical, track.Orientation);
        var thumb = Assert.IsType<Thumb>(track.Thumb);
        Assert.Equal(28, thumb.MinHeight);
        Assert.Equal(0, thumb.MinWidth);
        Assert.True(thumb.ActualHeight > thumb.ActualWidth);
        var thumbSurface = Assert.IsType<Border>(thumb.Template.FindName("ThumbSurface", thumb));
        Assert.Equal(6, thumbSurface.Width);
        var brush = Assert.IsType<SolidColorBrush>(thumbSurface.Background);
        Assert.NotEqual(Color.FromRgb(0, 0, 0), brush.Color);

        var point = thumbSurface.TranslatePoint(
            new Point(thumbSurface.ActualWidth / 2, thumbSurface.ActualHeight / 2),
            window);
        var hit = Assert.IsAssignableFrom<DependencyObject>(window.InputHitTest(point));
        Assert.True(HasAncestor<Thumb>(hit));
        Assert.True(HasAncestor<ScrollBar>(hit));
        AssertNoDefaultRepeatButtonChrome(track);
    }

    private static void AssertNoDefaultRepeatButtonChrome(Track track)
    {
        if (track.Orientation == Orientation.Vertical)
        {
            Assert.Equal(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);
        }
        else
        {
            Assert.Equal(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
        }

        foreach (var repeatButton in new[]
                 {
                     track.DecreaseRepeatButton,
                     track.IncreaseRepeatButton,
                 })
        {
            repeatButton.ApplyTemplate();
            Assert.Equal(0, repeatButton.BorderThickness.Left);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(repeatButton.Background).Color.A);
            Assert.DoesNotContain(
                Descendants<DependencyObject>(repeatButton),
                child => child.GetType().Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase));
            var surface = Assert.Single(Descendants<Border>(repeatButton));
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
        }
    }

    private static bool HasAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = WpfHitTestAncestry.GetParent(current);
        }

        return false;
    }


    private sealed class WpfTestApplicationScope : IDisposable
    {
        private readonly System.Windows.Application _application;
        private readonly ColorThemeManager _themeManager;

        public WpfTestApplicationScope()
        {
            _application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            _application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/GachaOverlay.App;component/Themes/DesignTokens.xaml",
                    UriKind.Absolute),
            });
            _application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/GachaOverlay.App;component/Themes/ModernControls.xaml",
                    UriKind.Absolute),
            });
            _themeManager = new ColorThemeManager(_application);
            _themeManager.Apply(ColorThemeCatalog.DefaultTheme);
        }

        public void ApplyTheme(ColorThemeId theme) => _themeManager.Apply(theme);

        public void Dispose()
        {
            foreach (Window window in _application.Windows.Cast<Window>().ToArray())
            {
                if (window is FoundationWindow foundationWindow)
                {
                    foundationWindow.AllowClose = true;
                }

                window.Close();
            }

            _application.Shutdown();
        }
    }

    private static Color ParseColor(string value) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public ViewModelFixture(string locale)
        {
            var store = new JsonSettingsStore(_directory.File("settings.json"));
            store.Load();
            var localization = new ResourceLocalizationService(locale);
            var resolver = new ChatTypographyResolver(
                NullAppLogger.Instance,
                new AllBundledCatalog());
            ViewModel = new FoundationViewModel(
                store,
                localization,
                NullAppLogger.Instance,
                resolver,
                () => { },
                _ => { },
                () => { });
        }

        public FoundationViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class AllBundledCatalog : IChatFontCatalog
    {
        public bool TryResolveBundled(
            string wpfFamilyName,
            string metadataFamilyName,
            FontWeight requestedWeight,
            string resolvedDisplayName,
            out ResolvedChatFontRole? role,
            out ChatFontFallbackReason failureReason)
        {
            role = Resolved(requestedWeight, resolvedDisplayName, ChatFontResolutionSource.Bundled);
            failureReason = default;
            return true;
        }

        public bool TryResolveSystem(
            string familyName,
            FontWeight requestedWeight,
            out ResolvedChatFontRole? role)
        {
            role = Resolved(requestedWeight, familyName, ChatFontResolutionSource.System);
            return true;
        }

        public ResolvedChatFontRole ResolveFallback(
            FontWeight requestedWeight,
            ChatFontFallbackReason reason) =>
            Resolved(requestedWeight, "Segoe UI", ChatFontResolutionSource.Fallback) with
            {
                IsFallback = true,
                FallbackReason = reason,
            };

        private static ResolvedChatFontRole Resolved(
            FontWeight weight,
            string name,
            ChatFontResolutionSource source) => new(
                new System.Windows.Media.FontFamily("Segoe UI"),
                weight,
                name,
                source,
                false,
                null);
    }
}
