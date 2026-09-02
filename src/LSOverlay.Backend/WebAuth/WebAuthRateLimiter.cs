using LSOverlay.Backend.Security;

namespace LSOverlay.Backend.WebAuth;

// Fixed bounds even for spoofed/rotating sources. No raw IP retained or logged.
internal sealed class WebAuthRateLimiter(Func<DateTimeOffset>? clock = null)
{
    public const int MaximumSources = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, int[]> _sources = new();
    private readonly int[] _total = new int[3];
    private DateTimeOffset _window;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool Allow(string source, int operation)
    {
        lock (_sync)
        {
            if (_clock() >= _window.AddMinutes(1))
            {
                _window = _clock(); _sources.Clear(); Array.Clear(_total);
            }
            var hash = CryptographicSecrets.HashHex(source);
            if (!_sources.TryGetValue(hash, out var counts))
            {
                if (_sources.Count >= MaximumSources) return false;
                _sources.Add(hash, counts = new int[3]);
            }
            var perSource = operation switch { 0 => 10, 1 => 3000, _ => 60 };
            var total = operation == 1 ? 12000 : 600;
            if (counts[operation] >= perSource || _total[operation] >= total) return false;
            counts[operation]++; _total[operation]++;
            return true;
        }
    }
}
