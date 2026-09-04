using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal sealed class ChatPresentationCoordinator : IDisposable
{
    private readonly ChatViewModel _viewModel;
    private readonly ChatPresentationSynchronizer _synchronizer = new();
    private readonly DiscordMediaAssetService _media;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly ChatTypographyResolver _typographyResolver;
    private readonly Dictionary<string, ChatMessageViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatMessagePresentation> _presentations = new(StringComparer.Ordinal);
    private AppSettings _settings;
    private ResolvedChatTypography _typography;
    private ChatResponsiveLevel _responsiveLevel = ChatResponsiveLevel.Full;
    private int _responsiveMeasurementRevision;
    private bool _disposed;
    private long _scrollGeneration = -1;
    private readonly IRuntimeMetrics? _metrics;

    public ChatPresentationCoordinator(
        ChatViewModel viewModel,
        DiscordMediaAssetService media,
        ILocalizationService localization,
        IAppLogger logger,
        AppSettings settings,
        ChatTypographyResolver typographyResolver,
        IRuntimeMetrics? metrics = null)
    {
        _viewModel = viewModel;
        _media = media;
        _localization = localization;
        _logger = logger;
        _settings = settings;
        _viewModel.PaintViewportPadding = ChatPaintSafety.CalculateViewportPadding(settings);
        _typographyResolver = typographyResolver;
        _typography = typographyResolver.Resolve(settings.ChatFontPreset);
        _metrics = metrics;
    }

    public void ApplyState(DiscordMessageState state, string? authenticatedUserId)
    {
        var started = Stopwatch.GetTimestamp();
        if (state.IsBootstrapping)
        {
            return;
        }

        if (_scrollGeneration != state.Generation)
        {
            _scrollGeneration = state.Generation;
            _viewModel.JumpToLatest();
        }
        _viewModel.BeginMessageUpdate();
        try
        {
            foreach (var change in _synchronizer.Synchronize(state, authenticatedUserId))
            {
                switch (change.Kind)
                {
                    case ChatPresentationChangeKind.Remove:
                        Remove(change.MessageId);
                        break;
                    case ChatPresentationChangeKind.SnapshotAdd:
                    case ChatPresentationChangeKind.Add:
                        Add(change.Index, change.Message!);
                        break;
                    case ChatPresentationChangeKind.Update:
                        Update(change.Message!);
                        break;
                }

                if (change.Kind == ChatPresentationChangeKind.Add) _viewModel.NotifyNewMessage();

                if (change.RequestMentionPulse)
                {
                    _viewModel.RequestMentionPulse();
                }

                if (ChatAutoScrollPolicy.ShouldScrollToLatest(change.Kind))
                {
                    _viewModel.RequestScrollToLatest();
                }
            }

            RegroupConsecutiveAuthors();
        }
        finally
        {
            _viewModel.EndMessageUpdate();
            _metrics?.RecordDuration(
                RuntimeMetricNames.ChatPresentationDuration,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        var metricsChanged =
            _settings.ChatFontPreset != settings.ChatFontPreset ||
            Math.Abs(_settings.ChatFontSizePoints - settings.ChatFontSizePoints) > 0.001 ||
            Math.Abs(_settings.ChatLineHeightMultiplier - settings.ChatLineHeightMultiplier) > 0.001;
        _settings = settings;
        _viewModel.PaintViewportPadding = ChatPaintSafety.CalculateViewportPadding(settings);
        _typography = _typographyResolver.Resolve(settings.ChatFontPreset);
        if (metricsChanged)
        {
            _responsiveMeasurementRevision++;
        }

        foreach (var item in _items.Values)
        {
            item.RestartEnrichment();
            item.ApplySettings(settings, _responsiveLevel, _typography);
            if (_presentations.TryGetValue(item.MessageId, out var presentation))
            {
                StartEnrichment(item, presentation);
            }
        }
    }

    internal ResolvedChatTypography CurrentTypography => _typography;

    internal int ResponsiveMeasurementRevision => _responsiveMeasurementRevision;

    internal ChatResponsiveLevel CurrentResponsiveLevel => _responsiveLevel;

    public void ClearMediaCache()
    {
        _viewModel.PreviewImage = null;
        _media.ClearCache();
        foreach (var item in _items.Values)
        {
            item.RestartEnrichment();
            item.Thumbnail = null;
            item.StickerImage = null;
            foreach (var forwarded in item.ForwardedMessages)
            {
                forwarded.Thumbnail = null;
                forwarded.StickerImage = null;
                foreach (var token in forwarded.Tokens)
                {
                    token.Image = null;
                }
            }

            foreach (var token in item.Tokens)
            {
                token.Image = null;
            }

            item.RoleIconImage = null;
            foreach (var reaction in item.Reactions)
            {
                reaction.Image = null;
            }

            if (_presentations.TryGetValue(item.MessageId, out var presentation))
            {
                StartEnrichment(item, presentation);
            }
        }
    }

    public void ApplyResponsiveLevel(ChatResponsiveLevel level)
    {
        if (_responsiveLevel == level)
        {
            return;
        }

        _responsiveLevel = level;
        foreach (var item in _items.Values)
        {
            item.ApplySettings(_settings, level, _typography);
            if (_presentations.TryGetValue(item.MessageId, out var presentation))
            {
                StartEnrichment(item, presentation);
            }
        }
    }

    public void EvaluateResponsive(System.Windows.Size availableSize, double pixelsPerDip)
    {
        var fontSize = _settings.ChatFontSizePoints * 96d / 72d;
        var nicknameTypeface = new Typeface(
            _typography.Nickname.FontFamily,
            FontStyles.Normal,
            _typography.Nickname.FontWeight,
            FontStretches.Normal);
        var messageTypeface = new Typeface(
            _typography.Message.FontFamily,
            FontStyles.Normal,
            _typography.Message.FontWeight,
            FontStretches.Normal);
        var maximumNicknameWidth = _viewModel.Messages.Count == 0
            ? Measure("Nickname", nicknameTypeface, fontSize, pixelsPerDip).Width
            : _viewModel.Messages.Max(item =>
                Measure(item.AuthorName, nicknameTypeface, fontSize, pixelsPerDip).Width);
        var time = Measure("23:59", messageTypeface, fontSize * 0.85, pixelsPerDip);
        var line = Measure("Ag한글日本語", messageTypeface, fontSize, pixelsPerDip);
        var input = new ChatResponsiveInput(
            availableSize.Width,
            availableSize.Height,
            Math.Max(
                line.Height,
                ChatVisualMetrics.CalculateLineHeight(
                    fontSize,
                    _settings.ChatLineHeightMultiplier)),
            maximumNicknameWidth,
            time.Width,
            132,
            _viewModel.Messages.Count,
            _settings.ChatShowImages && _presentations.Values.Any(item =>
                item.Media.Count > 0 ||
                item.Stickers.Count > 0 ||
                item.ForwardedMessages.Any(forwarded =>
                    forwarded.Media.Count > 0 || forwarded.Stickers.Count > 0)),
            _settings.ChatShowTime);
        ApplyResponsiveLevel(ChatResponsiveLayout.Evaluate(input, _responsiveLevel));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var item in _items.Values)
        {
            item.Dispose();
        }

        _items.Clear();
        _presentations.Clear();
        _viewModel.Messages.Clear();
        _viewModel.PreviewImage = null;
        _media.Dispose();
    }

    private void Add(int index, ChatMessagePresentation presentation)
    {
        if (_items.ContainsKey(presentation.MessageId))
        {
            Update(presentation);
            return;
        }

        var item = new ChatMessageViewModel(
            presentation,
            _localization,
            OnPreviewRequested);
        item.ApplySettings(_settings, _responsiveLevel, _typography);
        _items.Add(presentation.MessageId, item);
        _presentations[presentation.MessageId] = presentation;
        _viewModel.Messages.Insert(Math.Clamp(index, 0, _viewModel.Messages.Count), item);
        StartEnrichment(item, presentation);
    }

    private void Update(ChatMessagePresentation presentation)
    {
        if (!_items.TryGetValue(presentation.MessageId, out var item))
        {
            Add(_viewModel.Messages.Count, presentation);
            return;
        }

        if (ReferenceEquals(_viewModel.PreviewImage, item.Thumbnail))
        {
            _viewModel.PreviewImage = null;
        }

        item.Update(presentation);
        item.ApplySettings(_settings, _responsiveLevel, _typography);
        _presentations[presentation.MessageId] = presentation;
        StartEnrichment(item, presentation);
    }

    private void Remove(string messageId)
    {
        if (!_items.Remove(messageId, out var item))
        {
            return;
        }

        _presentations.Remove(messageId);
        _viewModel.Messages.Remove(item);
        item.Dispose();
        if (ReferenceEquals(_viewModel.PreviewImage, item.Thumbnail))
        {
            _viewModel.PreviewImage = null;
        }
    }

    private void StartEnrichment(
        ChatMessageViewModel item,
        ChatMessagePresentation presentation)
    {
        var revision = presentation.Revision;
        var identity = new ChatEnrichmentIdentity(
            presentation.MessageId,
            presentation.Generation,
            revision);
        foreach (var token in item.Tokens.Where(token =>
                     _settings.ChatCustomEmojiEnabled &&
                     token.Kind == ChatTokenKind.CustomEmoji &&
                     !string.IsNullOrWhiteSpace(token.Identity) &&
                     token.Image is null))
        {
            _ = EnrichEmojiAsync(item, token, identity);
        }

        if (item.RoleIconImage is null &&
            Uri.TryCreate(item.RoleIconUrl, UriKind.Absolute, out var roleIconUri) &&
            roleIconUri.Scheme == Uri.UriSchemeHttps)
        {
            _ = EnrichRoleIconAsync(item, roleIconUri.AbsoluteUri, identity);
        }

        foreach (var reaction in item.Reactions.Where(reaction =>
                     !string.IsNullOrWhiteSpace(reaction.EmojiId) && reaction.Image is null))
        {
            _ = EnrichReactionEmojiAsync(item, reaction, identity);
        }

        if (item.ShowImages && item.Thumbnail is null && presentation.Media.Count > 0)
        {
            _ = EnrichThumbnailAsync(item, presentation.Media[0].Url, identity);
        }

        if (item.ShowImages && _settings.ChatStickerEnabled &&
            item.StickerImage is null && presentation.Stickers.Count > 0)
        {
            _ = EnrichStickerAsync(item, presentation.Stickers[0], identity);
        }

        foreach (var forwarded in item.ForwardedMessages)
        {
            foreach (var token in forwarded.Tokens.Where(token =>
                         _settings.ChatCustomEmojiEnabled &&
                         token.Kind == ChatTokenKind.CustomEmoji &&
                         !string.IsNullOrWhiteSpace(token.Identity) &&
                         token.Image is null))
            {
                _ = EnrichForwardEmojiAsync(item, forwarded, token, identity);
            }

            if (item.ShowImages && forwarded.Thumbnail is null &&
                forwarded.PrimaryMedia is not null)
            {
                _ = EnrichForwardThumbnailAsync(
                    item,
                    forwarded,
                    forwarded.PrimaryMedia.Url,
                    identity);
            }

            if (item.ShowImages && _settings.ChatStickerEnabled &&
                forwarded.StickerImage is null && forwarded.PrimarySticker is not null)
            {
                _ = EnrichForwardStickerAsync(
                    item,
                    forwarded,
                    forwarded.PrimarySticker,
                    identity);
            }
        }
    }

    private async Task EnrichEmojiAsync(
        ChatMessageViewModel item,
        ChatTokenViewModel token,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetEmojiAsync(token.Identity!, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) && _items.ContainsKey(item.MessageId))
                {
                    token.Image = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichThumbnailAsync(
        ChatMessageViewModel item,
        string url,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetThumbnailAsync(url, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) && _items.ContainsKey(item.MessageId))
                {
                    item.Thumbnail = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichRoleIconAsync(
        ChatMessageViewModel item,
        string url,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetThumbnailAsync(url, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) && _items.ContainsKey(item.MessageId))
                {
                    item.RoleIconImage = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichReactionEmojiAsync(
        ChatMessageViewModel item,
        ChatReactionViewModel reaction,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetEmojiAsync(reaction.EmojiId!, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) &&
                    _items.ContainsKey(item.MessageId) &&
                    item.Reactions.Contains(reaction))
                {
                    reaction.Image = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichStickerAsync(
        ChatMessageViewModel item,
        ChatStickerPresentation sticker,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetStickerAsync(
                sticker,
                item.MessageId,
                item.EnrichmentToken);
            if (image is null)
            {
                _logger.Information(
                    "STICKER",
                    $"message={item.MessageId} presentation=FallbackVisible.");
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) && _items.ContainsKey(item.MessageId))
                {
                    item.StickerImage = image;
                    _logger.Information(
                        "STICKER",
                        $"message={item.MessageId} presentation=Visible.");
                }
                else
                {
                    _logger.Information(
                        "STICKER",
                        $"message={item.MessageId} presentation=StaleIgnored.");
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichForwardThumbnailAsync(
        ChatMessageViewModel item,
        ChatForwardMessageViewModel forwarded,
        string url,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetThumbnailAsync(url, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) &&
                    _items.ContainsKey(item.MessageId) &&
                    item.ForwardedMessages.Contains(forwarded))
                {
                    forwarded.Thumbnail = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichForwardEmojiAsync(
        ChatMessageViewModel item,
        ChatForwardMessageViewModel forwarded,
        ChatTokenViewModel token,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetEmojiAsync(token.Identity!, item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) &&
                    _items.ContainsKey(item.MessageId) &&
                    item.ForwardedMessages.Contains(forwarded) &&
                    forwarded.Tokens.Contains(token))
                {
                    token.Image = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnrichForwardStickerAsync(
        ChatMessageViewModel item,
        ChatForwardMessageViewModel forwarded,
        ChatStickerPresentation sticker,
        ChatEnrichmentIdentity identity)
    {
        try
        {
            var image = await _media.GetStickerAsync(
                sticker,
                item.MessageId + ":forward",
                item.EnrichmentToken);
            if (image is null)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (item.IsCurrent(identity) &&
                    _items.ContainsKey(item.MessageId) &&
                    item.ForwardedMessages.Contains(forwarded))
                {
                    forwarded.StickerImage = image;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPreviewRequested(ChatMessageViewModel item)
    {
        if (!item.CanEnlarge || item.Thumbnail is null)
        {
            return;
        }

        _viewModel.PreviewImage = item.Thumbnail;
    }

    private void RegroupConsecutiveAuthors()
    {
        var headers = ChatAuthorGrouping.ResolveHeaders(
            _viewModel.Messages.Select(item => item.AuthorId));
        for (var index = 0; index < _viewModel.Messages.Count; index++)
        {
            _viewModel.Messages[index].ShowAuthorHeader = headers[index];
        }
    }

    private static FormattedText Measure(
        string text,
        Typeface typeface,
        double fontSize,
        double pixelsPerDip) => new(
        text,
        CultureInfo.CurrentUICulture,
        System.Windows.FlowDirection.LeftToRight,
        typeface,
        fontSize,
        System.Windows.Media.Brushes.Transparent,
        Math.Max(1, pixelsPerDip));
}
