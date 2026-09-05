using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.Core.Caching;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using SkiaSharp;

namespace GachaOverlay.App.Services;

internal sealed record CachedMediaAsset(BitmapSource Preview, byte[]? AnimatedBytes, int DecodePixelWidth)
{
    public bool IsAnimated => AnimatedBytes is { Length: > 0 };
}

internal sealed class DiscordMediaAssetService : IDisposable
{
    private const int MaximumBytes = 12 * 1024 * 1024;
    private const int MaximumCanvasDimension = 8_192;
    private const long MaximumCanvasPixels = 32L * 1024 * 1024;
    private const int MaximumAnimationFrames = 2_000;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly BoundedAsyncCache<CachedMediaAsset> _emojiCache;
    private readonly BoundedAsyncCache<CachedMediaAsset> _thumbnailCache;
    private readonly BoundedAsyncCache<CachedMediaAsset> _stickerCache;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IAppLogger _logger;
    private readonly IRuntimeMetrics? _metrics;
    private readonly MediaAnimationScheduler _animations;
    private int _activeDownloads;
    private bool _disposed;

    public DiscordMediaAssetService(IAppLogger logger, IRuntimeMetrics? metrics = null)
    {
        _logger = logger;
        _metrics = metrics;
        _emojiCache = Cache(64, url => DownloadAsync(url, 64, false));
        _thumbnailCache = Cache(24, url => DownloadAsync(url, 384, false));
        _stickerCache = Cache(24, url => DownloadAsync(url, 384, true));
        _animations = new MediaAnimationScheduler(
            System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            metrics,
            logger);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LSOverlay/2.1");
    }

    public Task<CachedMediaAsset?> GetEmojiMediaAsync(string emojiId, bool animated, CancellationToken cancellationToken)
    {
        var extension = animated ? "gif" : "png";
        var url = $"https://cdn.discordapp.com/emojis/{Uri.EscapeDataString(emojiId)}.{extension}?size=64&quality=lossless";
        return GetAsync(_emojiCache, url, cancellationToken);
    }

    public Task<CachedMediaAsset?> GetThumbnailMediaAsync(string url, CancellationToken cancellationToken) =>
        GetAsync(_thumbnailCache, url, cancellationToken);

    public async Task<CachedMediaAsset?> GetStickerMediaAsync(ChatStickerPresentation sticker, string messageId, CancellationToken cancellationToken)
    {
        var url = ResolveStickerUrl(sticker);
        var source = HasSuppliedHttpsUrl(sticker.AssetUrl) ? "PayloadUrl" : url is null ? "Unavailable" : "ConstructedCdn";
        _logger.Information("STICKER", $"message={Sanitize(messageId)} id={Sanitize(sticker.StickerId)} name={Sanitize(sticker.Name)} format={FormatName(sticker.FormatType)} urlSource={source} urlHost={GetHost(url)} request={url is not null}.");
        if (url is null) return null;
        var asset = await GetAsync(_stickerCache, url, cancellationToken).ConfigureAwait(false);
        _logger.Information("STICKER", $"message={Sanitize(messageId)} decode={(asset is null ? "Failed" : "Success")} animated={asset?.IsAnimated == true} fallback={asset is null}.");
        return asset;
    }

    public async Task<BitmapSource?> GetEmojiAsync(string emojiId, CancellationToken token) =>
        (await GetEmojiMediaAsync(emojiId, false, token).ConfigureAwait(false))?.Preview;
    public async Task<BitmapSource?> GetThumbnailAsync(string url, CancellationToken token) =>
        (await GetThumbnailMediaAsync(url, token).ConfigureAwait(false))?.Preview;
    public async Task<BitmapSource?> GetStickerAsync(ChatStickerPresentation sticker, string messageId, CancellationToken token) =>
        (await GetStickerMediaAsync(sticker, messageId, token).ConfigureAwait(false))?.Preview;

    public IDisposable? Play(CachedMediaAsset? asset, Action<BitmapSource> frame)
    {
        if (asset?.AnimatedBytes is not { Length: > 0 } bytes) return null;
        return _animations.Register(bytes, asset.DecodePixelWidth, frame);
    }

    public void StopAnimations() => _animations.StopAll();

    public void ClearCache()
    {
        StopAnimations();
        _emojiCache.Clear();
        _thumbnailCache.Clear();
        _stickerCache.Clear();
        UpdateCacheMetrics();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _animations.Dispose();
        _emojiCache.Dispose();
        _thumbnailCache.Dispose();
        _stickerCache.Dispose();
        _httpClient.Dispose();
        _shutdown.Dispose();
    }

    private BoundedAsyncCache<CachedMediaAsset> Cache(int capacity, Func<string, Task<CachedMediaAsset?>> loader) =>
        new(capacity, loader, observer: ObserveCache);

    private async Task<CachedMediaAsset?> GetAsync(BoundedAsyncCache<CachedMediaAsset> cache, string url, CancellationToken token)
    {
        var value = await cache.GetAsync(url, token).ConfigureAwait(false);
        UpdateCacheMetrics();
        return value;
    }

    private async Task<CachedMediaAsset?> DownloadAsync(string url, int decodeWidth, bool stickerDiagnostic)
    {
        var active = Interlocked.Increment(ref _activeDownloads);
        _metrics?.SetGauge(RuntimeMetricNames.MediaActiveDownloads, active);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            FinishDownload();
            return null;
        }

        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            requestCancellation.CancelAfter(TimeSpan.FromSeconds(10));
            var token = requestCancellation.Token;
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumBytes || !IsSupportedImageContentType(mediaType))
                return null;
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes) return null;
                await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
            }

            var bytes = buffer.ToArray();
            var started = Stopwatch.GetTimestamp();
            var animated = InspectMedia(bytes);
            var preview = DecodePreview(bytes, decodeWidth);
            _metrics?.RecordDuration(RuntimeMetricNames.MediaDecodeDuration, Stopwatch.GetElapsedTime(started));
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadSucceeded);
            return new CachedMediaAsset(preview, animated ? bytes : null, decodeWidth);
        }
        catch (OperationCanceledException)
        {
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadFailed);
            return null;
        }
        catch (Exception exception)
        {
            _metrics?.Increment(RuntimeMetricNames.MediaDownloadFailed);
            _logger.Warning(stickerDiagnostic ? "STICKER" : "MEDIA", $"Image load failed host={uri.Host} type={exception.GetType().Name}.");
            return null;
        }
        finally
        {
            FinishDownload();
        }

        void FinishDownload()
        {
            var remaining = Interlocked.Decrement(ref _activeDownloads);
            _metrics?.SetGauge(RuntimeMetricNames.MediaActiveDownloads, remaining);
        }
    }

    internal static BitmapSource DecodeImage(Stream source, int decodePixelWidth)
    {
        using var decodeStream = new BitmapDecodeStream(source);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = decodeStream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource DecodePreview(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return DecodeImage(stream, width);
        }
        catch
        {
            return DecodeSkiaFrame(bytes, width, 0).Image;
        }
    }

    internal static (BitmapSource Image, TimeSpan Duration, int FrameCount) DecodeSkiaFrame(byte[] bytes, int width, int frameIndex)
    {
        using var decoder = new FrameDecoder(bytes, width);
        return decoder.Decode(frameIndex);
    }

    // One owner per player; used only by its single in-flight worker. No frame-array cache.
    internal sealed class FrameDecoder : IDisposable
    {
        private readonly SKData _data;
        private readonly SKCodec _codec;
        private readonly SKBitmap _bitmap;
        private readonly long[] _ends;
        private readonly SKImageInfo _target;

        public FrameDecoder(byte[] bytes, int width)
        {
            _data = SKData.CreateCopy(bytes);
            try
            {
                _codec = SKCodec.Create(_data) ?? throw new InvalidDataException("Unsupported image codec.");
                ValidateMedia(_codec.Info, _codec.FrameCount);
                var scale = Math.Min(1d, Math.Max(1, width) / (double)_codec.Info.Width);
                _target = new SKImageInfo(Math.Max(1, (int)Math.Round(_codec.Info.Width * scale)),
                    Math.Max(1, (int)Math.Round(_codec.Info.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
                _bitmap = new SKBitmap(_target);
                var frames = _codec.FrameInfo;
                _ends = new long[Math.Max(1, _codec.FrameCount)];
                for (var i = 0; i < _ends.Length; i++)
                    _ends[i] = (i == 0 ? 0 : _ends[i - 1]) + TimeSpan.FromMilliseconds(
                        frames.Length > i ? Math.Clamp(frames[i].Duration, 20, 10_000) : 100).Ticks;
            }
            catch
            {
                _bitmap?.Dispose();
                _codec?.Dispose();
                _data.Dispose();
                throw;
            }
        }

        internal long BufferBytes => (long)_target.RowBytes * _target.Height;

        internal (int Frame, long Ordinal, TimeSpan Next) SelectFrame(TimeSpan elapsed)
        {
            var ticks = Math.Max(0, elapsed.Ticks);
            var cycles = ticks / _ends[^1];
            var within = ticks % _ends[^1];
            var index = Array.BinarySearch(_ends, within + 1);
            if (index < 0) index = ~index;
            return (index, cycles * _ends.Length + index, TimeSpan.FromTicks(cycles * _ends[^1] + _ends[index]));
        }

        public (BitmapSource Image, TimeSpan Duration, int FrameCount) Decode(int frameIndex)
        {
            var frame = Math.Clamp(frameIndex, 0, _ends.Length - 1);
            var pixels = DecodePixels(frame);
            // Immutable snapshots are still used for previews and independent pixel comparisons.
            var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32,
                null, pixels.Address, pixels.Bytes, pixels.Stride);
            image.Freeze();
            return (image, TimeSpan.FromTicks(_ends[frame] - (frame == 0 ? 0 : _ends[frame - 1])), _ends.Length);
        }

        // Borrowed until the next decode/dispose. Player serializes decode -> UI copy -> next decode.
        internal (IntPtr Address, int Width, int Height, int Bytes, int Stride) DecodePixels(int frameIndex)
        {
            var frame = Math.Clamp(frameIndex, 0, _ends.Length - 1);
            // PriorFrame=-1 delegates required intermediate composition/disposal to Skia,
            // including random timeline seeks and loop wrap. Never assert an invalid prior frame.
            var result = _codec.GetPixels(_target, _bitmap.GetPixels(), new SKCodecOptions(frame) { PriorFrame = -1 });
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"Skia decode failed: {result}.");
            return (_bitmap.GetPixels(), _target.Width, _target.Height, checked((int)BufferBytes), _target.RowBytes);
        }

        public void Dispose()
        {
            _bitmap.Dispose();
            _codec.Dispose();
            _data.Dispose();
        }
    }

    private static bool InspectMedia(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported image codec.");
        ValidateMedia(codec.Info, codec.FrameCount);
        return codec.FrameCount > 1;
    }

    private static void ValidateMedia(SKImageInfo info, int frameCount)
    {
        if (info.Width <= 0 || info.Height <= 0 ||
            info.Width > MaximumCanvasDimension || info.Height > MaximumCanvasDimension ||
            (long)info.Width * info.Height > MaximumCanvasPixels)
            throw new InvalidDataException("Image canvas exceeds the media safety bound.");
        if (frameCount > MaximumAnimationFrames)
            throw new InvalidDataException("Animation frame count exceeds the media safety bound.");
    }

    private void ObserveCache(BoundedCacheEvent value)
    {
        if (value == BoundedCacheEvent.Hit) _metrics?.Increment(RuntimeMetricNames.MediaCacheHit);
        else if (value == BoundedCacheEvent.Miss) _metrics?.Increment(RuntimeMetricNames.MediaCacheMiss);
        else if (value == BoundedCacheEvent.StaleCompletion) _metrics?.Increment(RuntimeMetricNames.MediaStaleCompletion);
    }

    private void UpdateCacheMetrics()
    {
        _metrics?.SetGauge(RuntimeMetricNames.MediaCacheItems, _emojiCache.Count + _thumbnailCache.Count + _stickerCache.Count);
        var bytes = _emojiCache.EstimateSize(EstimateBytes) + _thumbnailCache.EstimateSize(EstimateBytes) + _stickerCache.EstimateSize(EstimateBytes);
        _metrics?.SetGauge(RuntimeMetricNames.MediaDecodedBytesEstimate, bytes);
    }

    private static long EstimateBytes(CachedMediaAsset asset) =>
        (long)asset.Preview.PixelWidth * asset.Preview.PixelHeight * 4 + (asset.AnimatedBytes?.LongLength ?? 0);

    internal static string? ResolveStickerUrl(ChatStickerPresentation sticker)
    {
        if (sticker.FormatType == 3) return null;
        var supplied = sticker.AssetUrl?.Trim();
        if (supplied?.StartsWith("//", StringComparison.Ordinal) == true) supplied = $"https:{supplied}";
        if (Uri.TryCreate(supplied, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(sticker.StickerId)) return null;
        var extension = sticker.FormatType switch { 1 or 2 => "png", 4 => "gif", _ => null };
        return extension is null ? null : $"https://media.discordapp.net/stickers/{Uri.EscapeDataString(sticker.StickerId)}.{extension}?size=256&quality=lossless";
    }

    internal static bool IsSupportedImageContentType(string? mediaType) =>
        string.IsNullOrWhiteSpace(mediaType) || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static bool HasSuppliedHttpsUrl(string? value)
    {
        var candidate = value?.Trim();
        if (candidate?.StartsWith("//", StringComparison.Ordinal) == true) candidate = $"https:{candidate}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
    private static string GetHost(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : "none";
    private static string FormatName(int? value) => value switch { 1 => "Png", 2 => "Apng", 3 => "Lottie", 4 => "Gif", _ => "Unknown" };
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }
}

internal sealed class MediaAnimationScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly HashSet<Player> _players = [];
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private readonly IRuntimeMetrics? _metrics;
    private readonly IAppLogger _logger;
    private int _activeDecoders;
    private int _activeSurfaces;
    private long _bufferBytes;
    private bool _disposed;

    public MediaAnimationScheduler(Dispatcher dispatcher, IRuntimeMetrics? metrics, IAppLogger logger)
    {
        _metrics = metrics;
        _logger = logger;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _timer.Stop();
    }

    public IDisposable Register(byte[] bytes, int width, Action<BitmapSource> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var player = new Player(this, bytes, width, callback);
        _players.Add(player);
        UpdateGauge();
        if (!_timer.IsEnabled) _timer.Start();
        return player;
    }

    public void StopAll()
    {
        foreach (var player in _players.ToArray()) player.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        StopAll();
    }

    private void Tick()
    {
        _metrics?.Increment("media.animation.scheduler_tick.count");
        foreach (var player in _players.ToArray()) player.TryAdvance();
        if (_players.Count == 0) _timer.Stop();
    }

    private void Remove(Player player)
    {
        _players.Remove(player);
        if (_players.Count == 0) _timer.Stop();
        UpdateGauge();
    }

    private void UpdateGauge()
    {
        _metrics?.SetGauge(RuntimeMetricNames.MediaAnimationActivePlayers, _players.Count);
        _metrics?.SetGauge(RuntimeMetricNames.MediaAnimationSchedulerActive, _players.Count > 0 ? 1 : 0);
    }

    private sealed class Player : IDisposable
    {
        private readonly MediaAnimationScheduler _owner;
        private readonly byte[] _bytes;
        private readonly int _width;
        private readonly Action<BitmapSource> _callback;
        private readonly CancellationTokenSource _cancel = new();
        private readonly object _lifetime = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private TimeSpan _next;
        private long _lastOrdinal = -1;
        private DiscordMediaAssetService.FrameDecoder? _decoder;
        private WriteableBitmap? _surface;
        private int _busy;
        private volatile bool _disposed;
        private bool _released;

        public Player(MediaAnimationScheduler owner, byte[] bytes, int width, Action<BitmapSource> callback)
        {
            _owner = owner;
            _bytes = bytes;
            _width = width;
            _callback = callback;
        }

        public void TryAdvance()
        {
            lock (_lifetime)
            {
                if (_disposed || _busy != 0 || Stopwatch.GetElapsedTime(_started) < _next) return;
                _busy = 1;
            }
            // WaitAsync alone can complete synchronously. Explicitly leave the UI thread.
            _ = Task.Run(DecodeAsync);
        }

        private async Task DecodeAsync()
        {
            try
            {
                await _owner._decodeSlots.WaitAsync(_cancel.Token).ConfigureAwait(false);
                try
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    var started = Stopwatch.GetTimestamp();
                    if (_decoder is null)
                    {
                        _decoder = new DiscordMediaAssetService.FrameDecoder(_bytes, _width);
                        var active = Interlocked.Increment(ref _owner._activeDecoders);
                        _owner._metrics?.SetGauge(RuntimeMetricNames.MediaAnimationDecoderCount, active);
                        _owner._metrics?.SetGauge(RuntimeMetricNames.MediaAnimationFrameBuffers, active);
                        _owner._metrics?.SetGauge("media.animation.working_bytes", Interlocked.Add(ref _owner._bufferBytes, _decoder.BufferBytes));
                        _owner._metrics?.Increment("media.animation.decoder_created.count");
                    }
                    var selection = _decoder.SelectFrame(Stopwatch.GetElapsedTime(_started));
                    var skipped = selection.Ordinal - _lastOrdinal - 1;
                    if (skipped > 0) _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationFramesSkipped, skipped);
                    _lastOrdinal = selection.Ordinal;
                    var decoded = _decoder.DecodePixels(selection.Frame);
                    _owner._metrics?.RecordDuration(RuntimeMetricNames.MediaAnimationDecodeDuration, Stopwatch.GetElapsedTime(started));
                    _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationFrameDecoded);
                    if (_disposed) return;
                    _next = selection.Next;
                    var presented = false;
                    await _owner._timer.Dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed) return;
                        if (Stopwatch.GetElapsedTime(_started) >= selection.Next)
                        {
                            _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationFramesSkipped);
                            return;
                        }
                        // One UI-owned presentation surface per player, not a fresh BitmapSource per frame.
                        // Decode stays on the worker. The borrowed pixels cannot be overwritten or freed
                        // until this awaited UI operation completes, including cancellation/disposal races.
                        if (_surface is null)
                        {
                            _surface = new WriteableBitmap(decoded.Width, decoded.Height, 96, 96, PixelFormats.Pbgra32, null);
                            _owner._metrics?.SetGauge("media.animation.presentation_surfaces", Interlocked.Increment(ref _owner._activeSurfaces));
                        }
                        _surface.WritePixels(new System.Windows.Int32Rect(0, 0, decoded.Width, decoded.Height),
                            decoded.Address, decoded.Bytes, decoded.Stride);
                        _callback(_surface);
                        presented = true;
                    }, DispatcherPriority.Background, _cancel.Token);
                    if (presented)
                        _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationFramesPresented);
                }
                finally
                {
                    _owner._decodeSlots.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationFrameFailed);
                _owner._logger.Warning("MEDIA", $"Animated frame decode failed type={exception.GetType().Name}.");
                await _owner._timer.Dispatcher.InvokeAsync(Dispose, DispatcherPriority.Background);
            }
            finally
            {
                lock (_lifetime)
                {
                    _busy = 0;
                    if (_disposed) ReleaseResources();
                }
            }
        }

        public void Dispose()
        {
            lock (_lifetime)
            {
                if (_disposed) return;
                _disposed = true;
                _cancel.Cancel();
                if (_busy == 0) ReleaseResources();
            }
            _owner.Remove(this);
            _owner._metrics?.Increment(RuntimeMetricNames.MediaAnimationDisposals);
        }

        private void ReleaseResources()
        {
            if (_released) return;
            _released = true;
            if (_surface is not null)
            {
                _surface = null;
                _owner._metrics?.SetGauge("media.animation.presentation_surfaces", Interlocked.Decrement(ref _owner._activeSurfaces));
            }
            if (_decoder is not null)
            {
                var bytes = _decoder.BufferBytes;
                _decoder.Dispose();
                _decoder = null;
                var remaining = Interlocked.Decrement(ref _owner._activeDecoders);
                _owner._metrics?.SetGauge(RuntimeMetricNames.MediaAnimationDecoderCount, remaining);
                _owner._metrics?.SetGauge(RuntimeMetricNames.MediaAnimationFrameBuffers, remaining);
                _owner._metrics?.SetGauge("media.animation.working_bytes", Interlocked.Add(ref _owner._bufferBytes, -bytes));
            }
            _cancel.Dispose();
        }
    }
}
