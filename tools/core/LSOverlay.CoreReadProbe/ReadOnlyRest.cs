using System.Net;
using System.Text.RegularExpressions;
using Discord.Net.Rest;

namespace LSOverlay.CoreReadProbe;

// Operator-only, bounded REST capture. Never starts a gateway, HTTP listener,
// production host, OAuth flow, slash-command worker, or media downloader.
internal sealed class ReadOnlyRest : IRestClient
{
    internal const int RequestLimit = 40;
    private readonly HttpClient _http;
    private readonly ulong _guild;
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationToken _cancel;
    private ulong _channel;
    private int _requests;
    private bool _historyRead;
    public int Requests => _requests;

    public ReadOnlyRest(ulong guild, HttpMessageHandler? handler = null)
    {
        _guild = guild;
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            CheckCertificateRevocationList = true,
        })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public void SelectChannel(ulong channel)
    {
        if (_channel != 0 || channel == 0) throw new InvalidOperationException("Channel selection is single-use.");
        _channel = channel;
    }

    public void SetHeader(string key, string value)
    {
        _http.DefaultRequestHeaders.Remove(key);
        if (value is not null) _http.DefaultRequestHeaders.Add(key, value);
    }

    public void SetCancelToken(CancellationToken token) => _cancel = token;

    internal bool Allows(string method, string endpoint)
    {
        if (method != "GET") return false;
        if (endpoint is "users/@me" or "oauth2/applications/@me") return true;
        if (endpoint == $"guilds/{_guild}?with_counts=false" || endpoint == $"guilds/{_guild}/channels") return true;
        if (Regex.IsMatch(endpoint, $"^guilds/{_guild}/members/[1-9][0-9]{{0,19}}$", RegexOptions.CultureInvariant)) return true;
        return _channel != 0 && endpoint == $"channels/{_channel}/messages?limit=20";
    }

    public async Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancellationToken,
        bool headerOnly, string reason, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null!)
    {
        if (headerOnly || !string.IsNullOrEmpty(reason) || requestHeaders?.Any() == true || !Allows(method, endpoint))
            throw new ReadOnlyFailure("request-rejected");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel, cancellationToken);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (_requests >= RequestLimit) throw new InvalidOperationException("Read-only probe request budget exhausted.");
            if (endpoint.StartsWith("channels/", StringComparison.Ordinal))
            {
                if (_historyRead) throw new ReadOnlyFailure("history-already-read");
                _historyRead = true;
            }
            if (_requests != 0) await Task.Delay(350, linked.Token);
            _requests++;
            using var response = await _http.GetAsync(new Uri("https://discord.com/api/v10/" + endpoint),
                HttpCompletionOption.ResponseHeadersRead, linked.Token);
            // Do not retry 429/401/403 or follow redirects with the shared bot token.
            if (!response.IsSuccessStatusCode) throw new ReadOnlyFailure("http-" + (int)response.StatusCode);
            const int maxBytes = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Response budget exceeded.");
            var buffer = new MemoryStream();
            await using var source = await response.Content.ReadAsStreamAsync(linked.Token);
            try
            {
                var chunk = new byte[8192];
                int read;
                while ((read = await source.ReadAsync(chunk, linked.Token)) != 0)
                {
                    if (buffer.Length + read > maxBytes) throw new InvalidDataException("Response budget exceeded.");
                    buffer.Write(chunk, 0, read);
                }
                buffer.Position = 0;
                var headers = response.Headers.Concat(response.Content.Headers)
                    .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
                return new RestResponse(response.StatusCode, headers, buffer);
            }
            catch { buffer.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

    public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancellationToken,
        bool headerOnly, string reason, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null!) =>
        throw new InvalidOperationException("Request bodies are forbidden.");

    public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams,
        CancellationToken cancellationToken, bool headerOnly, string reason,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null!) =>
        throw new InvalidOperationException("Uploads are forbidden.");

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}

internal sealed class ReadOnlyFailure(string code) : InvalidOperationException("Read-only request failed.")
{
    public string Code { get; } = code;
}
