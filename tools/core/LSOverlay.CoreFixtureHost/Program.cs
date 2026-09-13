// Isolated M0 infrastructure probe. No Discord, OAuth, application data or media fetch.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
app.MapGet("/healthz", () => Results.Json(new { status = "ok", environment = "core-fixture-only" }));
app.MapGet("/fixture/manifest", () => Results.Json(new
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
