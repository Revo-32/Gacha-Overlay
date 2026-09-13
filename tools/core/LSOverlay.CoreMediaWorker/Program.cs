using System.Text.Json;
using LSOverlay.CoreMedia;

// Isolated one-shot worker, not an HTTP server. Only the Backend/operator may
// provide a canonical CDN source on stdin after message authorization. No bot or
// OAuth credential is needed. Parent/container MUST enforce a kill deadline and
// memory limit because cancellation cannot interrupt a hung native codec call.
if (args is not ["--convert", var root] || !Path.IsPathFullyQualified(root)) return 2;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var input = new char[8193]; var used = 0;
    while (true)
    {
        var read = await Console.In.ReadAsync(input.AsMemory(used), timeout.Token);
        if (read == 0) break;
        used += read; if (used > 8192) throw new InvalidDataException();
    }
    var job = JsonSerializer.Deserialize<Job>(input.AsSpan(0, used)) ?? throw new InvalidDataException(); Array.Clear(input);
    using var cache = new DerivativeCache(root);
    using var client = MediaSourcePolicy.CreateClient();
    var key = await MediaSourcePolicy.FetchAndConvertAsync(client, cache, job.CanonicalSource, new(job.Width, job.Height), timeout.Token);
    using var lease = cache.Open(key); var package = MediaPackage.Read(lease);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", key, package.Width, package.Height, frames = package.Frames.Count, package.TotalPlays, bytes = lease.Length, cache.Hits, cache.Conversions }));
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // Do not expose signed source URLs, query strings or raw network exceptions.
    Console.Error.WriteLine("Core media worker stopped: " + ex.GetType().Name);
    return 1;
}
internal sealed record Job(string CanonicalSource, int Width, int Height);
