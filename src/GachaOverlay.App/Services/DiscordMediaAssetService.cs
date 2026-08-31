using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using GachaOverlay.Core.Caching;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class DiscordMediaAssetService : IDisposable
{
    private const int MaximumBytes = 12 * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private readonly BoundedAsyncCache<BitmapSource> _emojiCache;
    private readonly BoundedAsyncCache<BitmapSource> _thumbnailCache;
    private readonly BoundedAsyncCache<BitmapSource> _stickerCache;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IAppLogger _logger;
    private readonly IRuntimeMetrics? _metrics;
    private int _activeDownloads;

    public DiscordMediaAssetService(IAppLogger logger, IRuntimeMetrics? metrics = null)
    {
        _logger = logger;
        _metrics = metrics;
        _emojiCache = new BoundedAsyncCache<BitmapSource>(
            64,
            url => DownloadAsync(url, 64, false),
            observer: ObserveCache);
        _thumbnailCache = new BoundedAsyncCache<BitmapSource>(
            24,
            url => DownloadAsync(url, 384, false),
            observer: ObserveCache);
        _stickerCache = new BoundedAsyncCache<BitmapSource>(
            24,
            url => DownloadAsync(url, 384, true),
            observer: ObserveCache);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GachaOverlay/1.0");
    }

    public async Task<BitmapSource?> GetEmojiAsync(
        string emojiId,
        CancellationToken cancellationToken)
    {
        var url = $"https://cdn.discordapp.com/emojis/{Uri.EscapeDataString(emojiId)}.png?size=64&quality=lossless";
        var image = await _emojiCache.GetAsync(url, cancellationToken).ConfigureAwait(false);
        UpdateCacheMetrics();
        return image;
    }

    public async Task<BitmapSource?> GetThumbnailAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var image = await _thumbnailCache.GetAsync(url, cancellationToken).ConfigureAwait(false);
        UpdateCacheMetrics();
        return image;
    }

    public async Task<BitmapSource?> GetStickerAsync(
        ChatStickerPresentation sticker,
        string messageId,
        CancellationToken cancellationToken)
    {
        var url = ResolveStickerUrl(sticker);
        var source = HasSuppliedHttpsUrl(sticker.AssetUrl)
            ? "PayloadUrl"
            : url is null
                ? "Unavailable"
                : "ConstructedCdn";
        _logger.Information(
            "STICKER",
            $"message={Sanitize(messageId)} id={Sanitize(sticker.StickerId)} name={Sanitize(sticker.Name)} format={FormatName(sticker.FormatType)} urlSource={source} urlHost={GetHost(url)} request={url is not null}.");
        if (url is null)
        {
            _logger.Information(
                "STICKER",
                $"message={Sanitize(messageId)} decode=Unsupported fallback=true.");
            return null;
        }

        var image = await _stickerCache.GetAsync(url, cancellationToken).ConfigureAwait(false);
        UpdateCacheMetrics();
        _logger.Information(
            "STICKER",
            $"message={Sanitize(messageId)} decode={(image is null ? "Failed" : "Success")} fallback={image is null}.");
        return image;
    }

    public void ClearCache()
    {
        _emojiCache.Clear();
        _thumbnailCache.Clear();
        _stickerCache.Clear();
        UpdateCacheMetrics();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _emojiCache.Dispose();
        _thumbnailCache.Dispose();
        _stickerCache.Dispose();
        _httpClient.Dispose();
        _shutdown.Dispose();
    }

    private async Task<BitmapSource?> DownloadAsync(
        string url,
        int decodePixelWidth,
        bool stickerDiagnostic)
    {
        var active = Interlocked.Increment(ref _activeDownloads);
        _metrics?.SetGauge(RuntimeMetricNames.MediaActiveDownloads, active);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadFailed);
            Interlocked.Decrement(ref _activeDownloads);
            _metrics?.SetGauge(
                RuntimeMetricNames.MediaActiveDownloads,
                Volatile.Read(ref _activeDownloads));
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                _shutdown.Token).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (stickerDiagnostic)
            {
                _logger.Information(
                    "STICKER",
                    $"request=true urlHost={uri.Host} httpStatus={(int)response.StatusCode} contentType={Sanitize(mediaType)} declaredBytes={response.Content.Headers.ContentLength?.ToString() ?? "unknown"}.");
            }

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaximumBytes ||
                !IsSupportedImageContentType(mediaType))
            {
                return null;
            }

            await using var source = await response.Content.ReadAsStreamAsync(_shutdown.Token)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var total = 0;
            while (true)
            {
                var read = await source.ReadAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumBytes)
                {
                    if (stickerDiagnostic)
                    {
                        _logger.Warning("STICKER", $"urlHost={uri.Host} bytes={total} decode=Skipped reason=SizeLimit.");
                    }

                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), _shutdown.Token)
                    .ConfigureAwait(false);
            }

            buffer.Position = 0;
            var decodeStarted = Stopwatch.GetTimestamp();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            _metrics?.RecordDuration(
                RuntimeMetricNames.MediaDecodeDuration,
                Stopwatch.GetElapsedTime(decodeStarted));
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadSucceeded);
            if (stickerDiagnostic)
            {
                _logger.Information(
                    "STICKER",
                    $"urlHost={uri.Host} bytes={total} decode=Success pixelWidth={image.PixelWidth} pixelHeight={image.PixelHeight}.");
            }

            return image;
        }
        catch (OperationCanceledException)
        {
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadFailed);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadFailed);
            _logger.Warning(
                stickerDiagnostic ? "STICKER" : "MEDIA",
                $"Image load failed host={uri.Host} type={exception.GetType().Name} decode=Failed.");
            return null;
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _activeDownloads);
            _metrics?.SetGauge(RuntimeMetricNames.MediaActiveDownloads, remaining);
        }
    }

    private void ObserveCache(BoundedCacheEvent cacheEvent)
    {
        switch (cacheEvent)
        {
            case BoundedCacheEvent.Hit:
                _metrics?.Increment(RuntimeMetricNames.MediaCacheHit);
                break;
            case BoundedCacheEvent.Miss:
                _metrics?.Increment(RuntimeMetricNames.MediaCacheMiss);
                break;
            case BoundedCacheEvent.StaleCompletion:
                _metrics?.Increment(RuntimeMetricNames.MediaStaleCompletion);
                break;
        }
    }

    private void UpdateCacheMetrics()
    {
        _metrics?.SetGauge(
            RuntimeMetricNames.MediaCacheItems,
            _emojiCache.Count + _thumbnailCache.Count + _stickerCache.Count);
        var decodedBytes = _emojiCache.EstimateSize(EstimateDecodedBytes) +
            _thumbnailCache.EstimateSize(EstimateDecodedBytes) +
            _stickerCache.EstimateSize(EstimateDecodedBytes);
        _metrics?.SetGauge(RuntimeMetricNames.MediaDecodedBytesEstimate, decodedBytes);
    }

    private static long EstimateDecodedBytes(BitmapSource image)
    {
        var bitsPerPixel = Math.Max(1, image.Format.BitsPerPixel);
        var pixels = (long)Math.Max(0, image.PixelWidth) * Math.Max(0, image.PixelHeight);
        return pixels > long.MaxValue / bitsPerPixel
            ? long.MaxValue
            : (pixels * bitsPerPixel + 7) / 8;
    }

    internal static string? ResolveStickerUrl(ChatStickerPresentation sticker)
    {
        if (sticker.FormatType == 3)
        {
            return null;
        }

        var suppliedUrl = sticker.AssetUrl?.Trim();
        if (suppliedUrl?.StartsWith("//", StringComparison.Ordinal) == true)
        {
            suppliedUrl = $"https:{suppliedUrl}";
        }

        if (Uri.TryCreate(suppliedUrl, UriKind.Absolute, out var supplied) &&
            supplied.Scheme == Uri.UriSchemeHttps)
        {
            return supplied.AbsoluteUri;
        }

        if (string.IsNullOrWhiteSpace(sticker.StickerId))
        {
            return null;
        }

        var extension = sticker.FormatType switch
        {
            1 or 2 => "png",
            4 => "gif",
            _ => null,
        };
        if (extension is null)
        {
            return null;
        }

        return $"https://cdn.discordapp.com/stickers/{Uri.EscapeDataString(sticker.StickerId)}.{extension}";
    }

    internal static bool IsSupportedImageContentType(string? mediaType) =>
        string.IsNullOrWhiteSpace(mediaType) ||
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            mediaType,
            "application/octet-stream",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasSuppliedHttpsUrl(string? value)
    {
        var candidate = value?.Trim();
        if (candidate?.StartsWith("//", StringComparison.Ordinal) == true)
        {
            candidate = $"https:{candidate}";
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string GetHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "none";

    private static string FormatName(int? formatType) => formatType switch
    {
        1 => "Png",
        2 => "Apng",
        3 => "Lottie",
        4 => "Gif",
        _ => "Unknown",
    };

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

}
