using System.Windows.Media.Imaging;

namespace GachaOverlay.Tests.Presentation;

public sealed class M75ResourceTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void ModernControlDictionary_DefinesEveryRequiredControlFamilyAndState()
    {
        var path = Source("Themes", "ModernControls.xaml");
        var content = File.ReadAllText(path);

        Assert.Contains("TargetType=\"ScrollBar\"", content);
        Assert.Contains("TargetType=\"ToolTip\"", content);
        Assert.Contains("TargetType=\"TextBox\"", content);
        Assert.Contains("TargetType=\"PasswordBox\"", content);
        Assert.Contains("TargetType=\"CheckBox\"", content);
        Assert.Contains("TargetType=\"Slider\"", content);
        Assert.Contains("Style.Button.Primary", content);
        Assert.Contains("Style.Button.Secondary", content);
        Assert.Contains("Style.Button.Destructive", content);
        Assert.Contains("Validation.HasError", content);
        Assert.Contains("IsDragging", content);
    }

    [Fact]
    public void BrandingAssets_AreValidAndApplicationIconPointsToBundledIco()
    {
        var iconPath = Source("Assets", "Branding", "GachaOverlay-AppIcon.ico");
        var pngPath = Source("Assets", "Branding", "GachaOverlay-AppIcon.png");
        using var icon = new System.Drawing.Icon(iconPath);
        var decoder = BitmapDecoder.Create(
            new Uri(pngPath),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var project = File.ReadAllText(Source("GachaOverlay.App.csproj"));

        Assert.True(icon.Width > 0 && icon.Height > 0);
        Assert.NotEmpty(decoder.Frames);
        Assert.Contains("<ApplicationIcon>Assets\\Branding\\GachaOverlay-AppIcon.ico</ApplicationIcon>", project);
        Assert.Contains("<Resource Include=\"Assets\\Branding\\GachaOverlay-AppIcon.png\" />", project);
    }

    [Fact]
    public void FinalFontAssetsAndNotices_AreCompleteWithoutKoPub()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (var file in new[]
                 {
                     "PretendardVariable.ttf",
                     "KIMM_Bold.ttf",
                     "KIMM_Light.ttf",
                     "WantedSansVariable.ttf",
                     "Cafe24PROSlimMax.ttf",
                     "Cafe24PROSlimFit.ttf",
                 })
        {
            Assert.True(File.Exists(Path.Combine(output, file)), file);
        }

        var notice = File.ReadAllText(Path.Combine(
            output,
            "ThirdPartyNotices",
            "NOTICE-Fonts.txt"));
        Assert.Contains("github.com/orioncactus/pretendard", notice);
        Assert.Contains("github.com/wanteddev/wanted-sans", notice);
        Assert.DoesNotContain("KoPub", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChatMediaView_UsesBodyColumnAndNeverCropsThePreview()
    {
        var messageView = File.ReadAllText(Source("Presentation", "ChatMessageView.xaml"));
        var mediaView = File.ReadAllText(Source("Presentation", "ChatMediaView.xaml"));

        Assert.Equal(2, messageView.Split("<local:ChatMediaView", StringSplitOptions.None).Length - 1);
        Assert.Contains("Stretch=\"Uniform\"", mediaView);
        Assert.DoesNotContain("UniformToFill", mediaView);
        Assert.DoesNotContain("ShowStickerFallback", mediaView);
        Assert.DoesNotContain("StickerFallbackText", mediaView);
    }

    [Fact]
    public void M752_SettingsAndAuxiliaryWindows_UsePolishedSharedPresentationRules()
    {
        var controls = File.ReadAllText(Source("Themes", "ModernControls.xaml"));
        var settings = File.ReadAllText(Source("Presentation", "FoundationWindow.xaml"));
        var mapping = File.ReadAllText(Source("Presentation", "ProductMappingManagerWindow.xaml"));
        var mappingCode = File.ReadAllText(Source("Presentation", "ProductMappingManagerWindow.xaml.cs"));
        var preview = File.ReadAllText(Source("Presentation", "SalesPreviewWindow.xaml"));

        Assert.Contains("MinHeight=\"36\"", controls);
        Assert.Contains("Binding Tag, RelativeSource={RelativeSource TemplatedParent}", controls);
        var sliderLines = settings.Split(Environment.NewLine)
            .Where(line => line.Contains("<Slider ", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sliderLines);
        Assert.All(sliderLines, line => Assert.Contains("Tag=\"{Binding", line));
        Assert.Contains("SelectedValuePath=\"Mode\"", settings);
        Assert.Contains("TextPrimaryBrush", settings);
        Assert.DoesNotContain("<DataGrid", mapping, StringComparison.Ordinal);
        Assert.Contains("ProductNameSuggestions", mapping);
        Assert.Contains("ProductNameTextBox.Focus();", mappingCode);
        Assert.Contains("ProductNameTextBox.SelectAll();", mappingCode);
        Assert.Contains("ComboBox.ItemTemplate", preview);
        Assert.Contains("DisplayText", preview);
    }

    private static string Source(params string[] parts) => Path.Combine(
        new[] { RepositoryRoot, "src", "GachaOverlay.App" }.Concat(parts).ToArray());
}
