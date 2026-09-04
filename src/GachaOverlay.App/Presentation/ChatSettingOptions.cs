using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GachaOverlay.Core.Chat;

namespace GachaOverlay.App.Presentation;

internal sealed record TimerPresetOption(int Minutes, string DisplayText);

internal sealed record ChatLayoutModeOption(ChatLayoutMode Value, string DisplayText);

internal sealed record ChatFontPresetOption(
    ChatFontPreset Value,
    string DisplayText,
    System.Windows.Media.FontFamily PreviewFontFamily,
    FontWeight PreviewFontWeight,
    string PreviewText,
    string ResolutionStatus);

internal sealed record ChatImageModeOption(ChatImageMode Value, string DisplayText);

internal sealed record ChatImageSizeModeOption(ChatImageSizeMode Value, string DisplayText);

internal sealed record ChatLineLimitOption(int Value, string DisplayText);

internal sealed class ChatStylePresetOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public ChatStylePresetOption(
        ChatStylePreset value,
        string displayName,
        string fontName,
        string description,
        string previewNickname,
        string previewMessage,
        bool isRecommended,
        string recommendedLabel,
        ResolvedChatTypography typography,
        string resolutionStatus,
        Action<ChatStylePreset> apply)
    {
        Value = value;
        DisplayName = displayName;
        FontName = fontName;
        Description = description;
        PreviewNickname = previewNickname;
        PreviewMessage = previewMessage;
        IsRecommended = isRecommended;
        RecommendedLabel = recommendedLabel;
        NicknameFontFamily = typography.Nickname.FontFamily;
        MessageFontFamily = typography.Message.FontFamily;
        NicknameFontWeight = typography.Nickname.FontWeight;
        MessageFontWeight = typography.Message.FontWeight;
        ResolutionStatus = resolutionStatus;
        ApplyCommand = new RelayCommand(() => apply(value));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatStylePreset Value { get; }

    public string DisplayName { get; }

    public string FontName { get; }

    public string Description { get; }

    public string PreviewNickname { get; }

    public string PreviewMessage { get; }

    public bool IsRecommended { get; }

    public string RecommendedLabel { get; }

    public string ResolutionStatus { get; }

    public System.Windows.Media.FontFamily NicknameFontFamily { get; }

    public System.Windows.Media.FontFamily MessageFontFamily { get; }

    public FontWeight NicknameFontWeight { get; }

    public FontWeight MessageFontWeight { get; }

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
}
