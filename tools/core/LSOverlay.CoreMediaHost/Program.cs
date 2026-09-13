using System.Security.Cryptography;
using System.Text;
using LSOverlay.CoreMedia;

// Separate resource-limited DEV container only; no Backend/Discord credentials.
// The watchdog exits THIS media process on a hung native codec, never Chat.
if (args.Length!=0) return 2;
var secret=File.ReadAllText("/run/secrets/core-media-key").Trim();
if (secret.Length!=64 || secret.Any(c=>!char.IsAsciiHexDigit(c))) return 2;
var secretHash=SHA256.HashData(Encoding.ASCII.GetBytes(secret));
using var cache=new DerivativeCache("/cache");
using var cdn=MediaSourcePolicy.CreateClient();
using var slots=new SemaphoreSlim(4,4);
using var serial=new SemaphoreSlim(1,1);
var aliases=new System.Collections.Concurrent.ConcurrentDictionary<string,string>();
var diagnostics=new MediaDiagnostics();
var builder=WebApplication.CreateBuilder(args); builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options=> { options.Limits.MaxRequestBodySize=8192; options.Limits.MaxConcurrentConnections=8; });
var app=builder.Build();
app.Use(async (context,next)=> {
    var auth=context.Request.Headers.Authorization;
    if (context.Request.QueryString.HasValue || auth.Count!=1 || auth[0] is not string value || value.Length!=71 || !value.StartsWith("Bearer ",StringComparison.Ordinal) ||
        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(value[7..])),secretHash)) {context.Response.StatusCode=401;return;}
    context.Response.Headers.CacheControl="no-store"; await next();
});
app.MapPost("/internal/media",(RequestDelegate)ProcessMedia);
app.MapGet("/internal/diagnostics",()=>Results.Json(diagnostics.Snapshot()));
async Task ProcessMedia(HttpContext context) {
    if (!context.Request.HasJsonContentType()) {context.Response.StatusCode=415;return;}
    if (!slots.Wait(0)) {context.Response.StatusCode=429;return;}
    Stream? lease=null;
    var stage="request";
    try {
        var job=await context.Request.ReadFromJsonAsync<Job>(context.RequestAborted) ?? throw new InvalidDataException();
        if (string.IsNullOrWhiteSpace(job.Source)) throw new InvalidDataException();
        var source=MediaSourcePolicy.Validate(job.Source); var profile=new MediaProfile(job.Width,job.Height); profile.Validate();
        stage="queue";
        var timer=System.Diagnostics.Stopwatch.StartNew();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var alias=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri+"|"+job.Width+"|"+job.Height)));
        // A committed, leased derivative does not wait for unrelated cold codecs.
        // Keep full package validation; no unvalidated bytes or quality downgrade.
        if(aliases.TryGetValue(alias,out var warmKey)) {
            try {
                lease=cache.Open(warmKey);var warm=MediaPackage.Read(lease);
                for(var i=0;i<warm.Frames.Count;i++){deadline.Token.ThrowIfCancellationRequested();warm.ReadFrame(lease,i);}lease.Position=0;
                diagnostics.Timing("warmValidation",timer.Elapsed.TotalMilliseconds);diagnostics.Ready(warm,true,0,timer.Elapsed.TotalMilliseconds);diagnostics.ProviderReady(source);
                context.Response.ContentType="application/vnd.lsoverlay.media-v1";context.Response.ContentLength=lease.Length;
                context.Response.Headers["X-Core-Media-Key"]=warmKey;context.Response.Headers["X-Core-Media-Width"]=warm.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Core-Media-Height"]=warm.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);context.Response.Headers["X-Core-Cache-Hit"]="1";
            }catch(Exception e)when(e is IOException or InvalidDataException){lease?.Dispose();lease=null;aliases.TryRemove(alias,out _);}
            if(lease is not null){stage="delivery";await lease.CopyToAsync(context.Response.Body,64*1024,deadline.Token);return;}
        }
        await serial.WaitAsync(deadline.Token);
        var queueMs=timer.Elapsed.TotalMilliseconds;
        try {
            using var finished=new ManualResetEventSlim(false);
            var watchdog=new Thread(()=> {if (!finished.Wait(TimeSpan.FromSeconds(45))) Environment.Exit(70);}) {IsBackground=true,Name="CoreMediaCodecDeadline"};
            watchdog.Start();
            try {
                var hit=false; string? key=null;
                if (aliases.TryGetValue(alias,out key)) {
                    try {lease=cache.Open(key); var existing=MediaPackage.Read(lease); for (var i=0;i<existing.Frames.Count;i++) existing.ReadFrame(lease,i); hit=true;}
                    catch (Exception e) when(e is IOException or InvalidDataException) {lease?.Dispose();lease=null;aliases.TryRemove(alias,out _);}
                }
                if (lease is null) {
                    stage="conversion";
                    key=await Task.Run(()=>MediaSourcePolicy.FetchAndConvertAsync(cdn,cache,source.AbsoluteUri,profile,deadline.Token,diagnostics.Timing),deadline.Token);
                    lease=cache.Open(key);
                    if (aliases.Count>=512)aliases.TryRemove(aliases.Keys.First(),out _);
                    aliases[alias]=key;
                }
                var package=MediaPackage.Read(lease); lease.Position=0;
                diagnostics.Ready(package,hit,queueMs,timer.Elapsed.TotalMilliseconds-queueMs);diagnostics.ProviderReady(source);
                context.Response.ContentType="application/vnd.lsoverlay.media-v1"; context.Response.ContentLength=lease.Length;
                context.Response.Headers["X-Core-Media-Key"]=key;
                context.Response.Headers["X-Core-Media-Width"]=package.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Core-Media-Height"]=package.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Core-Cache-Hit"]=hit ? "1" : "0";
            } finally {finished.Set();watchdog.Join();}
        } finally {serial.Release();}
        // Cache lease stays held through delivery, including on Linux eviction.
        stage="delivery";
        await lease!.CopyToAsync(context.Response.Body,64*1024,deadline.Token);
    }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException or HttpRequestException or BadHttpRequestException) {
        diagnostics.Failed(stage,e);
        if (!context.Response.HasStarted) {context.Response.Headers.Clear();context.Response.StatusCode=422;} else context.Abort();
    }
    finally {lease?.Dispose();slots.Release();}
}
await app.RunAsync();
return 0;
internal sealed record Job(string Source,int Width,int Height);
