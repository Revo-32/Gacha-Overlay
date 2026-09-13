using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using LSOverlay.Backend.CoreClient;
using LSOverlay.CoreMedia;

namespace LSOverlay.CoreDevBridge;
internal sealed class AuthorizedMedia(string viewer,string token,Func<string,string?> readable,
    Func<string,string,CancellationToken,Task<bool>> authorized,CancellationToken session) : ICoreAnimatedMediaReferences
{
    private readonly byte[] _tokenHash=SHA256.HashData(Encoding.ASCII.GetBytes(token));
    private readonly object _gate=new();
    private MediaCatalog _catalog=new();
    private readonly HashSet<(string Channel,string Message)> _scopes=new();
    public CancellationToken Session => session;
    public bool Accepts(string tokenValue) => !session.IsCancellationRequested && ReadOnlyPolicy.IsToken(tokenValue) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(tokenValue)),_tokenHash);
    public void Revoke(string? message=null)
    {
        lock (_gate) {
            if (message is null) {_catalog=new();_scopes.Clear();return;}
            foreach (var scope in _scopes.Where(scope=>scope.Message==message).ToArray()) {_catalog.RevokeMessage(scope.Channel,scope.Message);_scopes.Remove(scope);}
        }
    }
    public void RevokeChannel(string channel)
    {
        lock (_gate) foreach (var scope in _scopes.Where(scope=>scope.Channel==channel).ToArray()) {
            _catalog.RevokeMessage(scope.Channel,scope.Message);_scopes.Remove(scope);
        }
    }
    public string RegisterEmoji(string messageId,string identity,bool animated) => RegisterCanonical(messageId,"emoji",identity,
        ulong.TryParse(identity,out _) ? "https://cdn.discordapp.com/emojis/"+identity+(animated ? ".gif" : ".png") : null);
    public string RegisterCanonical(string messageId,string kind,string identity,string? assetUrl)
    {
        var channel=readable(messageId);
        if (channel is not null && assetUrl is not null) {
            try {lock (_gate) {var id=_catalog.RegisterCanonical(viewer,channel,messageId,assetUrl);_scopes.Add((channel,messageId));return id;}}
            catch (Exception error) when(error is InvalidDataException or IOException or ArgumentException) { }
        }
        // Unsupported providers remain visible as named fallback. They do not
        // gain an arbitrary URL fetch path and do not invalidate the whole chat.
        return "unavailable-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId+"|"+kind+"|"+identity))).ToLowerInvariant();
    }
    public async Task<string> Resolve(string id,CancellationToken cancellation)
    {
        MediaCatalog catalog; lock (_gate) catalog=_catalog;
        var source=await catalog.ResolveAuthorizedAsync(id,viewer,async (channel,message,token)=>
            !session.IsCancellationRequested && readable(message)==channel && await authorized(channel,message,token) && readable(message)==channel,cancellation);
        lock (_gate) if (!ReferenceEquals(catalog,_catalog) || session.IsCancellationRequested) throw new UnauthorizedAccessException();
        return source;
    }
}

internal sealed class PrivateMediaProxy : IDisposable
{
    private readonly HttpClient _http=new(new SocketsHttpHandler {AllowAutoRedirect=false,UseCookies=false,UseProxy=false,MaxConnectionsPerServer=4}) {Timeout=TimeSpan.FromSeconds(50)};
    private readonly string _secret=File.ReadAllText("/run/secrets/core-media-key").Trim();
    private readonly SemaphoreSlim _slots=new(4,4);
    private readonly BridgeTelemetry _telemetry;
    public PrivateMediaProxy(BridgeTelemetry telemetry) {_telemetry=telemetry;if (_secret.Length!=64 || _secret.Any(c=>!char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid private media credential file.");}
    public async Task Serve(HttpContext context,AuthorizedMedia? active,string id,int width,int height)
    {
        if (active is null || !TryToken(context,out var token) || !active.Accepts(token)) {_telemetry.Count(BridgeSignal.MediaDenied);context.Response.StatusCode=401;return;}
        _telemetry.Count(BridgeSignal.MediaRequests);
        if (id.Length!=48 || id.Any(c=>!char.IsAsciiHexDigit(c))) {context.Response.StatusCode=404;return;}
        if (!_slots.Wait(0)) {context.Response.StatusCode=429;return;}
        try {
            new MediaProfile(width,height).Validate();
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,active.Session); lifetime.CancelAfter(TimeSpan.FromSeconds(50));
            var timer=System.Diagnostics.Stopwatch.StartNew();var source=await active.Resolve(id,lifetime.Token);_telemetry.Timing("permissionBefore",timer.Elapsed.TotalMilliseconds);timer.Restart();
            using var request=new HttpRequestMessage(HttpMethod.Post,"http://lsoverlay-core-media:8080/internal/media");
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_secret);
            request.Content=JsonContent.Create(new {Source=source,Width=width,Height=height});
            using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,lifetime.Token);
            _telemetry.Timing("workerHeaders",timer.Elapsed.TotalMilliseconds);timer.Restart();
            if (response.StatusCode!=HttpStatusCode.OK) {_telemetry.Count(BridgeSignal.MediaFailures);context.Response.StatusCode=response.StatusCode==HttpStatusCode.TooManyRequests ? 429 : 422;return;}
            if (response.Content.Headers.ContentType?.MediaType!="application/vnd.lsoverlay.media-v1" || response.Content.Headers.ContentLength is not long length || length<24 || length>MediaPackage.MaximumBytes)
                throw new InvalidDataException();
            // Permission can change during conversion. Recheck before exposing bytes.
            await active.Resolve(id,lifetime.Token);
            _telemetry.Timing("permissionAfter",timer.Elapsed.TotalMilliseconds);timer.Restart();
            foreach (var name in new[] {"X-Core-Media-Key","X-Core-Media-Width","X-Core-Media-Height","X-Core-Cache-Hit"}) {
                if (!response.Headers.TryGetValues(name,out var values)) throw new InvalidDataException();
                var candidates=values.ToArray();if (candidates.Length!=1) throw new InvalidDataException();var value=candidates[0];
                var valid=name switch {
                    "X-Core-Media-Key"=>value.Length==64 && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                    "X-Core-Cache-Hit"=>value is "0" or "1",
                    _=>int.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var dimension) && dimension is >=1 and <=8192
                };
                if (!valid) throw new InvalidDataException();
                context.Response.Headers[name]=value;
            }
            context.Response.ContentType="application/vnd.lsoverlay.media-v1"; context.Response.ContentLength=length;
            await using var body=await response.Content.ReadAsStreamAsync(lifetime.Token);
            var buffer=new byte[64*1024]; long total=0; int read;
            while ((read=await body.ReadAsync(buffer,lifetime.Token))!=0) {
                total+=read; if (total>length) throw new InvalidDataException();
                await context.Response.Body.WriteAsync(buffer.AsMemory(0,read),lifetime.Token);
            }
            if (total!=length) throw new InvalidDataException();
            _telemetry.Timing("transfer",timer.Elapsed.TotalMilliseconds);
            _telemetry.Count(BridgeSignal.MediaDelivered);_telemetry.Count(BridgeSignal.MediaBytes,total);
            if (context.Response.Headers["X-Core-Cache-Hit"]=="1") _telemetry.Count(BridgeSignal.MediaCacheHits);
        }
        catch (UnauthorizedAccessException) {_telemetry.Count(BridgeSignal.MediaDenied);if (!context.Response.HasStarted) context.Response.StatusCode=403;else context.Abort();}
        catch (Exception error) when(error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ArgumentException) {
            _telemetry.Count(BridgeSignal.MediaFailures);if (!context.Response.HasStarted) {context.Response.Headers.Clear();context.Response.StatusCode=422;} else context.Abort();
        }
        finally {_slots.Release();}
    }
    private static bool TryToken(HttpContext context,out string token)
    {
        token="";var values=context.Request.Headers.Authorization;
        if (values.Count!=1 || values[0] is not string header || !header.StartsWith("Bearer ",StringComparison.Ordinal)) return false;
        token=header[7..];return ReadOnlyPolicy.IsToken(token);
    }
    public void Dispose() {_http.Dispose();_slots.Dispose();}
}
