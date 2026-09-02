using System.Net.WebSockets;
using System.Threading.RateLimiting;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Pairing;
using LSOverlay.Backend.Sales;
using LSOverlay.Protocol;
using LSOverlay.Backend.WebAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace LSOverlay.Backend.Transport;

internal static class BackendWebApi
{
    public const string PairingCreatePolicy = "pairing-create";
    public const string PairingClaimPolicy = "pairing-claim";

    public static void AddTransportRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PairingCreatePolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }));
            options.AddPolicy(PairingClaimPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }));
        });
    }

    public static void MapTransportApi(this WebApplication app)
    {
        app.UseBackendTransportSecurity();
        app.MapDiscordWebAuth();
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.Zero,
        });

        app.MapGet("/healthz", () => BackendTransportHosting.HealthResult(app.Services));

        app.MapPost("/api/v1/pairings", (
            HttpContext context,
            CreatePairingRequest request,
            PairingService pairing,
            Security.ClientCredentialRegistry credentials,
            TransportMetrics metrics) =>
        {
            if (TransportAuthentication.HasForbiddenCredentialQuery(context.Request))
            {
                return Results.BadRequest();
            }

            if (credentials.IsFaulted)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                OverlayProtocolJson.EnsureVersion(request.ProtocolVersion);
                var created = pairing.Create(request.ClientInstallationId);
                metrics.Increment(TransportMetric.PairingCreated);
                return Results.Json(new CreatePairingResponse(
                    OverlayTransportProtocol.Version,
                    created.PairingId,
                    created.UserCode,
                    created.PairingClaimSecret,
                    created.ExpiresAt));
            }
            catch (NotSupportedException)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
            catch (InvalidOperationException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting(PairingCreatePolicy);

        app.MapGet("/api/v1/pairings/{pairingId:guid}", (
            HttpContext context,
            Guid pairingId,
            PairingService pairing,
            TransportMetrics metrics) =>
        {
            if (TransportAuthentication.HasForbiddenCredentialQuery(context.Request) ||
                !TransportAuthentication.TryReadPairingClaim(context.Request, out var secret))
            {
                metrics.Increment(TransportMetric.PairingClaimRejected);
                return Results.Unauthorized();
            }

            try
            {
                var result = pairing.Claim(pairingId, secret);
                if (result.State == PairingState.Expired)
                {
                    metrics.Increment(TransportMetric.PairingExpired);
                }

                return Results.Json(new PairingClaimResponse(
                    OverlayTransportProtocol.Version,
                    result.State,
                    result.Credential?.AccessToken,
                    result.Credential?.ExpiresAt));
            }
            catch (UnauthorizedAccessException)
            {
                metrics.Increment(TransportMetric.PairingClaimRejected);
                return Results.Unauthorized();
            }
            catch (InvalidOperationException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting(PairingClaimPolicy);

        app.MapGet("/api/v1/bootstrap", async (
            HttpContext context,
            Security.ClientCredentialRegistry credentials,
            IGuildMembershipVerifier membership,
            RemotePublicationHub publication,
            TransportMetrics metrics) =>
        {
            var identity = Authenticate(context, credentials, metrics);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            var membershipStatus = await membership
                .VerifyAsync(identity, context.RequestAborted)
                .ConfigureAwait(false);
            if (membershipStatus == GuildMembershipStatus.NotMember)
            {
                // Authentication is handled by the protocol credential registry,
                // not an ASP.NET authentication scheme. Forbid() invokes a scheme
                // handler and throws here; return the denial status directly.
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (membershipStatus == GuildMembershipStatus.VerificationUnavailable)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            metrics.Increment(TransportMetric.BootstrapRequests);
            return Results.Json(publication.CaptureBootstrap(identity));
        });

        app.MapGet("/api/v1/chat/channels", async (
            HttpContext context,
            Security.ClientCredentialRegistry credentials,
            [FromServices] RemoteChatService chat,
            TransportMetrics metrics) =>
        {
            var identity = Authenticate(context, credentials, metrics);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            var result = await chat.GetCatalogAsync(identity, context.RequestAborted)
                .ConfigureAwait(false);
            return result.Status switch
            {
                ChatAuthorizationStatus.Authorized => Results.Json(
                    new ChatChannelCatalogResponse(
                        OverlayTransportProtocol.Version,
                        result.AuthorizedChannels)),
                ChatAuthorizationStatus.AccessRevoked => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            };
        });

        app.MapPost("/api/v1/chat/bootstrap", async (
            HttpContext context,
            ChatBootstrapRequest request,
            Security.ClientCredentialRegistry credentials,
            [FromServices] RemoteChatService chat,
            TransportMetrics metrics) =>
        {
            var identity = Authenticate(context, credentials, metrics);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await chat.BootstrapAsync(
                        identity,
                        request,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return result.Status switch
                {
                    ChatAuthorizationStatus.Authorized when result.Response is not null =>
                        Results.Json(result.Response),
                    ChatAuthorizationStatus.AccessRevoked => Results.StatusCode(StatusCodes.Status403Forbidden),
                    ChatAuthorizationStatus.ChannelUnavailable => Results.NotFound(),
                    _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                };
            }
            catch (NotSupportedException)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }
        });

        app.MapPost("/api/v1/sales/bootstrap", async (
            HttpContext context,
            SalesBootstrapRequest request,
            Security.ClientCredentialRegistry credentials,
            [FromServices] RemoteSalesService sales,
            TransportMetrics metrics) =>
        {
            var identity = Authenticate(context, credentials, metrics);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await sales.BootstrapAsync(
                        identity,
                        request,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return result.Status switch
                {
                    ChatAuthorizationStatus.Authorized when result.Response is not null =>
                        Results.Json(result.Response),
                    ChatAuthorizationStatus.AccessRevoked => Results.StatusCode(StatusCodes.Status403Forbidden),
                    ChatAuthorizationStatus.ChannelUnavailable => Results.NotFound(),
                    _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                };
            }
            catch (NotSupportedException)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }
        });

        app.MapPost("/api/v1/sales/status", async (
            HttpContext context,
            SalesStatusActionRequest request,
            Security.ClientCredentialRegistry credentials,
            [FromServices] RemoteSalesActionService actions,
            TransportMetrics metrics) =>
        {
            var identity = Authenticate(context, credentials, metrics);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Json(await actions.SetStatusAsync(
                        identity,
                        request,
                        context.RequestAborted)
                    .ConfigureAwait(false));
            }
            catch (NotSupportedException)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }
        });

        app.Map("/api/v1/stream", HandleWebSocketAsync);
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        var original = context.RequestAborted;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            original, lifetime.ApplicationStopping);
        context.RequestAborted = stopping.Token;
        try
        {
            await HandleWebSocketCoreAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stop accepting/awaiting frames immediately on Generic Host shutdown.
            // A disconnected peer must not hold up Discord callback drain or exit.
            context.Abort();
        }
        finally
        {
            context.RequestAborted = original;
        }
    }

    private static async Task HandleWebSocketCoreAsync(HttpContext context)
    {
        var metrics = context.RequestServices.GetRequiredService<TransportMetrics>();
        var credentials = context.RequestServices
            .GetRequiredService<Security.ClientCredentialRegistry>();
        var identity = Authenticate(context, credentials, metrics);
        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(
                OverlayTransportProtocol.WebSocketSubprotocol,
                StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var membership = context.RequestServices.GetRequiredService<IGuildMembershipVerifier>();
        var membershipStatus = await membership
            .VerifyAsync(identity, context.RequestAborted)
            .ConfigureAwait(false);
        if (membershipStatus != GuildMembershipStatus.Member)
        {
            context.Response.StatusCode = membershipStatus == GuildMembershipStatus.NotMember
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var limiter = context.RequestServices.GetRequiredService<RemoteConnectionLimiter>();
        using var lease = limiter.TryAcquire(identity);
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(
                OverlayTransportProtocol.WebSocketSubprotocol)
            .ConfigureAwait(false);
        metrics.Increment(TransportMetric.Connections);
        try
        {
            var session = context.RequestServices.GetRequiredService<BackendWebSocketSession>();
            await session.RunAsync(socket, identity, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            metrics.Increment(TransportMetric.Disconnects);
        }
    }

    private static Security.AuthenticatedClientIdentity? Authenticate(
        HttpContext context,
        Security.ClientCredentialRegistry credentials,
        TransportMetrics metrics)
    {
        if (TransportAuthentication.HasForbiddenCredentialQuery(context.Request))
        {
            metrics.Increment(TransportMetric.AuthRejected);
            return null;
        }

        var identity = TransportAuthentication.AuthenticateBearer(context.Request, credentials);
        metrics.Increment(identity is null
            ? TransportMetric.AuthRejected
            : TransportMetric.AuthAccepted);
        return identity;
    }

    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
