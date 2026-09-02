using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class SalesNotificationSoundService : ISalesNotificationSoundService
{
    private static readonly TimeSpan PlaybackGuard = TimeSpan.FromMilliseconds(700);
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly string _assetDirectory;
    private readonly IAppLogger _logger;
    private readonly Func<SalesTurnNotificationKind, string> _toneAssetFactory;
    private MediaPlayer? _player;
    private SalesTurnNotificationKind? _requestedKind;
    private DateTimeOffset _requestGuardUntil;
    private long _requestVersion;
    private Task _lastPlaybackTask = Task.CompletedTask;
    private int _audioFailureLogged;
    private bool _disposed;

    public SalesNotificationSoundService(
        Dispatcher dispatcher,
        string assetDirectory,
        IAppLogger logger)
        : this(dispatcher, assetDirectory, logger, null)
    {
    }

    internal SalesNotificationSoundService(
        Dispatcher dispatcher,
        string assetDirectory,
        IAppLogger logger,
        Func<SalesTurnNotificationKind, string>? toneAssetFactory)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        _assetDirectory = assetDirectory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _toneAssetFactory = toneAssetFactory ?? EnsureToneAsset;
    }

    public void Play(SalesTurnNotificationKind kind, double volumePercent)
    {
        if (!double.IsFinite(volumePercent) || volumePercent <= 0)
        {
            return;
        }

        var volume = Math.Clamp(volumePercent, 0, 100) / 100d;
        long version;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_requestedKind == kind && now < _requestGuardUntil)
            {
                return;
            }

            if (_requestedKind == SalesTurnNotificationKind.Current &&
                kind == SalesTurnNotificationKind.Next &&
                now < _requestGuardUntil)
            {
                return;
            }

            _requestedKind = kind;
            _requestGuardUntil = now + PlaybackGuard;
            version = ++_requestVersion;
        }

        var playbackTask = PrepareAndPlayAsync(kind, volume, version);
        Volatile.Write(ref _lastPlaybackTask, playbackTask);
        _ = playbackTask;
    }

    internal Task LastPlaybackTask => Volatile.Read(ref _lastPlaybackTask);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            _requestVersion++;
        }
        void ClosePlayer()
        {
            _player?.Stop();
            _player?.Close();
            _player = null;
        }

        if (_dispatcher.CheckAccess())
        {
            ClosePlayer();
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(ClosePlayer);
        }
    }

    private async Task PrepareAndPlayAsync(
        SalesTurnNotificationKind kind,
        double volume,
        long version)
    {
        try
        {
            var path = await Task.Run(() => _toneAssetFactory(kind)).ConfigureAwait(false);
            if (_dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => PlayCore(path, volume, version)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException)
        {
            LogAudioFailure(exception);
        }
    }

    private void PlayCore(string path, double volume, long version)
    {
        lock (_sync)
        {
            if (_disposed || version != _requestVersion)
            {
                return;
            }
        }

        try
        {
            _player?.Stop();
            _player?.Close();
            var player = new MediaPlayer
            {
                Volume = volume,
            };
            player.MediaEnded += (_, _) => CompletePlayback(player);
            player.MediaFailed += (_, eventArgs) => FailPlayback(player, eventArgs.ErrorException);
            _player = player;
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException)
        {
            LogAudioFailure(exception);
        }
    }

    private string EnsureToneAsset(SalesTurnNotificationKind kind)
    {
        Directory.CreateDirectory(_assetDirectory);
        var path = Path.Combine(
            _assetDirectory,
            kind == SalesTurnNotificationKind.Current
                ? "sales-turn-current.wav"
                : "sales-turn-next.wav");
        if (File.Exists(path) && new FileInfo(path).Length > 44)
        {
            return path;
        }

        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, SalesNotificationTone.CreateWave(kind));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return path;
    }

    private void CompletePlayback(MediaPlayer player)
    {
        if (!ReferenceEquals(_player, player))
        {
            player.Close();
            return;
        }

        player.Close();
        _player = null;
    }

    private void FailPlayback(MediaPlayer player, Exception? exception)
    {
        CompletePlayback(player);
        LogAudioFailure(exception ?? new InvalidOperationException("Audio playback failed."));
    }

    private void LogAudioFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _audioFailureLogged, 1) != 0)
        {
            return;
        }

        _logger.Warning(
            "SALES-SOUND",
            $"Notification audio is unavailable ({exception.GetType().Name}); Sales remains active.");
    }
}

internal static class SalesNotificationTone
{
    private const int SampleRate = 22050;

    public static byte[] CreateWave(SalesTurnNotificationKind kind)
    {
        var notes = kind == SalesTurnNotificationKind.Current
            ? new[] { (Frequency: 783.99, Duration: 0.14), (Frequency: 1046.50, Duration: 0.20) }
            : new[] { (Frequency: 659.25, Duration: 0.12), (Frequency: 783.99, Duration: 0.16) };
        const double gapSeconds = 0.035;
        var samples = new List<short>();
        for (var noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            var note = notes[noteIndex];
            var sampleCount = (int)Math.Round(SampleRate * note.Duration);
            var fadeSamples = Math.Max(1, (int)(SampleRate * 0.018));
            for (var index = 0; index < sampleCount; index++)
            {
                var envelope = Math.Min(
                    1d,
                    Math.Min(
                        (index + 1d) / fadeSamples,
                        (sampleCount - index) / (double)fadeSamples));
                var phase = 2d * Math.PI * note.Frequency * index / SampleRate;
                samples.Add((short)Math.Round(Math.Sin(phase) * envelope * short.MaxValue * 0.24));
            }

            if (noteIndex < notes.Length - 1)
            {
                samples.AddRange(Enumerable.Repeat((short)0, (int)(SampleRate * gapSeconds)));
            }
        }

        var dataLength = samples.Count * sizeof(short);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
