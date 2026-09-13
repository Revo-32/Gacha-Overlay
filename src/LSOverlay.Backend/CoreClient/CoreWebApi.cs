using System.Net.WebSockets;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;

internal static class CoreWebApi
{
    internal static void MapCoreApi(this WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/core");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            // Native clients never need browser-origin or credential query paths.
            if (http.Request.Headers.ContainsKey("Origin") || http.Request.QueryString.HasValue)
                return Results.BadRequest();
            return await next(context);
        });
        routes.MapGet("/manifest", () => Results.Json(new
        {
            protocolVersion = 1,
            readOnly = false,
            authOrigin = "https://overlay.revo32.cloud",
            mediaDelivery = true,
            channelSelection = true,
            salesActions = true,
        }));
        routes.MapPost("/channel/{slot:int}", (HttpContext context, int slot, ClientCredentialRegistry credentials, CoreSessionRegistry sessions) =>
        {
            var identity = Authenticate(context, credentials);
            if (identity is null) return Results.Unauthorized();
            return sessions.Find(identity)?.Select(slot) == true ? Results.Json(new { accepted = true }) : Results.StatusCode(403);
        });
        routes.MapGet("/media/{id}/{width:int}/{height:int}", async (HttpContext context, string id, int width, int height,
            ClientCredentialRegistry credentials, CoreSessionRegistry sessions, CoreMediaGateway media) =>
        {
            var identity = Authenticate(context, credentials);
            if (identity is null) { context.Response.StatusCode = 401; return; }
            var session = sessions.Find(identity);
            if (session is null) { context.Response.StatusCode = 403; return; }
            await media.Serve(context, session.Media, id, width, height);
        });
        routes.MapGet("/stream", Stream);
    }

    private static AuthenticatedClientIdentity? Authenticate(HttpContext context, ClientCredentialRegistry credentials) =>
        TransportAuthentication.HasForbiddenCredentialQuery(context.Request) ? null : TransportAuthentication.AuthenticateBearer(context.Request, credentials);

    private static async Task Stream(HttpContext context, ClientCredentialRegistry credentials, IGuildMembershipVerifier membership,
        RemoteConnectionLimiter limiter, CoreSessionRegistry sessions, RemoteChatService chat, RemoteSalesService sales,
        RemotePublicationHub presence, IHostApplicationLifetime application, ILogger<CoreUserSession> logger)
    {
        var identity = Authenticate(context, credentials);
        if (identity is null) { context.Response.StatusCode = 401; return; }
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(OverlayTransportProtocol.WebSocketSubprotocol, StringComparer.Ordinal))
        { context.Response.StatusCode = 400; return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, application.ApplicationStopping);
        var status = await membership.VerifyAsync(identity, lifetime.Token);
        if (status != GuildMembershipStatus.Member) { context.Response.StatusCode = status == GuildMembershipStatus.NotMember ? 403 : 503; return; }
        using var lease = limiter.TryAcquire(identity);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        using var session = new CoreUserSession(identity, chat, sales, presence, membership,
            () => Authenticate(context, credentials) == identity, lifetime.Token);
        if (!sessions.Add(session)) { context.Response.StatusCode = 429; return; }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(OverlayTransportProtocol.WebSocketSubprotocol);
            await session.Run(socket, lifetime.Token);
        }
        catch (Exception error)
        {
            // Never log exception messages, request headers, URLs or chat content.
            logger.LogInformation("Core connection ended ({ErrorType})", error.GetType().Name);
            context.Abort();
        }
        finally { sessions.Remove(session); }
    }
}
