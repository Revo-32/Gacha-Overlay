using System.Net;
using System.Net.Http.Headers;
using LSOverlay.CoreMedia;

namespace LSOverlay.Backend.CoreClient;

// The worker alone downloads/decodes external media. It has no Discord/OAuth credentials.
internal sealed class CoreMediaGateway : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _secret;
    private readonly ILogger<CoreMediaGateway>? _logger;
    private readonly SemaphoreSlim _slots = new(8, 8);
    public CoreMediaGateway(ILogger<CoreMediaGateway> logger) : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, MaxConnectionsPerServer = 8 },
        File.ReadAllText("/run/secrets/core-media-key").Trim())
    { _logger = logger; }
    internal CoreMediaGateway(HttpMessageHandler handler, string secret)
    {
        if (secret.Length != 64 || secret.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid private media credential file.");
        _secret = secret; _http = new(handler) { Timeout = TimeSpan.FromSeconds(50) };
    }
    public async Task Serve(HttpContext context, CoreMediaScope scope, string id, int width, int height)
    {
        if (id.Length != 48 || id.Any(c => !char.IsAsciiHexDigit(c))) { context.Response.StatusCode = 404; return; }
        if (!scope.TryEnter()) { context.Response.StatusCode = 429; return; }
        if (!_slots.Wait(0)) { scope.Leave(); context.Response.StatusCode = 429; return; }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double authorizationMs = 0, workerMs = 0;
        try
        {
            new MediaProfile(width, height).Validate();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, scope.Session);
            lifetime.CancelAfter(TimeSpan.FromSeconds(50));
            var source = await scope.Resolve(id, lifetime.Token);
            authorizationMs = timer.Elapsed.TotalMilliseconds;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:15191/internal/media");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
            request.Content = JsonContent.Create(new { Source = source, Width = width, Height = height });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            workerMs = timer.Elapsed.TotalMilliseconds - authorizationMs;
            if (response.StatusCode != HttpStatusCode.OK) { context.Response.StatusCode = response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : 422; return; }
            if (response.Content.Headers.ContentType?.MediaType != "application/vnd.lsoverlay.media-v1" || response.Content.Headers.ContentLength is not long length || length < 24 || length > MediaPackage.MaximumBytes)
                throw new InvalidDataException();
            // Revalidate after potentially slow conversion, before the first response byte.
            await scope.Resolve(id, lifetime.Token);
            authorizationMs = timer.Elapsed.TotalMilliseconds - workerMs;
            foreach (var name in new[] { "X-Core-Media-Key", "X-Core-Media-Width", "X-Core-Media-Height", "X-Core-Cache-Hit" })
            {
                if (!response.Headers.TryGetValues(name, out var values)) throw new InvalidDataException();
                var candidates = values.ToArray(); if (candidates.Length != 1) throw new InvalidDataException(); var value = candidates[0];
                var valid = name switch
                {
                    "X-Core-Media-Key" => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                    "X-Core-Cache-Hit" => value is "0" or "1",
                    _ => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var dimension) && dimension is >= 1 and <= 8192,
                };
                if (!valid) throw new InvalidDataException();
                context.Response.Headers[name] = value;
            }
            context.Response.ContentType = "application/vnd.lsoverlay.media-v1"; context.Response.ContentLength = length;
            await using var body = await response.Content.ReadAsStreamAsync(lifetime.Token);
            var buffer = new byte[64 * 1024]; long total = 0; int read;
            while ((read = await body.ReadAsync(buffer, lifetime.Token)) != 0)
            {
                total += read; if (total > length) throw new InvalidDataException();
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), lifetime.Token);
            }
            if (total != length) throw new InvalidDataException();
        }
        catch (UnauthorizedAccessException) { if (!context.Response.HasStarted) context.Response.StatusCode = 403; else context.Abort(); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ArgumentException)
        { if (!context.Response.HasStarted) { context.Response.Headers.Clear(); context.Response.Headers.CacheControl = "no-store"; context.Response.StatusCode = 422; } else context.Abort(); }
        finally
        {
            if (timer.ElapsedMilliseconds >= 250)
                _logger?.LogInformation("Core media timing: elapsedMs={ElapsedMs:F1} authorizationMs={AuthorizationMs:F1} workerMs={WorkerMs:F1} status={Status}",
                    timer.Elapsed.TotalMilliseconds, authorizationMs, workerMs, context.Response.StatusCode);
            _slots.Release(); scope.Leave();
        }
    }
    public void Dispose() { _http.Dispose(); _slots.Dispose(); }
}
