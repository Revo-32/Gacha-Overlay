using LSOverlay.Backend.CoreClient;
using LSOverlay.CoreFixtureHost;
using System.Text;

// Synthetic contract fixture only. Never imports Production settings/credentials.
if (args is ["--export-chat", var directory])
{
    ChatFixtureData.Export(directory);
    return;
}
if (args is ["--export-wire", var path])
{
    var frames = CoreSnapshotWire.Encode(CoreFixtureData.Create("cross-language", long.MaxValue - 1, large: true));
    File.WriteAllLines(path, frames.Select(frame => Encoding.UTF8.GetString(frame)), new UTF8Encoding(false));
    return;
}
var m3 = args.Contains("--m3-chat-fixture", StringComparer.Ordinal);
var m2 = m3 || args.Contains("--m2-contract-fixture", StringComparer.Ordinal);
var builder = WebApplication.CreateSlimBuilder(args.Where(arg => arg is not ("--m2-contract-fixture" or "--m3-chat-fixture")).ToArray());
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
var app = builder.Build();
app.MapGet("/healthz", () => Results.Json(new { status = "ok", environment = "core-fixture-only" }));
if (m2) new CoreContractFixture(m3).Map(app);
else app.MapGet("/fixture/manifest", () => Results.Json(new
{
    product = "LS Overlay Core",
    stage = "M0 infrastructure only",
    sourceBaseline = "1f9dcd53afad400298d0ca2aff7b58e92d1b9aea",
    liveDiscord = false,
    authenticationImplemented = false,
    coreProtocolImplemented = false,
    productionDataMounted = false,
    mediaFetchEnabled = false,
}));
await app.RunAsync();
