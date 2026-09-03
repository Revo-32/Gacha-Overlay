using System.Net;
using System.Text;
using System.Text.Json;

namespace LSOverlay.Backend.PublicWeb;

internal static class PublicServicePages
{
    internal const string PrivacyUrl = "https://overlay.revo32.cloud/privacy";
    internal const string TermsUrl = "https://overlay.revo32.cloud/terms";
    internal const string StatusOrigin = "https://status.revo32.cloud";
    internal const string UpdatedDate = "2026-09-03";

    // Operator-confirmed public contact. Both documents derive their visible
    // address and mailto link from this ONE email source; it is not a secret.
    internal const string ContactEmail = "revo.32.39.41@gmail.com";
    internal const string ContactUrl = "mailto:" + ContactEmail;
    internal const string ContactReadiness = "PUBLIC CONTACT VERIFIED";

    internal const string LegalCsp = "default-src 'none'; img-src 'self'; style-src 'self'; " +
        "script-src 'none'; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> Privacy = new(() => Render("개인정보처리방침", "privacy"));
    private static readonly Lazy<string> Terms = new(() => Render("이용약관", "terms"));
    private static readonly Lazy<byte[]> Css = new(() => Asset("site.css"));
    private static readonly Lazy<byte[]> Logo = new(() => Asset("logo.png"));

    internal static void MapPublicServicePages(this WebApplication app, Func<PublicStatusSnapshot>? captureStatus = null)
    {
        // Overrides exist only in the separate offline preview/tests, never in
        // a production request/query parameter or environment toggle.
        captureStatus ??= () => app.Services.GetRequiredService<PublicStatusService>().Capture();
        app.MapGet("/privacy", (HttpContext context) => LegalResult(context, Privacy.Value));
        app.MapGet("/terms", (HttpContext context) => LegalResult(context, Terms.Value));
        app.MapGet("/public/assets/site.css", (HttpContext context) => AssetResult(context, Css.Value, "text/css; charset=utf-8"));
        app.MapGet("/public/assets/ls-overlay-logo.png", (HttpContext context) => AssetResult(context, Logo.Value, "image/png"));
        app.MapGet("/status/public", (HttpContext context) =>
        {
            StatusHeaders(context);
            return Results.Json(captureStatus(), JsonOptions);
        });
        app.MapMethods("/status/public", new[] { "OPTIONS" }, (HttpContext context) =>
        {
            StatusHeaders(context);
            if (context.Request.Headers.Origin != StatusOrigin ||
                context.Request.Headers.AccessControlRequestMethod != "GET" ||
                context.Request.Headers.ContainsKey("Access-Control-Request-Headers"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            context.Response.Headers.AccessControlAllowMethods = "GET";
            return Results.NoContent();
        });
    }

    private static void StatusHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.Vary = "Origin";
        if (context.Request.Headers.Origin == StatusOrigin)
            context.Response.Headers.AccessControlAllowOrigin = StatusOrigin;
        // No credentialed CORS and no change to OAuth/Remote endpoint policies.
    }

    private static IResult LegalResult(HttpContext context, string html)
    {
        context.Response.Headers.ContentSecurityPolicy = LegalCsp;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    private static IResult AssetResult(HttpContext context, byte[] bytes, string contentType)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Bytes(bytes, contentType);
    }

    internal static string Render(string title, string page)
    {
        if (page is not ("privacy" or "terms")) throw new ArgumentException("Unknown public document.", nameof(page));
        var contact = $"<p>LS Overlay 이용 문의·개인정보 관련 문의·데이터 삭제 요청: " +
            $"<a href=\"{WebUtility.HtmlEncode(ContactUrl)}\">{WebUtility.HtmlEncode(ContactEmail)}</a></p>";
        var body = Encoding.UTF8.GetString(Asset(page + ".html"));
        var canonical = page == "privacy" ? PrivacyUrl : TermsUrl;
        return $$"""
            <!doctype html>
            <html lang="ko">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="description" content="LS Overlay {{WebUtility.HtmlEncode(title)}} — 서비스의 이용과 데이터 처리를 안내합니다.">
              <title>LS Overlay {{WebUtility.HtmlEncode(title)}}</title>
              <link rel="canonical" href="{{canonical}}">
              <link rel="stylesheet" href="/public/assets/site.css">
            </head>
            <body>
              <a class="skip" href="#content">본문으로 건너뛰기</a>
              <header class="site-header"><img src="/public/assets/ls-overlay-logo.png" alt="LS Overlay 로고" class="logo"><span>서비스 안내</span></header>
              <main id="content" class="document">
                <p class="eyebrow">LS OVERLAY</p>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p class="updated">최종 업데이트: <time datetime="{{UpdatedDate}}">{{UpdatedDate}}</time></p>
                {{body}}
                <section aria-labelledby="contact"><h2 id="contact">문의 및 데이터 삭제 요청</h2>{{contact}}
                <p>문의 시 계정 식별에 필요한 최소한의 정보만 전달해 주세요. 비밀번호, 인증 토큰, 로그인 코드, 클라이언트 시크릿은 보내지 마세요.</p></section>
              </main>
              <footer><nav aria-label="서비스 문서"><a href="{{PrivacyUrl}}">개인정보처리방침</a><a href="{{TermsUrl}}">이용약관</a><a href="{{StatusOrigin}}">서비스 상태</a></nav><p>LS Overlay 운영자</p></footer>
            </body>
            </html>
            """;
    }

    private static byte[] Asset(string name)
    {
        using var stream = typeof(PublicServicePages).Assembly.GetManifestResourceStream("LSOverlay.PublicWeb." + name)
            ?? throw new InvalidOperationException("Required public asset is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
