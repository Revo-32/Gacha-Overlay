using LSOverlay.Backend.Configuration;
using LSOverlay.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LSOverlay.Backend.WebAuth;

internal static class DiscordWebAuthEndpoints
{
    public const string SessionsPath = "/api/v1/auth/discord/sessions";
    public static void MapDiscordWebAuth(this WebApplication app)
    {
        if (app.Services.GetService<BackendConfiguration>()?.WebAuth is null) return;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(SessionsPath) || context.Request.Path == DiscordWebAuthOptions.CallbackPath)
            {
                SetHeaders(context.Response);
                var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 1024;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapPost(SessionsPath, async (HttpContext context, DiscordWebAuthService sessions, WebAuthRateLimiter limiter) =>
        {
            if (context.Request.QueryString.HasValue) return Results.BadRequest();
            if (!Allowed(context, limiter, 0)) return Results.StatusCode(429);
            try
            {
                var request = await context.Request.ReadFromJsonAsync<DiscordWebAuthStartRequest>(OverlayProtocolJson.Options, context.RequestAborted).ConfigureAwait(false);
                if (request is null) return Results.BadRequest();
                OverlayProtocolJson.EnsureVersion(request.ProtocolVersion);
                return Results.Json(sessions.Start(request.ClientInstallationId));
            }
            catch (NotSupportedException) { return Results.StatusCode(426); }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
            catch (InvalidOperationException) { return Results.StatusCode(503); }
        });
        app.MapMethods(SessionsPath + "/{sessionId:guid}", new[] { "GET", "DELETE" },
            (HttpContext context, Guid sessionId, DiscordWebAuthService sessions, WebAuthRateLimiter limiter) =>
            {
                if (context.Request.QueryString.HasValue) return Results.BadRequest();
                if (!Allowed(context, limiter, 1)) return Results.StatusCode(429);
                var header = context.Request.Headers.Authorization;
                const string scheme = "LSOAuthClaim ";
                if (header.Count != 1 || header[0] is not string value || !value.StartsWith(scheme, StringComparison.Ordinal) || value.Length != scheme.Length + 43)
                    return Results.Unauthorized();
                try { return Results.Json(sessions.Claim(sessionId, value[scheme.Length..], HttpMethods.IsDelete(context.Request.Method))); }
                catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { return Results.StatusCode(503); }
            });
        app.MapGet(DiscordWebAuthOptions.CallbackPath, async (HttpContext context, DiscordWebAuthService sessions,
            WebAuthRateLimiter limiter, IHostApplicationLifetime lifetime) =>
        {
            if (!Allowed(context, limiter, 2)) return Page(DiscordWebAuthFailure.TemporaryFailure, 429);
            if (context.Request.QueryString.Value?.Length > 4096) return Page(DiscordWebAuthFailure.InvalidRequest, 400);
            var query = context.Request.Query;
            if (query.Any(pair => pair.Value.Count != 1) || query.Keys.Any(key => key is not ("code" or "state" or "error" or "error_description")) ||
                query.ContainsKey("code") && query.ContainsKey("error")) return Page(DiscordWebAuthFailure.InvalidRequest, 400);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            var failure = await sessions.CompleteAsync(query["state"], query["code"], query["error"], cancellation.Token).ConfigureAwait(false);
            return Page(failure, failure == DiscordWebAuthFailure.None ? 200 : 400);
        });
    }
    private static bool Allowed(HttpContext context, WebAuthRateLimiter limiter, int operation) =>
        limiter.Allow(context.Connection.RemoteIpAddress?.ToString() ?? "unavailable", operation);

    internal static void SetHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    }
    internal static IResult Page(DiscordWebAuthFailure failure, int status)
    {
        var message = failure switch
        {
            DiscordWebAuthFailure.None => "Discord 연결이 완료되었습니다. 이 창을 닫고 LS Overlay로 돌아가세요.",
            DiscordWebAuthFailure.Cancelled => "인증이 취소되었습니다.",
            DiscordWebAuthFailure.SessionExpired => "인증 시간이 만료되었습니다.",
            DiscordWebAuthFailure.NotMember => "이 Discord 계정은 서버 구성원이 아닙니다.",
            DiscordWebAuthFailure.VerificationUnavailable => "서버 구성원 여부를 확인할 수 없습니다.",
            DiscordWebAuthFailure.TemporaryFailure => "일시적으로 Discord 계정을 확인할 수 없습니다.",
            _ => "유효하지 않거나 이미 사용한 인증 요청입니다.",
        };
        var retry = failure == DiscordWebAuthFailure.None ? "" : "<p>LS Overlay에서 다시 Discord 로그인을 시도해주세요.</p>";
        return Results.Content("<!doctype html><html lang=\"ko\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
            "<title>LS Overlay</title><style>body{background:#151927;color:#f2f4ff;font:18px system-ui;margin:10vh auto;padding:24px;max-width:640px}h1{color:#80baff}p{line-height:1.7}</style>" +
            "<h1>LS Overlay</h1><p>" + message + "</p>" + retry + "</html>", "text/html; charset=utf-8", statusCode: status);
    }
}
