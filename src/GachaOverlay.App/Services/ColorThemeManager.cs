using System.Windows;
using System.Windows.Media;
using GachaOverlay.Core.Themes;
using MediaColor = System.Windows.Media.Color;

namespace GachaOverlay.App.Services;

internal sealed class ColorThemeManager
{
    private readonly System.Windows.Application _application;
    private ResourceDictionary? _activeResources;

    public ColorThemeManager(System.Windows.Application application)
    {
        _application = application;
    }

    public event Action<ColorThemeId>? ThemeChanged;

    public ColorThemeId CurrentTheme { get; private set; } = ColorThemeCatalog.DefaultTheme;

    public void Apply(ColorThemeId requestedTheme)
    {
        var definition = ColorThemeCatalog.Get(requestedTheme);
        var resources = CreateResources(definition);
        if (_activeResources is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_activeResources);
        }

        _application.Resources.MergedDictionaries.Insert(0, resources);
        _activeResources = resources;
        CurrentTheme = definition.Id;
        ThemeChanged?.Invoke(CurrentTheme);
    }

    internal static ResourceDictionary CreateResources(ColorThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var resources = new ResourceDictionary
        {
            ["ColorTheme.Id"] = definition.Id,
        };
        foreach (var pair in definition.Colors)
        {
            var color = ParseColor(pair.Value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[$"{pair.Key}Color"] = color;
            resources[$"{pair.Key}Brush"] = brush;
        }

        // Non-zero alpha keeps the unlocked layered-window resize edges hittable.
        var editHitTestBrush = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0));
        editHitTestBrush.Freeze();
        resources["HudEditHitTestBrush"] = editHitTestBrush;

        return resources;
    }

    internal static MediaColor ResolveColor(SemanticColorToken token)
    {
        var value = System.Windows.Application.Current?.TryFindResource($"{token}Color");
        return value switch
        {
            MediaColor color => color,
            SolidColorBrush brush => brush.Color,
            _ => ParseColor(ColorThemeCatalog.Get(ColorThemeCatalog.DefaultTheme).Colors[token]),
        };
    }

    internal static SolidColorBrush CreateOpacityBrush(
        SemanticColorToken token,
        byte alpha)
    {
        var color = ResolveColor(token);
        var brush = new SolidColorBrush(MediaColor.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    internal static SolidColorBrush CreateDiscordRoleBrush(uint? rgb)
    {
        var color = rgb is > 0
            ? MediaColor.FromRgb(
                (byte)((rgb.Value >> 16) & 0xff),
                (byte)((rgb.Value >> 8) & 0xff),
                (byte)(rgb.Value & 0xff))
            : ResolveColor(SemanticColorToken.ChatNickname);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static MediaColor ParseColor(string value) =>
        (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
}
