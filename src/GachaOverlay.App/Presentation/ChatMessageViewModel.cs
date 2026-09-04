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
using GachaOverlay.App.Services;

namespace GachaOverlay.App.Presentation;


internal sealed class ChatMessageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Action<ChatMessageViewModel> _previewRequested;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource _enrichmentCancellation = new();
    private string _authorName = string.Empty;
    private string _plainText = string.Empty;
    private string _replyText = string.Empty;
    private string _remoteDetailsText = string.Empty;
    private DiscordRemoteMessageMetadata? _remoteMetadata;
    private IReadOnlyList<DiscordAttachmentMetadata> _remoteAttachments =
        Array.Empty<DiscordAttachmentMetadata>();
    private IReadOnlyList<DiscordEmbedMetadata> _remoteEmbeds =
        Array.Empty<DiscordEmbedMetadata>();
    private IReadOnlyList<ChatToken> _sourceTokens = Array.Empty<ChatToken>();
    private ChatMediaCandidate? _primaryMedia;
    private string _timeText = string.Empty;
    private ImageSource? _thumbnail;
    private ImageSource? _stickerImage;
    private string _stickerFallbackText = string.Empty;
    private string? _stickerName;
    private bool _hasStructuredRemoteSticker;
    private DiscordMessageFallbackKind _fallbackKind;
    private bool _hasSticker;
    private bool _hasPrimaryText;
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
    private bool _showAuthorHeader = true;
    private System.Windows.Media.Brush _nicknameBrush =
        ColorThemeManager.CreateDiscordRoleBrush(null);
    private string _roleIconText = string.Empty;
    private ImageSource? _roleIconImage;
    private string? _roleIconUrl;
    private RoleIconPosition _roleIconPosition = RoleIconPosition.Left;

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

    public ObservableCollection<ChatForwardMessageViewModel> ForwardedMessages { get; } = new();

    public ObservableCollection<ChatReactionViewModel> Reactions { get; } = new();

    public ICommand PreviewCommand { get; }

    public CancellationToken EnrichmentToken => _enrichmentCancellation.Token;

    public string AuthorName { get => _authorName; private set => SetField(ref _authorName, value); }

    public string AuthorId { get; private set; } = string.Empty;

    public bool ShowAuthorHeader
    {
        get => _showAuthorHeader;
        set => SetField(ref _showAuthorHeader, value);
    }

    public System.Windows.Media.Brush NicknameBrush
    {
        get => _nicknameBrush;
        private set => SetField(ref _nicknameBrush, value);
    }

    public string RoleIconText
    {
        get => _roleIconText;
        private set
        {
            if (SetField(ref _roleIconText, value))
            {
                OnPropertyChanged(nameof(HasRoleIconText));
                NotifyRoleIconPlacementChanged();
            }
        }
    }

    public bool HasRoleIconText => !string.IsNullOrWhiteSpace(RoleIconText);

    public ImageSource? RoleIconImage
    {
        get => _roleIconImage;
        set
        {
            if (SetField(ref _roleIconImage, value))
            {
                OnPropertyChanged(nameof(HasRoleIconImage));
                NotifyRoleIconPlacementChanged();
            }
        }
    }

    public bool HasRoleIconImage => RoleIconImage is not null;

    public bool ShowRoleIconLeft =>
        _roleIconPosition == RoleIconPosition.Left && (HasRoleIconText || HasRoleIconImage);

    public bool IsRoleIconAdjacentRight =>
        _roleIconPosition == RoleIconPosition.AdjacentRight;

    public bool UseAnchoredRoleIconLayout => !IsRoleIconAdjacentRight;

    public bool ShowRoleIconAdjacentRight =>
        IsRoleIconAdjacentRight && (HasRoleIconText || HasRoleIconImage);

    public bool ShowRoleIconFarRight =>
        _roleIconPosition == RoleIconPosition.FarRight && (HasRoleIconText || HasRoleIconImage);

    public bool ShowRoleIconRight => ShowRoleIconAdjacentRight || ShowRoleIconFarRight;

    public GridLength RoleIconNicknameColumnWidth => IsRoleIconAdjacentRight
        ? GridLength.Auto
        : new GridLength(1, GridUnitType.Star);

    public string? RoleIconUrl => _roleIconUrl;

    public bool HasReactions => Reactions.Count > 0;

    public string PlainText { get => _plainText; private set => SetField(ref _plainText, value); }

    public string ReplyText
    {
        get => _replyText;
        private set
        {
            if (SetField(ref _replyText, value))
            {
                OnPropertyChanged(nameof(HasReply));
                OnPropertyChanged(nameof(UltraCompactSummaryText));
                OnPropertyChanged(nameof(ShowUltraCompactSummary));
            }
        }
    }

    public bool HasReply => !string.IsNullOrWhiteSpace(ReplyText);

    public bool HasPrimaryText
    {
        get => _hasPrimaryText;
        private set
        {
            if (SetField(ref _hasPrimaryText, value))
            {
                OnPropertyChanged(nameof(ShowUltraCompactSummary));
            }
        }
    }

    public bool HasForwardedMessages => ForwardedMessages.Count > 0;

    public string UltraCompactSummaryText => HasForwardedMessages
        ? _localization["ChatRemoteForwardedLabel"]
        : HasSticker
            ? StickerFallbackText
            : ReplyText;

    public bool ShowUltraCompactSummary =>
        IsUltraCompact && !HasPrimaryText && !string.IsNullOrWhiteSpace(UltraCompactSummaryText);

    public string RemoteDetailsText
    {
        get => _remoteDetailsText;
        private set
        {
            if (SetField(ref _remoteDetailsText, value))
            {
                OnPropertyChanged(nameof(HasRemoteDetails));
            }
        }
    }

    public bool HasRemoteDetails => !string.IsNullOrWhiteSpace(RemoteDetailsText);

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
                OnPropertyChanged(nameof(UltraCompactSummaryText));
                OnPropertyChanged(nameof(ShowUltraCompactSummary));
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
                OnPropertyChanged(nameof(UltraCompactSummaryText));
                OnPropertyChanged(nameof(ShowUltraCompactSummary));
                RefreshDisplayContent();
            }
        }
    }

    public bool ShowStickerImage => HasSticker && ShowImages && StickerExtent > 0 && StickerImage is not null;

    public bool ShowStickerFallback =>
        _stickersEnabled &&
        (_fallbackKind == DiscordMessageFallbackKind.Sticker && !_hasStructuredRemoteSticker ||
         HasSticker && !ShowStickerImage);

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

    public Thickness MessageMargin { get => _messageMargin; internal set => SetField(ref _messageMargin, value); }

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
                OnPropertyChanged(nameof(ShowUltraCompactSummary));
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
        AuthorId = presentation.AuthorId;
        OnPropertyChanged(nameof(AuthorId));
        NicknameBrush = CreateNicknameBrush(presentation.AuthorStyle?.Color);
        var roleIcon = presentation.AuthorStyle?.Icon;
        RoleIconText = string.Equals(roleIcon?.Kind, "unicode", StringComparison.OrdinalIgnoreCase)
            ? roleIcon?.Value ?? string.Empty
            : string.Empty;
        _roleIconUrl = string.Equals(roleIcon?.Kind, "image", StringComparison.OrdinalIgnoreCase)
            ? roleIcon?.Url
            : null;
        RoleIconImage = null;
        _sourceTokens = presentation.Tokens.ToArray();
        _fallbackKind = presentation.FallbackKind;
        _remoteMetadata = presentation.RemoteMetadata;
        _remoteAttachments = presentation.RemoteAttachments;
        _remoteEmbeds = presentation.RemoteEmbeds;
        _primaryMedia = presentation.Media.FirstOrDefault();
        TimeText = presentation.CreatedAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;
        AdditionalMediaCount = presentation.AdditionalMediaCount;
        Thumbnail = null;
        StickerImage = null;
        HasSticker = _stickersEnabled && presentation.Stickers.Count > 0;
        _hasStructuredRemoteSticker =
            presentation.RemoteMetadata is not null && presentation.Stickers.Count > 0;
        _stickerName = presentation.Stickers.Count == 0
            ? null
            : presentation.Stickers[0].Name;
        ForwardedMessages.Clear();
        foreach (var forwarded in presentation.ForwardedMessages)
        {
            ForwardedMessages.Add(new ChatForwardMessageViewModel(forwarded, _localization));
        }

        Reactions.Clear();
        foreach (var reaction in presentation.Reactions.Where(item => item.Count > 0))
        {
            Reactions.Add(new ChatReactionViewModel(reaction));
        }

        OnPropertyChanged(nameof(HasForwardedMessages));
        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(UltraCompactSummaryText));
        OnPropertyChanged(nameof(ShowUltraCompactSummary));
        RefreshStickerFallback();
        RefreshReply();
        RefreshRemoteDetails();
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
        if (_roleIconPosition != settings.ChatRoleIconPosition)
        {
            _roleIconPosition = settings.ChatRoleIconPosition;
            NotifyRoleIconPlacementChanged();
        }
        foreach (var reaction in Reactions)
        {
            reaction.ApplySize(settings.ChatReactionSize);
        }
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
        foreach (var forwarded in ForwardedMessages)
        {
            forwarded.ApplySettings(
                ShowImages,
                ThumbnailWidth,
                ThumbnailMaxHeight,
                StickerExtent);
        }

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
        RefreshReply();
        foreach (var forwarded in ForwardedMessages)
        {
            forwarded.RefreshLocalization(_localization);
        }

        RefreshRemoteDetails();
        RefreshDisplayContent();
    }

    private void RefreshStickerFallback()
    {
        StickerFallbackText = _hasStructuredRemoteSticker &&
            !string.IsNullOrWhiteSpace(_stickerName)
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    _localization["ChatRemoteStickerNamed"],
                    _stickerName)
                : _localization["ChatStickerFallbackUnnamed"];
        OnPropertyChanged(nameof(UltraCompactSummaryText));
        OnPropertyChanged(nameof(ShowUltraCompactSummary));
    }

    private void RefreshReply()
    {
        if (_remoteMetadata?.Reply is not { } reply)
        {
            ReplyText = string.Empty;
            return;
        }

        ReplyText = !string.IsNullOrWhiteSpace(reply.ResolvedContent)
            ? string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["ChatRemoteReplyResolved"],
                reply.ResolvedAuthorName ?? _localization["ChatRemoteReplyUnknownAuthor"],
                reply.ResolvedContent)
            : _localization["ChatRemoteReply"];
    }

    private void RefreshRemoteDetails()
    {
        if (_remoteMetadata is null)
        {
            RemoteDetailsText = string.Empty;
            return;
        }

        var lines = new List<string>();
        foreach (var attachment in _remoteAttachments.Where(item => item.IsVoiceMessage))
        {
            var seconds = Math.Max(0, attachment.DurationSeconds ?? 0);
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["ChatRemoteVoice"],
                TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss")));
        }

        foreach (var attachment in _remoteAttachments.Where(item =>
                     !item.IsVoiceMessage &&
                     item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true))
        {
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["ChatRemoteAttachment"],
                attachment.FileName ?? _localization["ChatRemoteUnnamedAttachment"]));
        }

        foreach (var embed in _remoteEmbeds)
        {
            var value = new[] { embed.Title, embed.Description }
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    _localization["ChatRemoteEmbed"],
                    value));
            }
        }

        if (_remoteMetadata.Poll is { } poll)
        {
            var answers = string.Join(
                " · ",
                poll.Answers
                    .Select(answer => answer.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["ChatRemotePoll"],
                poll.Question ?? _localization["ChatRemotePollUntitled"],
                answers));
        }

        var componentLabels = FlattenComponents(_remoteMetadata.Components)
            .Select(component => component.Label ?? component.Content ?? component.Description)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(4)
            .ToArray();
        if (componentLabels.Length > 0)
        {
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["ChatRemoteComponents"],
                string.Join(" · ", componentLabels)));
        }

        RemoteDetailsText = string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    private static IEnumerable<DiscordComponentMetadata> FlattenComponents(
        IEnumerable<DiscordComponentMetadata> components)
    {
        foreach (var component in components)
        {
            yield return component;
            foreach (var child in FlattenComponents(component.Children))
            {
                yield return child;
            }
        }
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
            DiscordMessageFallbackKind.ForwardedMessage when
                !HasForwardedMessages && !_hasStructuredRemoteSticker =>
            [
                new(ChatTokenKind.Text, _localization["ChatForwardFallback"]),
            ],
            DiscordMessageFallbackKind.Message when
                !HasForwardedMessages && !_hasStructuredRemoteSticker =>
            [
                new(ChatTokenKind.Text, _localization["ChatMessageFallback"]),
            ],
            _ => _sourceTokens.ToList(),
        };
        if ((_fallbackKind is not (
                 DiscordMessageFallbackKind.ForwardedMessage or
                 DiscordMessageFallbackKind.Message) ||
             HasForwardedMessages ||
             _hasStructuredRemoteSticker) &&
            ShowStickerFallback)
        {
            displayTokens.RemoveAll(token =>
                token.Kind == ChatTokenKind.Text && string.IsNullOrWhiteSpace(token.Text));
            var prefix = displayTokens.Count == 0 ? string.Empty : " ";
            displayTokens.Add(new ChatToken(
                ChatTokenKind.Text,
                prefix + StickerFallbackText));
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
        HasPrimaryText = Tokens.Any(token =>
            token.Kind != ChatTokenKind.Text || !string.IsNullOrWhiteSpace(token.Text));
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

    private void NotifyRoleIconPlacementChanged()
    {
        OnPropertyChanged(nameof(ShowRoleIconLeft));
        OnPropertyChanged(nameof(IsRoleIconAdjacentRight));
        OnPropertyChanged(nameof(UseAnchoredRoleIconLayout));
        OnPropertyChanged(nameof(ShowRoleIconAdjacentRight));
        OnPropertyChanged(nameof(ShowRoleIconFarRight));
        OnPropertyChanged(nameof(ShowRoleIconRight));
        OnPropertyChanged(nameof(RoleIconNicknameColumnWidth));
    }

    private static System.Windows.Media.Brush CreateNicknameBrush(uint? color)
        => ColorThemeManager.CreateDiscordRoleBrush(color);
}

internal sealed class ChatReactionViewModel : INotifyPropertyChanged
{
    private ImageSource? _image;
    private double _imageExtent = ChatSettings.DefaultReactionSize;
    private double _unicodeFontSize = 16;
    private double _countFontSize = 12;

    public ChatReactionViewModel(DiscordMessageReaction reaction)
    {
        EmojiId = reaction.Emoji.EmojiId;
        Text = string.IsNullOrWhiteSpace(reaction.Emoji.EmojiId)
            ? reaction.Emoji.Name
            : $":{reaction.Emoji.Name}:";
        Count = reaction.Count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? EmojiId { get; }

    public string Text { get; }

    public int Count { get; }

    public double ImageExtent
    {
        get => _imageExtent;
        private set => SetField(ref _imageExtent, value);
    }

    public double UnicodeFontSize
    {
        get => _unicodeFontSize;
        private set => SetField(ref _unicodeFontSize, value);
    }

    public double CountFontSize
    {
        get => _countFontSize;
        private set => SetField(ref _countFontSize, value);
    }

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
            {
                return;
            }

            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowText)));
        }
    }

    public bool HasImage => Image is not null;

    public bool ShowText => Image is null;

    public void ApplySize(double size)
    {
        var metrics = ChatReactionMetrics.FromMasterSize(size);
        ImageExtent = metrics.ImageExtent;
        UnicodeFontSize = metrics.UnicodeFontSize;
        CountFontSize = metrics.CountFontSize;
    }

    private void SetField(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (Math.Abs(field - value) < 0.001)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal sealed class ChatForwardMessageViewModel : INotifyPropertyChanged
{
    private readonly ChatForwardPresentation _presentation;
    private string _label = string.Empty;
    private string _stickerFallbackText = string.Empty;
    private string _mediaFallbackText = string.Empty;
    private string _detailsText = string.Empty;
    private ImageSource? _thumbnail;
    private ImageSource? _stickerImage;
    private bool _showImages;
    private double _thumbnailWidth = 132;
    private double _thumbnailMaxHeight = 96;
    private double _stickerExtent = 96;

    public ChatForwardMessageViewModel(
        ChatForwardPresentation presentation,
        ILocalizationService localization)
    {
        _presentation = presentation;
        Text = presentation.Text;
        var tokens = presentation.Tokens.Count > 0
            ? presentation.Tokens
            : string.IsNullOrEmpty(presentation.Text)
                ? Array.Empty<ChatToken>()
                : new[] { new ChatToken(ChatTokenKind.Text, presentation.Text) };
        foreach (var token in tokens)
        {
            Tokens.Add(new ChatTokenViewModel(token));
        }

        PrimaryMedia = presentation.Media.FirstOrDefault();
        PrimarySticker = presentation.Stickers.FirstOrDefault();
        AdditionalMediaCount = presentation.AdditionalMediaCount;
        RefreshLocalization(localization);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get => _label; private set => SetField(ref _label, value); }

    public string Text { get; }

    public ObservableCollection<ChatTokenViewModel> Tokens { get; } = new();

    public bool HasText => Tokens.Count > 0 || !string.IsNullOrWhiteSpace(Text);

    public ChatMediaCandidate? PrimaryMedia { get; }

    public ChatStickerPresentation? PrimarySticker { get; }

    public int AdditionalMediaCount { get; }

    public string AdditionalMediaText => $"+{AdditionalMediaCount}";

    public bool HasAdditionalMedia => AdditionalMediaCount > 0;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetField(ref _thumbnail, value))
            {
                NotifyMediaStateChanged();
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
                NotifyMediaStateChanged();
            }
        }
    }

    public string StickerFallbackText
    {
        get => _stickerFallbackText;
        private set => SetField(ref _stickerFallbackText, value);
    }

    public string MediaFallbackText
    {
        get => _mediaFallbackText;
        private set => SetField(ref _mediaFallbackText, value);
    }

    public string DetailsText
    {
        get => _detailsText;
        private set
        {
            if (SetField(ref _detailsText, value))
            {
                OnPropertyChanged(nameof(HasDetails));
            }
        }
    }

    public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsText);

    public bool ShowThumbnail => _showImages && Thumbnail is not null;

    public bool ShowMediaFallback => PrimaryMedia is not null && !ShowThumbnail;

    public bool ShowStickerImage =>
        _showImages && StickerExtent > 0 && StickerImage is not null;

    public bool ShowStickerFallback => PrimarySticker is not null && !ShowStickerImage;

    public bool HasVisibleMedia =>
        ShowThumbnail || ShowMediaFallback || ShowStickerImage || ShowStickerFallback;

    public double ThumbnailWidth
    {
        get => _thumbnailWidth;
        private set => SetField(ref _thumbnailWidth, value);
    }

    public double ThumbnailMaxHeight
    {
        get => _thumbnailMaxHeight;
        private set => SetField(ref _thumbnailMaxHeight, value);
    }

    public double StickerExtent
    {
        get => _stickerExtent;
        private set
        {
            if (SetField(ref _stickerExtent, value))
            {
                NotifyMediaStateChanged();
            }
        }
    }

    public void ApplySettings(
        bool showImages,
        double thumbnailWidth,
        double thumbnailMaxHeight,
        double stickerExtent)
    {
        _showImages = showImages;
        ThumbnailWidth = thumbnailWidth;
        ThumbnailMaxHeight = thumbnailMaxHeight;
        StickerExtent = stickerExtent;
        NotifyMediaStateChanged();
    }

    public void RefreshLocalization(ILocalizationService localization)
    {
        Label = localization["ChatRemoteForwardedLabel"];
        StickerFallbackText = PrimarySticker is null
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                localization["ChatRemoteStickerNamed"],
                string.IsNullOrWhiteSpace(PrimarySticker.Name)
                    ? localization["ChatStickerFallbackUnnamed"]
                    : PrimarySticker.Name);
        MediaFallbackText = PrimaryMedia is null
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                localization["ChatRemoteAttachment"],
                ResolveMediaName(PrimaryMedia, localization));
        DetailsText = CreateDetailsText(_presentation, localization);
    }

    private static string CreateDetailsText(
        ChatForwardPresentation presentation,
        ILocalizationService localization)
    {
        var lines = new List<string>();
        foreach (var attachment in presentation.Attachments.Where(item => item.IsVoiceMessage))
        {
            var seconds = Math.Max(0, attachment.DurationSeconds ?? 0);
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                localization["ChatRemoteVoice"],
                TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss")));
        }

        foreach (var attachment in presentation.Attachments.Where(item =>
                     !item.IsVoiceMessage &&
                     item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true))
        {
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                localization["ChatRemoteAttachment"],
                attachment.FileName ?? localization["ChatRemoteUnnamedAttachment"]));
        }

        foreach (var embed in presentation.Embeds)
        {
            var value = new[] { embed.Title, embed.Description }
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    localization["ChatRemoteEmbed"],
                    value));
            }
        }

        var componentLabels = FlattenComponents(presentation.Components)
            .Select(component => component.Label ?? component.Content ?? component.Description)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(4)
            .ToArray();
        if (componentLabels.Length > 0)
        {
            lines.Add(string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                localization["ChatRemoteComponents"],
                string.Join(" · ", componentLabels)));
        }

        return string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    private static IEnumerable<DiscordComponentMetadata> FlattenComponents(
        IEnumerable<DiscordComponentMetadata> components)
    {
        foreach (var component in components)
        {
            yield return component;
            foreach (var child in FlattenComponents(component.Children))
            {
                yield return child;
            }
        }
    }

    private static string ResolveMediaName(
        ChatMediaCandidate media,
        ILocalizationService localization)
    {
        if (!string.IsNullOrWhiteSpace(media.DisplayName))
        {
            return media.DisplayName;
        }

        if (Uri.TryCreate(media.SourceUrl ?? media.Url, UriKind.Absolute, out var uri))
        {
            var name = Uri.UnescapeDataString(System.IO.Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return localization["ChatRemoteUnnamedAttachment"];
    }

    private void NotifyMediaStateChanged()
    {
        OnPropertyChanged(nameof(ShowThumbnail));
        OnPropertyChanged(nameof(ShowMediaFallback));
        OnPropertyChanged(nameof(ShowStickerImage));
        OnPropertyChanged(nameof(ShowStickerFallback));
        OnPropertyChanged(nameof(HasVisibleMedia));
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
