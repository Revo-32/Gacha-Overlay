using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class M758UnifiedTextRenderingTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static IEnumerable<object[]> LongNicknameCases =>
        from text in new[]
        {
            "VeryLongNickname_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789",
            "아주긴닉네임테스트사용자_가나다라마바사아자차카타파하_0123456789",
            "とても長いニックネームのユーザー_日本語表示テスト_0123456789",
            "Mixed_사용자_日本語_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789",
        }
        from thickness in new[] { 1.5, 3, 6, 10 }
        select new object[] { text, thickness };

    [Fact]
    public void ProductionView_UsesOnlyTheUnifiedRendererForNicknameAndBody()
    {
        var presentation = Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Presentation");
        var view = File.ReadAllText(Path.Combine(presentation, "ChatMessageView.xaml"));
        var renderer = File.ReadAllText(Path.Combine(presentation, "CrispOutlinedText.cs"));

        Assert.Equal(6, view.Split("<local:CrispOutlinedText", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CrispTextOutline", view);
        Assert.DoesNotContain("ChatRichTextBlock", view);
        Assert.DoesNotContain("OutlinePaintMargin", view);
        Assert.DoesNotContain("OutlineSafePadding", view);
        Assert.DoesNotContain("Margin=\"-", view, StringComparison.Ordinal);
        Assert.Contains("TextFormatter", renderer);
        Assert.Contains("GetIndexedGlyphRuns", renderer);
        Assert.Contains("BuildGeometry", renderer);
        Assert.Contains("DrawGeometry", renderer);
        Assert.Contains("line.Line.Draw", renderer);
        Assert.DoesNotContain("FormattedText", renderer);
    }

    [Fact]
    public void ChatTextShadow_IsAbsentFromRuntimeSettingsUiThemeAndLocalization()
    {
        var runtimeFiles = new[]
        {
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Core", "Settings", "AppSettings.cs"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Core", "Chat", "ChatSettings.cs"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Core", "Chat", "ChatStylePresets.cs"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Core", "Themes", "ColorThemeCatalog.cs"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationViewModel.cs"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.App", "Presentation", "ChatMessageView.xaml"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Infrastructure", "Localization", "Resources", "Strings.resx"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Infrastructure", "Localization", "Resources", "Strings.ko.resx"),
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.Infrastructure", "Localization", "Resources", "Strings.ja.resx"),
        };
        var runtime = string.Join(Environment.NewLine, runtimeFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("ChatNicknameShadowEnabled", runtime);
        Assert.DoesNotContain("ChatMessageShadowEnabled", runtime);
        Assert.DoesNotContain("ChatShadowOpacity", runtime);
        Assert.DoesNotContain("ChatShadowDepth", runtime);
        Assert.DoesNotContain("SettingsNicknameShadow", runtime);
        Assert.DoesNotContain("SettingsMessageShadow", runtime);
        Assert.DoesNotContain("SettingsShadowOpacity", runtime);
        Assert.DoesNotContain("SettingsShadowDepth", runtime);
        Assert.DoesNotContain("DropShadowEffect", runtime);
        Assert.DoesNotContain("ChatShadow,", runtime);
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => property.Name.Contains("Shadow", StringComparison.Ordinal));
        Assert.DoesNotContain("ChatShadow", Enum.GetNames<SemanticColorToken>());
    }

    [Fact]
    public void LegacyShadowKeys_LoadSafelyAndAreRemovedOnSchemaTenPersistence()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 10,
              "language": "ko",
              "chatNicknameShadowEnabled": true,
              "chatMessageShadowEnabled": false,
              "chatShadowEnabled": true,
              "chatShadowOpacity": 0.75,
              "chatShadowStrength": 0.8,
              "chatShadowDepth": 3,
              "chatShadowOffset": 2,
              "futureSetting": { "keep": true }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();
        var persisted = File.ReadAllText(path);

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("ko", loaded.Language);
        Assert.True(loaded.ExtensionData!.ContainsKey("futureSetting"));
        foreach (var key in new[]
                 {
                     "chatNicknameShadowEnabled",
                     "chatMessageShadowEnabled",
                     "chatShadowEnabled",
                     "chatShadowOpacity",
                     "chatShadowStrength",
                     "chatShadowDepth",
                     "chatShadowOffset",
                 })
        {
            Assert.DoesNotContain(key, persisted, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("futureSetting", persisted);
    }

    [Theory]
    [MemberData(nameof(LongNicknameCases))]
    public void LongNickname_AllSupportedThicknesses_HasNoClippedPaint(
        string text,
        double thickness)
    {
        RunSta(() =>
        {
            var result = RenderText(text, thickness, 96, 1500, 120);

            Assert.True(result.OpaquePixels > 0);
            Assert.True(result.BlackPixels > 0);
            Assert.True(result.Left > 0, $"left={result.Left}");
            Assert.True(result.Top > 0, $"top={result.Top}");
            Assert.True(result.Right < result.PixelWidth - 1, $"right={result.Right}");
            Assert.True(result.Bottom < result.PixelHeight - 1, $"bottom={result.Bottom}");
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void LongMixedNickname_AtHighDpi_HasNoClippedPaint(double dpi)
    {
        RunSta(() =>
        {
            var result = RenderText(
                "Mixed_Long_사용자_日本語_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789",
                10,
                dpi,
                1500,
                120);

            Assert.True(result.BlackPixels > 0);
            Assert.True(result.Left > 0 && result.Top > 0);
            Assert.True(result.Right < result.PixelWidth - 1);
            Assert.True(result.Bottom < result.PixelHeight - 1);
        });
    }

    [Fact]
    public void BundledDefaultNicknameFont_PreservesTheFormattedAdvanceWidth()
    {
        RunSta(() =>
        {
            var typography = new ChatTypographyResolver(NullAppLogger.Instance)
                .Resolve(ChatFontPreset.Kimm);
            var control = CreateText("BloodysawABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
            control.FontFamily = typography.Nickname.FontFamily;
            control.FontWeight = typography.Nickname.FontWeight;
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 1000, Height = 100 };
            root.Children.Add(control);
            Layout(root, 1000, 100);

            var pixels = Render(root, 1000, 100, 96);
            var logicalTextWidth = control.DesiredSize.Width - 28;

            Assert.True(
                pixels.Right - pixels.Left > logicalTextWidth * 0.65,
                $"ink={pixels.Right - pixels.Left}, logical={logicalTextWidth}");
        });
    }

    [Fact]
    public void ManyAdjacentGlyphRuns_PreserveTheirIndividualHorizontalOrigins()
    {
        RunSta(() =>
        {
            const string value = "LongNicknameABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var control = CreateText(string.Empty);
            control.Tokens = value.Select(character =>
                    new ChatTokenViewModel(new ChatToken(
                        ChatTokenKind.Text,
                        character.ToString())))
                .ToArray();
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 1000, Height = 100 };
            root.Children.Add(control);
            Layout(root, 1000, 100);

            var pixels = Render(root, 1000, 100, 96);
            var logicalTextWidth = control.DesiredSize.Width - 28;

            Assert.True(
                pixels.Right - pixels.Left > logicalTextWidth * 0.65,
                $"ink={pixels.Right - pixels.Left}, logical={logicalTextWidth}");
        });
    }

    [Fact]
    public void ManyAdjacentGlyphRuns_WrapAcrossLinesWithoutHorizontalCollapse()
    {
        RunSta(() =>
        {
            var value = string.Concat(Enumerable.Repeat(
                "RunSplit_사용자_日本語_ABC123_",
                5));
            var control = CreateText(string.Empty);
            control.Tokens = value.Select(character =>
                    new ChatTokenViewModel(new ChatToken(
                        ChatTokenKind.Text,
                        character.ToString())))
                .ToArray();
            control.TextWrapping = TextWrapping.Wrap;
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 240, Height = 360 };
            root.Children.Add(control);
            Layout(root, 240, 360);

            var pixels = Render(root, 240, 360, 96);

            Assert.True(pixels.Right - pixels.Left > 150);
            Assert.True(pixels.Bottom - pixels.Top > 100);
            Assert.True(pixels.BlackPixels > 0);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void OutlinePaintChanges_DoNotRebuildLayoutOrChangeDesiredSize(double thickness)
    {
        RunSta(() =>
        {
            var control = CreateText("paint-only 설정 전환 안정성 테스트");
            control.Measure(new Size(420, 100));
            control.Arrange(new Rect(new Point(), control.DesiredSize));
            var desired = control.DesiredSize;
            var renderSize = control.RenderSize;
            var buildCount = control.LayoutBuildCount;

            control.OutlineEnabled = thickness > 0;
            control.OutlineThickness = thickness;
            control.OutlineBrush = Brushes.DarkBlue;
            control.Foreground = Brushes.Lime;
            control.Measure(new Size(420, 100));

            Assert.Equal(desired, control.DesiredSize);
            Assert.Equal(renderSize, control.RenderSize);
            Assert.Equal(buildCount, control.LayoutBuildCount);
        });
    }

    [Fact]
    public void LayoutCache_RebuildsForGeometryInputsAndNotForPaintInputs()
    {
        RunSta(() =>
        {
            var control = CreateText("cache geometry");
            control.Measure(new Size(420, 100));
            var count = control.LayoutBuildCount;

            control.OutlineEnabled = !control.OutlineEnabled;
            control.OutlineThickness = 8;
            control.OutlineBrush = Brushes.Navy;
            control.Foreground = Brushes.Lime;
            control.Measure(new Size(420, 100));
            Assert.Equal(count, control.LayoutBuildCount);

            control.FontSize += 1;
            control.Measure(new Size(420, 100));
            Assert.True(control.LayoutBuildCount > count);
            count = control.LayoutBuildCount;

            control.Text += " changed";
            control.Measure(new Size(420, 100));
            Assert.True(control.LayoutBuildCount > count);
            count = control.LayoutBuildCount;

            control.LineHeight = 36;
            control.Measure(new Size(420, 100));
            Assert.True(control.LayoutBuildCount > count);
            count = control.LayoutBuildCount;

            control.Measure(new Size(180, 100));
            Assert.True(control.LayoutBuildCount > count);
        });
    }

    [Fact]
    public void RapidOutlineToggle_KeepsFillCentroidAndLogicalBoundsStable()
    {
        RunSta(() =>
        {
            var control = CreateText("Rapid Toggle 사용자 日本語 ABC123");
            control.Foreground = Brushes.Lime;
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 720, Height = 100 };
            root.Children.Add(control);
            Layout(root, 720, 100);
            var desired = control.DesiredSize;
            var renderSize = control.RenderSize;
            var builds = control.LayoutBuildCount;
            var initial = Render(root, 720, 100, 96);

            for (var index = 0; index < 20; index++)
            {
                control.OutlineEnabled = index % 2 == 0;
                control.OutlineThickness = new[] { 1.5, 3d, 6d, 10d }[index % 4];
                var current = Render(root, 720, 100, 96);
                Assert.InRange(Math.Abs(current.FillCentroidX - initial.FillCentroidX), 0, 0.05);
                Assert.InRange(Math.Abs(current.FillCentroidY - initial.FillCentroidY), 0, 0.05);
                Assert.Equal(desired, control.DesiredSize);
                Assert.Equal(renderSize, control.RenderSize);
                Assert.Equal(builds, control.LayoutBuildCount);
            }
        });
    }

    [Theory]
    [InlineData(TextWrapping.NoWrap, TextTrimming.CharacterEllipsis)]
    [InlineData(TextWrapping.Wrap, TextTrimming.None)]
    [InlineData(TextWrapping.Wrap, TextTrimming.CharacterEllipsis)]
    public void WrappingAndTrimming_AreIndependentFromOutlinePaint(
        TextWrapping wrapping,
        TextTrimming trimming)
    {
        RunSta(() =>
        {
            var control = CreateText(
                "long wrapping and trimming text 가나다라 日本語 ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            control.TextWrapping = wrapping;
            control.TextTrimming = trimming;
            control.MaxHeight = 58;
            control.OutlineEnabled = false;
            control.Measure(new Size(180, 58));
            var before = control.DesiredSize;
            var builds = control.LayoutBuildCount;

            control.OutlineEnabled = true;
            control.OutlineThickness = 10;
            control.Measure(new Size(180, 58));

            Assert.Equal(before, control.DesiredSize);
            Assert.Equal(builds, control.LayoutBuildCount);
        });
    }

    [Fact]
    public void WideNarrowWideResize_ReturnsToTheSameLogicalSize()
    {
        RunSta(() =>
        {
            var control = CreateText(
                "resize layout 가나다라 日本語 ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            control.TextWrapping = TextWrapping.Wrap;
            control.Measure(new Size(520, 200));
            var firstWide = control.DesiredSize;
            control.Measure(new Size(160, 200));
            var narrow = control.DesiredSize;
            control.Measure(new Size(520, 200));
            var secondWide = control.DesiredSize;

            Assert.Equal(firstWide, secondWide);
            Assert.True(narrow.Height >= firstWide.Height);
        });
    }

    [Fact]
    public void MentionAndSelfMention_UseUnifiedGlyphPaintAndBackgrounds()
    {
        RunSta(() =>
        {
            var tokens = new ObservableCollection<ChatTokenViewModel>
            {
                new(new ChatToken(ChatTokenKind.Text, "hello ")),
                new(new ChatToken(ChatTokenKind.Mention, "@other", "1")),
                new(new ChatToken(ChatTokenKind.Text, " and ")),
                new(new ChatToken(ChatTokenKind.Mention, "@self", "2", IsSelfMention: true)),
            };
            var control = CreateText(string.Empty);
            control.Tokens = tokens;
            control.Foreground = Brushes.White;
            control.MentionForeground = Brushes.Yellow;
            control.SelfMentionForeground = Brushes.Lime;
            control.MentionBackground = Brushes.DarkBlue;
            control.SelfMentionBackground = Brushes.DarkRed;
            control.OutlineThickness = 3;
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 620, Height = 100 };
            root.Children.Add(control);
            Layout(root, 620, 100);

            var pixels = Render(root, 620, 100, 96);

            Assert.True(pixels.BlackPixels > 0);
            Assert.True(pixels.YellowPixels > 0);
            Assert.True(pixels.LimePixels > 0);
            Assert.True(pixels.BluePixels > 0);
            Assert.True(pixels.RedPixels > 0);
            Assert.True(
                pixels.Right - pixels.Left > control.DesiredSize.Width - 32,
                $"ink={pixels.Right - pixels.Left}, desired={control.DesiredSize.Width}");
        });
    }

    [Fact]
    public void CustomEmoji_IsRenderedButNeverReceivesTextOutline()
    {
        RunSta(() =>
        {
            var emoji = new ChatTokenViewModel(new ChatToken(ChatTokenKind.CustomEmoji, ":test:"))
            {
                Image = CreateRedEmoji(),
            };
            var control = CreateText(string.Empty);
            control.Tokens = new[] { emoji };
            control.EmojiExtent = 32;
            control.OutlineEnabled = true;
            control.OutlineThickness = 10;
            control.Margin = new Thickness(14);
            var root = new Grid { Width = 100, Height = 90 };
            root.Children.Add(control);
            Layout(root, 100, 90);

            var pixels = Render(root, 100, 90, 96);

            Assert.True(pixels.RedPixels > 0);
            Assert.Equal(0, pixels.BlackPixels);
            Assert.True(pixels.Left > 0 && pixels.Top > 0);
            Assert.True(pixels.Right < pixels.PixelWidth - 1);
            Assert.True(pixels.Bottom < pixels.PixelHeight - 1);
        });
    }

    [Fact]
    public void InlineEmoji_PaintBoundsStayInsideItsOwnChatLine()
    {
        RunSta(() =>
        {
            var emoji = new ChatTokenViewModel(new ChatToken(ChatTokenKind.CustomEmoji, ":line:"))
            {
                Image = CreateRedEmoji(),
            };
            var first = CreateText(string.Empty);
            first.Tokens = new[]
            {
                new ChatTokenViewModel(new ChatToken(ChatTokenKind.Text, "Spicat: ")),
                emoji,
            };
            first.EmojiExtent = 32;
            first.LineHeight = 24;
            first.OutlineThickness = 1.5;
            var second = CreateText("Yamada_Anna: next chat");
            second.LineHeight = 24;
            var root = new StackPanel { Width = 520, Height = 120 };
            root.Children.Add(first);
            root.Children.Add(second);
            Layout(root, 520, 120);

            var pixels = Render(root, 520, 120, 96);
            var firstBounds = BoundsIn(first, root);
            var secondBounds = BoundsIn(second, root);

            Assert.True(pixels.RedPixels > 0, "emoji was not rendered");
            Assert.True(
                pixels.RedTop >= Math.Floor(firstBounds.Top),
                $"redTop={pixels.RedTop}, firstTop={firstBounds.Top}");
            Assert.True(
                pixels.RedBottom <= Math.Ceiling(firstBounds.Bottom),
                $"redBottom={pixels.RedBottom}, firstBottom={firstBounds.Bottom}");
            Assert.True(
                pixels.RedBottom < secondBounds.Top,
                $"redBottom={pixels.RedBottom}, secondTop={secondBounds.Top}");
        });
    }

    [Fact]
    public void CompactProductionMessages_InlineEmojiNeverPaintsIntoTheNextMessage()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with
            {
                ChatLayoutMode = ChatLayoutMode.Compact,
                ChatFontSizePoints = 12.5,
                ChatLineHeightMultiplier = 1.4,
                ChatMessageSpacing = 1.25,
            };
            var role = new ResolvedChatFontRole(
                new FontFamily("Segoe UI"),
                FontWeights.Normal,
                "Segoe UI",
                ChatFontResolutionSource.System,
                false,
                null);
            var typography = new ResolvedChatTypography(
                ChatFontPreset.Kimm,
                "test",
                role,
                role);
            using var firstModel = new ChatMessageViewModel(
                new ChatMessagePresentation(
                    "emoji-row",
                    "Spicat",
                    DateTimeOffset.UtcNow,
                    new[]
                    {
                        new ChatToken(
                            ChatTokenKind.CustomEmoji,
                            ":emoji:",
                            "emoji-1"),
                    },
                    ":emoji:",
                    Array.Empty<ChatMediaCandidate>(),
                    Array.Empty<ChatStickerPresentation>(),
                    0,
                    false,
                    1,
                    1),
                new ResourceLocalizationService(),
                _ => { });
            using var secondModel = new ChatMessageViewModel(
                new ChatMessagePresentation(
                    "next-row",
                    "Yamada_Anna",
                    DateTimeOffset.UtcNow,
                    new[] { new ChatToken(ChatTokenKind.Text, "next chat") },
                    "next chat",
                    Array.Empty<ChatMediaCandidate>(),
                    Array.Empty<ChatStickerPresentation>(),
                    0,
                    false,
                    1,
                    1),
                new ResourceLocalizationService(),
                _ => { });
            firstModel.ApplySettings(settings, ChatResponsiveLevel.Full, typography);
            secondModel.ApplySettings(settings, ChatResponsiveLevel.Full, typography);
            firstModel.Tokens.Single(token => token.Kind == ChatTokenKind.CustomEmoji).Image =
                CreateRedEmoji();
            var first = new ChatMessageView { DataContext = firstModel };
            var second = new ChatMessageView { DataContext = secondModel };
            var root = new StackPanel { Width = 520, Height = 160 };
            root.Children.Add(first);
            root.Children.Add(second);
            Layout(root, 520, 160);

            var pixels = Render(root, 520, 160, 96);
            var secondBounds = BoundsIn(second, root);

            Assert.True(pixels.RedPixels > 0);
            Assert.True(
                pixels.RedBottom < secondBounds.Top,
                $"redBottom={pixels.RedBottom}, secondTop={secondBounds.Top}");
        });
    }

    [Fact]
    public void ViewportPaintGutter_IsConstantAcrossAllOutlineSettings()
    {
        var cases = new[]
        {
            AppSettings.CreateDefault() with
            {
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatNicknameOutlineThickness = 0,
                ChatMessageOutlineThickness = 0,
            },
            AppSettings.CreateDefault() with
            {
                ChatNicknameOutlineEnabled = true,
                ChatMessageOutlineEnabled = true,
                ChatNicknameOutlineThickness = 10,
                ChatMessageOutlineThickness = 10,
            },
        };

        Assert.All(cases, settings => Assert.Equal(
            new Thickness(11),
            ChatPaintSafety.CalculateViewportPadding(settings)));
    }

    [Theory]
    [InlineData(ChatLayoutMode.Balanced, "BalancedNickname")]
    [InlineData(ChatLayoutMode.Compact, "CompactNickname")]
    public void SettingsOffOnThicknessChanges_DoNotMoveTheProductionNicknameRoot(
        ChatLayoutMode layoutMode,
        string nicknameName)
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with
            {
                ChatLayoutMode = layoutMode,
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatNicknameOutlineThickness = 1.5,
                ChatMessageOutlineThickness = 1.5,
            };
            using var model = new ChatMessageViewModel(
                new ChatMessagePresentation(
                    "m758-production",
                    "LongNickname_사용자_日本語_ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                    DateTimeOffset.UtcNow,
                    new[] { new ChatToken(ChatTokenKind.Text, "message body") },
                    "message body",
                    Array.Empty<ChatMediaCandidate>(),
                    Array.Empty<ChatStickerPresentation>(),
                    0,
                    false,
                    1,
                    1),
                new ResourceLocalizationService(SupportedLocales.Korean),
                _ => { });
            var role = new ResolvedChatFontRole(
                new FontFamily("Segoe UI"),
                FontWeights.Normal,
                "Segoe UI",
                ChatFontResolutionSource.System,
                false,
                null);
            var typography = new ResolvedChatTypography(
                ChatFontPreset.Kimm,
                "test",
                role,
                role);
            model.ApplySettings(settings, ChatResponsiveLevel.Full, typography);
            var view = new ChatMessageView { DataContext = model };
            Layout(view, 760, 180);
            var nickname = Assert.IsType<CrispOutlinedText>(view.FindName(nicknameName));
            var before = BoundsIn(nickname, view);

            model.ApplySettings(settings with
            {
                ChatNicknameOutlineEnabled = true,
                ChatMessageOutlineEnabled = true,
                ChatNicknameOutlineThickness = 10,
                ChatMessageOutlineThickness = 10,
            }, ChatResponsiveLevel.Full, typography);
            Layout(view, 760, 180);
            var enabled = BoundsIn(nickname, view);

            model.ApplySettings(settings with
            {
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatNicknameOutlineThickness = 3,
                ChatMessageOutlineThickness = 3,
            }, ChatResponsiveLevel.Full, typography);
            Layout(view, 760, 180);
            var disabledAgain = BoundsIn(nickname, view);

            Assert.Equal(before, enabled);
            Assert.Equal(before, disabledAgain);
        });
    }

    private static CrispOutlinedText CreateText(string text) => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 28,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.Lime,
        OutlineBrush = Brushes.Black,
        OutlineEnabled = true,
        OutlineThickness = 1.5,
        Text = text,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static RenderPixels RenderText(
        string text,
        double thickness,
        double dpi,
        int width,
        int height)
    {
        var control = CreateText(text);
        control.OutlineThickness = thickness;
        control.Margin = new Thickness(14);
        var root = new Grid { Width = width, Height = height };
        root.Children.Add(control);
        Layout(root, width, height);
        return Render(root, width, height, dpi);
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(new Point(), element.RenderSize));

    private static RenderPixels Render(Visual visual, int width, int height, double dpi)
    {
        var pixelWidth = (int)Math.Ceiling(width * dpi / 96);
        var pixelHeight = (int)Math.Ceiling(height * dpi / 96);
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bytes = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(bytes, pixelWidth * 4, 0);
        var opaque = 0;
        var black = 0;
        var lime = 0;
        var yellow = 0;
        var blue = 0;
        var red = 0;
        var redTop = pixelHeight;
        var redBottom = -1;
        var left = pixelWidth;
        var top = pixelHeight;
        var right = -1;
        var bottom = -1;
        double fillX = 0;
        double fillY = 0;
        for (var y = 0; y < pixelHeight; y++)
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                var offset = ((y * pixelWidth) + x) * 4;
                var b = bytes[offset];
                var g = bytes[offset + 1];
                var r = bytes[offset + 2];
                var a = bytes[offset + 3];
                if (a == 0)
                {
                    continue;
                }

                opaque++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                if (a > 180 && r < 45 && g < 45 && b < 45)
                {
                    black++;
                }
                if (a > 180 && g > 180 && r < 80 && b < 80)
                {
                    lime++;
                    fillX += x;
                    fillY += y;
                }
                if (a > 180 && r > 180 && g > 180 && b < 100)
                {
                    yellow++;
                }
                if (a > 180 && b > 70 && b > r * 1.4 && b > g * 1.2)
                {
                    blue++;
                }
                if (a > 180 && r > 70 && r > g * 1.4 && r > b * 1.2)
                {
                    red++;
                    redTop = Math.Min(redTop, y);
                    redBottom = Math.Max(redBottom, y);
                }
            }
        }

        return new RenderPixels(
            opaque,
            black,
            lime,
            yellow,
            blue,
            red,
            redTop,
            redBottom,
            lime == 0 ? double.NaN : fillX / lime,
            lime == 0 ? double.NaN : fillY / lime,
            left,
            top,
            right,
            bottom,
            pixelWidth,
            pixelHeight);
    }

    private static BitmapSource CreateRedEmoji()
    {
        const int width = 16;
        const int height = 16;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            pixels[(index * 4) + 2] = 255;
            pixels[(index * 4) + 3] = 255;
        }
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
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

    private sealed record RenderPixels(
        int OpaquePixels,
        int BlackPixels,
        int LimePixels,
        int YellowPixels,
        int BluePixels,
        int RedPixels,
        int RedTop,
        int RedBottom,
        double FillCentroidX,
        double FillCentroidY,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int PixelWidth,
        int PixelHeight);
}
