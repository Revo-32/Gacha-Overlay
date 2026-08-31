using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;


internal sealed class ChatMessageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Action<ChatMessageViewModel> _previewRequested;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource _enrichmentCancellation = new();
    private string _authorName = string.Empty;
    private string _plainText = string.Empty;
    private IReadOnlyList<ChatToken> _sourceTokens = Array.Empty<ChatToken>();
    private ChatMediaCandidate? _primaryMedia;
    private string _timeText = string.Empty;
    private ImageSource? _thumbnail;
    private ImageSource? _stickerImage;
    private string _stickerFallbackText = string.Empty;
    private string? _stickerName;
    private DiscordMessageFallbackKind _fallbackKind;
    private bool _hasSticker;
    private double _stickerExtent;
    private int _additionalMediaCount;
    private int _revision;
    private long _generation;
    private System.Windows.Media.FontFamily _nicknameFontFamily = new("Segoe UI");
    private System.Windows.Media.FontFamily _messageFontFamily = new("Segoe UI");
    private FontWeight _nicknameFontWeight = FontWeights.Bold;
    private FontWeight _messageFontWeight = FontWeights.Normal;
    private double _fontSizeDip = 16;
    private bool _isCompact;
    private bool _showTime;
    private bool _showImages;
    private bool _canEnlarge;
    private bool _showNicknameOutline;
    private bool _showMessageOutline;
    private double _nicknameOutlineThickness;
    private double _messageOutlineThickness;
    private bool _hidePreviewSourceUrl;
    private bool _stickersEnabled = true;
    private double _thumbnailWidth = 132;
    private double _thumbnailMaxHeight = 96;
    private int _maxLines;
    private bool _isUltraCompact;
    private double _lineHeight;
    private Thickness _messageMargin;
    private int _typographyRevision;

    public ChatMessageViewModel(
        ChatMessagePresentation presentation,
        ILocalizationService localization,
        Action<ChatMessageViewModel> previewRequested)
    {
        MessageId = presentation.MessageId;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        _previewRequested = previewRequested;
        PreviewCommand = new RelayCommand(() => _previewRequested(this));
        Update(presentation);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MessageId { get; }

    public ObservableCollection<ChatTokenViewModel> Tokens { get; } = new();

    public ICommand PreviewCommand { get; }

    public CancellationToken EnrichmentToken => _enrichmentCancellation.Token;

    public string AuthorName { get => _authorName; private set => SetField(ref _authorName, value); }

    public string PlainText { get => _plainText; private set => SetField(ref _plainText, value); }

    public string TimeText { get => _timeText; private set => SetField(ref _timeText, value); }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetField(ref _thumbnail, value))
            {
                RefreshDisplayContent();
                OnPropertyChanged(nameof(HasVisibleMedia));
            }
        }
    }

    public ImageSource? StickerImage
    {
        get => _stickerImage;
        set
        {
            if (SetField(ref _stickerImage, value))
            {
                OnPropertyChanged(nameof(ShowStickerImage));
                OnPropertyChanged(nameof(ShowStickerFallback));
                OnPropertyChanged(nameof(HasVisibleMedia));
                RefreshDisplayContent();
            }
        }
    }

    public string StickerFallbackText
    {
        get => _stickerFallbackText;
        private set => SetField(ref _stickerFallbackText, value);
    }

    public bool HasSticker
    {
        get => _hasSticker;
        private set
        {
            if (SetField(ref _hasSticker, value))
            {
                OnPropertyChanged(nameof(ShowStickerImage));
                OnPropertyChanged(nameof(ShowStickerFallback));
                OnPropertyChanged(nameof(HasVisibleMedia));
                RefreshDisplayContent();
            }
        }
    }

    public bool ShowStickerImage => HasSticker && ShowImages && StickerExtent > 0 && StickerImage is not null;

    public bool ShowStickerFallback =>
        _stickersEnabled &&
        (_fallbackKind == DiscordMessageFallbackKind.Sticker || HasSticker && !ShowStickerImage);

    public bool HasVisibleMedia =>
        ShowStickerImage ||
        ShowImages && (Thumbnail is not null || HasAdditionalMedia);

    public double StickerExtent
    {
        get => _stickerExtent;
        private set
        {
            if (SetField(ref _stickerExtent, value))
            {
                OnPropertyChanged(nameof(ShowStickerImage));
                OnPropertyChanged(nameof(ShowStickerFallback));
                OnPropertyChanged(nameof(HasVisibleMedia));
                RefreshDisplayContent();
            }
        }
    }

    public int AdditionalMediaCount
    {
        get => _additionalMediaCount;
        private set
        {
            if (SetField(ref _additionalMediaCount, value))
            {
                OnPropertyChanged(nameof(AdditionalMediaText));
                OnPropertyChanged(nameof(HasAdditionalMedia));
                OnPropertyChanged(nameof(HasVisibleMedia));
            }
        }
    }

    public string AdditionalMediaText => $"+{AdditionalMediaCount}";

    public bool HasAdditionalMedia => AdditionalMediaCount > 0;

    public int Revision { get => _revision; private set => SetField(ref _revision, value); }

    public long Generation { get => _generation; private set => SetField(ref _generation, value); }

    public ChatEnrichmentIdentity EnrichmentIdentity => new(MessageId, Generation, Revision);

    public System.Windows.Media.FontFamily NicknameFontFamily
    {
        get => _nicknameFontFamily;
        private set => SetField(ref _nicknameFontFamily, value);
    }

    public System.Windows.Media.FontFamily MessageFontFamily
    {
        get => _messageFontFamily;
        private set => SetField(ref _messageFontFamily, value);
    }

    public FontWeight NicknameFontWeight
    {
        get => _nicknameFontWeight;
        private set => SetField(ref _nicknameFontWeight, value);
    }

    public FontWeight MessageFontWeight
    {
        get => _messageFontWeight;
        private set => SetField(ref _messageFontWeight, value);
    }

    public double FontSizeDip { get => _fontSizeDip; private set => SetField(ref _fontSizeDip, value); }

    public double LineHeight { get => _lineHeight; private set => SetField(ref _lineHeight, value); }

    public double EmojiExtent => ChatVisualMetrics.CalculateEmojiExtent(FontSizeDip, LineHeight);

    public int TypographyRevision
    {
        get => _typographyRevision;
        private set => SetField(ref _typographyRevision, value);
    }

    public Thickness MessageMargin { get => _messageMargin; private set => SetField(ref _messageMargin, value); }

    public double MessageMaxHeight => LineHeight * (IsUltraCompact ? 1 : MaxLines);

    public bool IsCompact
    {
        get => _isCompact;
        private set
        {
            if (SetField(ref _isCompact, value))
            {
                OnPropertyChanged(nameof(IsBalanced));
            }
        }
    }

    public bool IsBalanced => !IsCompact && !IsUltraCompact;

    public bool IsUltraCompact
    {
        get => _isUltraCompact;
        private set
        {
            if (SetField(ref _isUltraCompact, value))
            {
                OnPropertyChanged(nameof(IsBalanced));
                OnPropertyChanged(nameof(MessageMaxHeight));
            }
        }
    }

    public bool ShowTime { get => _showTime; private set => SetField(ref _showTime, value); }

    public bool ShowImages
    {
        get => _showImages;
        private set
        {
            if (SetField(ref _showImages, value))
            {
                OnPropertyChanged(nameof(ShowStickerImage));
                OnPropertyChanged(nameof(ShowStickerFallback));
                OnPropertyChanged(nameof(HasVisibleMedia));
                RefreshDisplayContent();
            }
        }
    }

    public bool CanEnlarge { get => _canEnlarge; private set => SetField(ref _canEnlarge, value); }

    public bool ShowNicknameOutline { get => _showNicknameOutline; private set => SetField(ref _showNicknameOutline, value); }

    public bool ShowMessageOutline { get => _showMessageOutline; private set => SetField(ref _showMessageOutline, value); }

    public double NicknameOutlineThickness
    {
        get => _nicknameOutlineThickness;
        private set => SetField(ref _nicknameOutlineThickness, value);
    }

    public double MessageOutlineThickness
    {
        get => _messageOutlineThickness;
        private set => SetField(ref _messageOutlineThickness, value);
    }

    public double ThumbnailWidth { get => _thumbnailWidth; private set => SetField(ref _thumbnailWidth, value); }

    public double ThumbnailMaxHeight { get => _thumbnailMaxHeight; private set => SetField(ref _thumbnailMaxHeight, value); }

    public int MaxLines
    {
        get => _maxLines;
        private set
        {
            if (SetField(ref _maxLines, value))
            {
                OnPropertyChanged(nameof(MessageMaxHeight));
            }
        }
    }

    public void Update(ChatMessagePresentation presentation)
    {
        CancelEnrichment();
        Revision = presentation.Revision;
        Generation = presentation.Generation;
        AuthorName = presentation.AuthorName;
        _sourceTokens = presentation.Tokens.ToArray();
        _fallbackKind = presentation.FallbackKind;
        _primaryMedia = presentation.Media.FirstOrDefault();
        TimeText = presentation.CreatedAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;
        AdditionalMediaCount = presentation.AdditionalMediaCount;
        Thumbnail = null;
        StickerImage = null;
        HasSticker = _stickersEnabled && presentation.Stickers.Count > 0;
        _stickerName = presentation.Stickers.Count == 0
            ? null
            : presentation.Stickers[0].Name;
        RefreshStickerFallback();
        RefreshDisplayContent();
    }

    public void ApplySettings(
        AppSettings settings,
        ChatResponsiveLevel responsiveLevel,
        ResolvedChatTypography typography)
    {
        var typographyChanged =
            !Equals(NicknameFontFamily, typography.Nickname.FontFamily) ||
            !Equals(MessageFontFamily, typography.Message.FontFamily) ||
            NicknameFontWeight != typography.Nickname.FontWeight ||
            MessageFontWeight != typography.Message.FontWeight;
        NicknameFontFamily = typography.Nickname.FontFamily;
        MessageFontFamily = typography.Message.FontFamily;
        NicknameFontWeight = typography.Nickname.FontWeight;
        MessageFontWeight = typography.Message.FontWeight;
        if (typographyChanged)
        {
            TypographyRevision++;
        }

        FontSizeDip = settings.ChatFontSizePoints * 96d / 72d;
        LineHeight = ChatVisualMetrics.CalculateLineHeight(
            FontSizeDip,
            settings.ChatLineHeightMultiplier);
        OnPropertyChanged(nameof(EmojiExtent));
        MessageMargin = new Thickness(0, 0, 0, Math.Max(0, settings.ChatMessageSpacing));
        OnPropertyChanged(nameof(MessageMaxHeight));
        var layout = ChatLayoutPresentation.Resolve(settings, responsiveLevel);
        IsCompact = layout.IsCompact;
        OnPropertyChanged(nameof(IsBalanced));
        IsUltraCompact = layout.IsUltraCompact;
        ShowTime = layout.ShowTime;
        ShowImages = layout.ShowImages;
        _stickersEnabled = settings.ChatStickerEnabled;
        HasSticker = _stickersEnabled && _stickerName is not null;
        OnPropertyChanged(nameof(ShowStickerFallback));
        RefreshDisplayContent();
        var largeMedia = settings.ChatImageSizeMode == ChatImageSizeMode.Large;
        ThumbnailWidth = IsUltraCompact ? 0 : largeMedia ? 360 : 132;
        ThumbnailMaxHeight = IsUltraCompact ? 0 : largeMedia ? 270 : 96;
        StickerExtent = layout.ShowImages
            ? ChatVisualMetrics.CalculateStickerExtent(responsiveLevel, largeMedia)
            : 0;
        CanEnlarge = layout.CanEnlarge;
        ShowNicknameOutline = settings.ChatNicknameOutlineEnabled;
        ShowMessageOutline = settings.ChatMessageOutlineEnabled;
        NicknameOutlineThickness = settings.ChatNicknameOutlineThickness;
        MessageOutlineThickness = settings.ChatMessageOutlineThickness;
        if (_hidePreviewSourceUrl != settings.HidePreviewSourceUrl)
        {
            _hidePreviewSourceUrl = settings.HidePreviewSourceUrl;
            RefreshDisplayContent();
        }
        MaxLines = settings.ChatMaxLines;
    }

    public void RestartEnrichment() => CancelEnrichment();

    public bool IsCurrent(ChatEnrichmentIdentity expected) =>
        ChatEnrichmentGuard.IsCurrent(
            expected,
            EnrichmentIdentity,
            !_enrichmentCancellation.IsCancellationRequested);

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        CancelEnrichment(permanent: true);
    }

    private void CancelEnrichment(bool permanent = false)
    {
        _enrichmentCancellation.Cancel();
        _enrichmentCancellation.Dispose();
        if (!permanent)
        {
            _enrichmentCancellation = new CancellationTokenSource();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        RefreshStickerFallback();
        RefreshDisplayContent();
    }

    private void RefreshStickerFallback()
    {
        StickerFallbackText = _localization["ChatStickerFallbackUnnamed"];
    }

    private void RefreshDisplayContent()
    {
        var enrichedEmoji = Tokens
            .Where(token => token.Kind == ChatTokenKind.CustomEmoji &&
                            token.Identity is not null &&
                            token.Image is not null)
            .GroupBy(token => token.Identity!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Image!, StringComparer.Ordinal);

        Tokens.Clear();
        var displayTokens = _fallbackKind switch
        {
            DiscordMessageFallbackKind.ForwardedMessage =>
            [
                new(ChatTokenKind.Text, _localization["ChatForwardFallback"]),
            ],
            DiscordMessageFallbackKind.Message =>
            [
                new(ChatTokenKind.Text, _localization["ChatMessageFallback"]),
            ],
            _ => _sourceTokens.ToList(),
        };
        if (_fallbackKind is not (
                DiscordMessageFallbackKind.ForwardedMessage or
                DiscordMessageFallbackKind.Message) &&
            ShowStickerFallback)
        {
            displayTokens.RemoveAll(token =>
                token.Kind == ChatTokenKind.Text && string.IsNullOrWhiteSpace(token.Text));
            var prefix = displayTokens.Count == 0 ? string.Empty : " ";
            displayTokens.Add(new ChatToken(
                ChatTokenKind.Text,
                prefix + _localization["ChatStickerFallbackUnnamed"]));
        }
        foreach (var source in displayTokens)
        {
            var token = source;
            if (source.Kind == ChatTokenKind.Text && _primaryMedia is not null)
            {
                var filtered = ChatMediaSourcePolicy.SuppressExactSourceToken(
                    source.Text,
                    _primaryMedia,
                    Thumbnail is not null,
                    _hidePreviewSourceUrl);
                if (filtered.Length == 0)
                {
                    continue;
                }

                token = source with { Text = filtered };
            }

            var viewModel = new ChatTokenViewModel(token);
            if (viewModel.Identity is not null &&
                enrichedEmoji.TryGetValue(viewModel.Identity, out var image))
            {
                viewModel.Image = image;
            }

            Tokens.Add(viewModel);
        }

        PlainText = string.Concat(Tokens.Select(token => token.Text));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
