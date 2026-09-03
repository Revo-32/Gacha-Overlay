using System.Text.Json;
using LSOverlay.Backend.PublicWeb;

// Standalone OFFLINE fixture host. It never creates Program.CreateHost,
// DiscordSocketClient, credentials, OAuth sessions or a Remote connection.
var root = Path.GetFullPath(args[0]);
var port = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5191;
if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
var site = Path.Combine(root, "web", "status", "public");
var logo = Path.Combine(root, "assets", "branding", "LS_Overlay_logo.png");
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    EnvironmentName = "Development",
    ContentRootPath = root,
});
builder.Logging.ClearProviders();
var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
PublicStatusSnapshot Snapshot(string value)
{
    var state = Enum.Parse<PublicStatusState>(value, true);
    return new(1, state, DateTimeOffset.UtcNow, new(state, state, state, state));
}
app.MapPublicServicePages(() => Snapshot("operational"));
app.MapGet("/", () => Results.Content("""
    <!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>LS Overlay 오프라인 미리보기</title><link rel="stylesheet" href="/public/assets/site.css">
    <main><h1>로컬 검증 화면</h1><p>아래 상태는 모의 데이터입니다. Discord 및 운영 서버에 연결하지 않습니다.</p>
    <nav><a href="/privacy">개인정보처리방침</a><a href="/terms">이용약관</a></nav>
    <ul><li><a href="/preview/operational/">정상</a></li><li><a href="/preview/degraded/">일부 지연</a></li>
    <li><a href="/preview/maintenance/">점검 중</a></li><li><a href="/preview/unavailable/">이용 불가</a></li>
    <li><a href="/preview/unknown/">상태 확인 중</a></li><li><a href="/preview/error/">API 연결 실패</a></li></ul></main></html>
    """, "text/html; charset=utf-8"));
var states = new HashSet<string>(new[] { "operational", "degraded", "maintenance", "unavailable", "unknown", "error" });
app.MapGet("/fixtures/{state}", (string state) => !states.Contains(state) ? Results.NotFound() :
    state == "error" ? Results.StatusCode(503) : Results.Json(Snapshot(state), json));
app.MapGet("/preview/{state}/", (string state) =>
{
    if (!states.Contains(state)) return Results.NotFound();
    var html = File.ReadAllText(Path.Combine(site, "index.html"))
        .Replace("connect-src https://overlay.revo32.cloud", "connect-src 'self'", StringComparison.Ordinal)
        .Replace("CURRENT STATUS", "OFFLINE PREVIEW · 모의 상태", StringComparison.Ordinal);
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapGet("/preview/{state}/status.js", (string state) => !states.Contains(state) ? Results.NotFound() :
    Results.Content(File.ReadAllText(Path.Combine(site, "status.js"))
        .Replace("https://overlay.revo32.cloud/status/public", "/fixtures/" + state, StringComparison.Ordinal), "text/javascript; charset=utf-8"));
app.MapGet("/preview/{state}/styles.css", () => Results.File(Path.Combine(site, "styles.css"), "text/css; charset=utf-8"));
app.MapGet("/preview/{state}/assets/ls-overlay-logo.png", () => Results.File(logo, "image/png"));
app.MapGet("/responsive/{width:int}/{page}", (int width, string page) =>
{
    if (width is not (320 or 768 or 1280) || !(states.Contains(page) || page is "privacy" or "terms")) return Results.NotFound();
    // Same-origin srcdoc frames provide actual mobile/tablet CSS viewports
    // without changing the user's browser window. Only this offline tool has them.
    var html = page is "privacy" or "terms"
        ? PublicServicePages.Render(page == "privacy" ? "개인정보처리방침" : "이용약관", page)
        : File.ReadAllText(Path.Combine(site, "index.html"))
            .Replace("connect-src https://overlay.revo32.cloud", "connect-src 'self'", StringComparison.Ordinal)
            .Replace("href=\"styles.css\"", $"href=\"/preview/{page}/styles.css\"", StringComparison.Ordinal)
            .Replace("src=\"status.js\"", $"src=\"/preview/{page}/status.js\"", StringComparison.Ordinal)
            .Replace("src=\"assets/ls-overlay-logo.png\"", $"src=\"/preview/{page}/assets/ls-overlay-logo.png\"", StringComparison.Ordinal);
    return Results.Content($"<!doctype html><html lang=\"ko\"><meta charset=\"utf-8\"><title>Offline viewport {width}</title>" +
        $"<body style=\"margin:0;background:#111714;color:#e7eee9;font-family:system-ui\"><p>OFFLINE QA · {width}px · {page}</p>" +
        $"<iframe title=\"{page} {width}px\" width=\"{width}\" height=\"900\" style=\"border:0\" srcdoc=\"{System.Net.WebUtility.HtmlEncode(html)}\"></iframe></body></html>", "text/html; charset=utf-8");
});
Console.WriteLine($"Offline preview: http://127.0.0.1:{port}/ (Ctrl+C to stop)");
await app.RunAsync($"http://127.0.0.1:{port}");
