using System.Security.Cryptography;
using System.Text;
using LSOverlay.CoreMedia;

namespace LSOverlay.Backend.CoreClient;

internal sealed class CoreMediaScope(string viewer, Func<string, string?> readable,
    Func<string, CancellationToken, Task<bool>> authorized, CancellationToken session) : ICoreAnimatedMediaReferences
{
    private readonly object _gate = new();
    // IDs are still authorized per fetch. Their expiry exceeds the six-hour
    // connection lifetime so an idle room cannot leave visible media IDs expired.
    private MediaCatalog _catalog = CreateCatalog();
    private static MediaCatalog CreateCatalog() => new(referenceLifetime: TimeSpan.FromHours(8));
    private readonly HashSet<(string Channel, string Message)> _scopes = new();
    private int _activeRequests;
    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _activeRequests) <= 4) return true;
        Interlocked.Decrement(ref _activeRequests); return false;
    }
    public void Leave() => Interlocked.Decrement(ref _activeRequests);
    public CancellationToken Session => session;
    public void RevokeChannel(string channel)
    {
        lock (_gate) foreach (var scope in _scopes.Where(scope => scope.Channel == channel).ToArray())
            { _catalog.RevokeMessage(scope.Channel, scope.Message); _scopes.Remove(scope); }
    }
    public void Revoke(string? message = null)
    {
        lock (_gate)
        {
            if (message is null) { _catalog = CreateCatalog(); _scopes.Clear(); return; }
            foreach (var scope in _scopes.Where(scope => scope.Message == message).ToArray())
            { _catalog.RevokeMessage(scope.Channel, scope.Message); _scopes.Remove(scope); }
        }
    }
    public string RegisterEmoji(string messageId, string identity, bool animated) => RegisterCanonical(messageId, "emoji", identity,
        ulong.TryParse(identity, out _) ? "https://cdn.discordapp.com/emojis/" + identity + (animated ? ".gif" : ".png") : null);
    public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl)
    {
        var channel = readable(messageId);
        if (channel is not null && assetUrl is not null && !session.IsCancellationRequested)
        {
            try { lock (_gate) { var id = _catalog.RegisterCanonical(viewer, channel, messageId, assetUrl); _scopes.Add((channel, messageId)); return id; } }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { }
        }
        return "unavailable-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "|" + kind + "|" + identity))).ToLowerInvariant();
    }
    public async Task<string> Resolve(string id, CancellationToken cancellation)
    {
        MediaCatalog catalog; lock (_gate) catalog = _catalog;
        var source = await catalog.ResolveAuthorizedAsync(id, viewer, async (channel, message, token) =>
            !session.IsCancellationRequested && readable(message) == channel && await authorized(channel, token) && readable(message) == channel, cancellation);
        lock (_gate) if (!ReferenceEquals(catalog, _catalog) || session.IsCancellationRequested) throw new UnauthorizedAccessException();
        return source;
    }
}
