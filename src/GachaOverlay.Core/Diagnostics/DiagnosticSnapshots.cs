using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.Core.Diagnostics;

public sealed record SanitizedSettingsSnapshot(
    int SchemaVersion,
    int OnboardingVersion,
    string Language,
    string ColorTheme,
    string TypographyPreset,
    string HudVisibilityMode,
    bool MinimalHudMode,
    bool SalesTrackingEnabled,
    bool WindowsAutoStart)
{
    public bool RemoteEndpointConfigured { get; init; }

    public bool RemoteChannelSelected { get; init; }

    public static SanitizedSettingsSnapshot From(AppSettings settings) => new(
        settings.SchemaVersion,
        settings.OnboardingVersion,
        settings.Language,
        settings.ColorTheme.ToString(),
        settings.ChatFontPreset.ToString(),
        settings.HudVisibilityMode.ToString(),
        settings.MinimalHudMode,
        settings.SalesTrackingEnabled,
        settings.WindowsAutoStart)
    {
        RemoteEndpointConfigured = !string.IsNullOrWhiteSpace(settings.RemoteBackendBaseUrl),
        RemoteChannelSelected = !string.IsNullOrWhiteSpace(settings.RemoteSelectedChannelId),
    };
}
