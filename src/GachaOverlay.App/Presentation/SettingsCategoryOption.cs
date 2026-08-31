using GachaOverlay.Core.Settings;
using System.Windows.Media;

namespace GachaOverlay.App.Presentation;

internal sealed record SettingsCategoryOption(
    SettingsCategory Value,
    string DisplayText,
    Geometry Icon);
