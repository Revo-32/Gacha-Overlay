using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LSOverlay.CoreMedia;

public sealed class MediaSourceResponseException(int status,string reason) : IOException("Canonical media response rejected.")
{
    public int Status { get; }=status;
    public string Reason { get; }=reason;
}

public static class MediaSourcePolicy
{
    // Exact Discord/provider image origins only. No user-supplied URL HTTP
    // endpoint, wildcard suffix, redirect, cookies, proxy or authorization header.
    private static bool IsImageHost(string host) => host is "cdn.discordapp.com" or "media.discordapp.net" or
        "images-ext-1.discordapp.net" or "images-ext-2.discordapp.net" or
        "static.klipy.com" or "static1.klipy.com" or "static2.klipy.com" or
        "media.giphy.com" or "media0.giphy.com" or "media1.giphy.com" or "media2.giphy.com" or "media3.giphy.com" or "media4.giphy.com" or "media.tenor.com";
    public static Uri Validate(string source)
    {
        if (source.Length > 4096 || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !IsImageHost(uri.Host) ||
            source.Contains('\\') || source.Any(char.IsControl)) throw new InvalidDataException("Untrusted canonical media source.");
        var pathAllowed=uri.Host switch {
            "cdn.discordapp.com" or "media.discordapp.net" => Regex.IsMatch(uri.AbsolutePath,"\\A/(attachments/[0-9]+/[0-9]+/[^/]+|emojis/[0-9]+\\.(png|gif|webp)|stickers/[0-9]+\\.(png|gif|webp)|role-icons/[0-9]+/[a-zA-Z0-9]+\\.(png|gif|webp))\\z",RegexOptions.CultureInvariant),
            "images-ext-1.discordapp.net" or "images-ext-2.discordapp.net" => Regex.IsMatch(uri.AbsolutePath,"\\A/external/[A-Za-z0-9_-]+/https/(static[12]?\\.klipy\\.com|media[0-4]?\\.giphy\\.com|media\\.tenor\\.com)/[A-Za-z0-9_/=.%-]+\\.(gif|png|webp|jpg|jpeg)\\z",RegexOptions.CultureInvariant),
            _ => Regex.IsMatch(uri.AbsolutePath,"\\A/[A-Za-z0-9_/=.-]+\\.(gif|png|webp|jpg|jpeg)\\z",RegexOptions.CultureInvariant)
        };
        if (!pathAllowed ||
            uri.AbsolutePath.Split('/').Select(Uri.UnescapeDataString).Any(segment => segment.Contains('/') || segment.Contains('\\') || segment.Contains('%') || segment.Any(char.IsControl)))
            throw new InvalidDataException("Unsupported canonical CDN path.");
        return uri;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) && !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 192 && bytes[1] == 0) && !(bytes[0] == 192 && bytes[1] == 2) &&
                !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        // Conservative global-unicast allow range; transition, documentation,
        // link-local, ULA, multicast and scoped addresses are not network targets.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, token) =>
            {
                if (!IsImageHost(context.DnsEndPoint.Host) || context.DnsEndPoint.Port != 443)
                    throw new HttpRequestException("CDN peer rejected.");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length is < 1 or > 32 || addresses.Any(address => !IsPublic(address))) throw new HttpRequestException("Non-public CDN resolution rejected.");
                // Connect to the checked IP, not the hostname: no second DNS
                // lookup/rebinding window. HttpClient still verifies TLS/SNI.
                var selected = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
                var socket = new Socket(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(selected, 443), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static async Task<string> FetchAndConvertAsync(HttpClient client, DerivativeCache cache, string canonicalUrl, MediaProfile profile, CancellationToken token,Action<string,double>? timing=null)
    {
        var source = Validate(canonicalUrl); profile.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, source) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var timer=System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        timing?.Invoke("cdnHeaders",timer.Elapsed.TotalMilliseconds);
        var reason=response.StatusCode!=HttpStatusCode.OK ? "cdn-status" : response.Content.Headers.ContentLength>MediaConverter.MaximumSourceBytes ? "source-bytes" :
            response.Content.Headers.ContentEncoding.Count!=0 ? "content-encoding" : response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/gif" or "image/jpeg" or "image/webp") ? "content-type" : null;
        if (reason is not null) throw new MediaSourceResponseException((int)response.StatusCode,reason);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await cache.GetOrCreateAsync(body, profile, timeout.Token,timing);
    }
}

// Backend owns this short-lived catalog. Raw URLs and signed query strings never
// appear in the Core DTO. Authorization is required on every resolve, even hits.
public sealed class MediaCatalog(TimeProvider? clock = null, TimeSpan? referenceLifetime = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private sealed record Entry(string Viewer, string Channel, string Message, Uri Source, DateTimeOffset Expires);
    public string RegisterCanonical(string viewer, string channel, string message, string source)
    {
        if (new[] { viewer, channel, message }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)) throw new ArgumentException("Media authorization scope is required.");
        var uri = MediaSourcePolicy.Validate(source);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _entries.Remove(key);
            foreach (var pair in _entries)
                if (pair.Value.Viewer == viewer && pair.Value.Channel == channel && pair.Value.Message == message && pair.Value.Source == uri) return pair.Key;
            if (_entries.Count >= 512) throw new IOException("Media reference budget exhausted.");
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _entries.Add(id, new(viewer, channel, message, uri, now + (referenceLifetime ?? TimeSpan.FromMinutes(10)))); return id;
        }
    }
    public async Task<string> ResolveAuthorizedAsync(string id, string viewer, Func<string, string, CancellationToken, Task<bool>> canReadMessage, CancellationToken token)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out entry!) || entry.Viewer != viewer || entry.Expires <= _clock.GetUtcNow()) throw new UnauthorizedAccessException("Media reference is unavailable.");
        }
        if (!await canReadMessage(entry.Channel, entry.Message, token)) throw new UnauthorizedAccessException("Media message access denied.");
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var current) || current != entry || entry.Expires <= _clock.GetUtcNow()) throw new UnauthorizedAccessException("Media reference expired/revoked.");
        }
        return entry.Source.AbsoluteUri; // server/isolated worker only, never an external response
    }
    public void RevokeMessage(string channel, string message)
    {
        lock (_sync)
            foreach (var id in _entries.Where(pair => pair.Value.Channel == channel && pair.Value.Message == message).Select(pair => pair.Key).ToArray()) _entries.Remove(id);
    }
}
