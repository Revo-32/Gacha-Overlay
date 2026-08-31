using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using GachaOverlay.Core.Themes;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace GachaOverlay.App.Presentation;

internal sealed class ColorThemeOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public ColorThemeOption(
        ColorThemeDefinition definition,
        string description,
        Action<ColorThemeId> apply)
    {
        Value = definition.Id;
        DisplayName = definition.DisplayName;
        Description = description;
        Swatches = definition.Swatches
            .Select(token => CreateBrush(definition.Colors[token]))
            .ToArray();
        ApplyCommand = new RelayCommand(() => apply(Value));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ColorThemeId Value { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<MediaBrush> Swatches { get; }

    public ICommand ApplyCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public void SetSelected(bool selected) => IsSelected = selected;

    private static MediaBrush CreateBrush(string value)
    {
        var brush = new SolidColorBrush(
            (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
