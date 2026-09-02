using System.Net;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using Microsoft.AspNetCore.HttpOverrides;

namespace LSOverlay.Backend.Transport;

internal static class BackendTransportHosting
{
    public static void UseBackendTransportSecurity(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<BackendConfiguration>();
        var railway = configuration.Deployment?.IsRailway == true;
        if (railway)
        {
            // The trusted boundary is the isolated, single-service Railway environment,
            // NOT possession of a header. No TCP proxy or untrusted private-network
            // services may expose this listener. See the deployment security contract.
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Forwarded-Proto") &&
                    (!Single(context, "X-Forwarded-Proto", out var proto) ||
                     proto is not ("https" or "http") ||
                     !Single(context, "X-Real-IP", out var ip) || !IPAddress.TryParse(ip, out _)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next(context).ConfigureAwait(false);
            });
            app.UseForwardedHeaders(CreateRailwayForwardingOptions());
        }

        app.Use(async (context, next) =>
        {
            // Railway's internal deployment health probe is plaintext. Only this
            // exact, data-free GET endpoint is exempt; never exempt other API routes.
            var healthProbe = HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/healthz";
            if (!healthProbe && !IsSecure(context, railway))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    internal static ForwardedHeadersOptions CreateRailwayForwardingOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
            // Railway documents X-Real-IP, not a sanitized X-Forwarded-For chain.
            // Never use the client-supplied X-Forwarded-For for rate-limit identity.
            ForwardedForHeaderName = "X-Real-IP",
            ForwardLimit = 1,
            RequireHeaderSymmetry = true,
        };
        // Keep the framework's loopback defaults. These are non-public address
        // classes, not a claim that Railway publishes fixed edge CIDRs. Trust is
        // conditional on Railway's isolated environment and exclusive edge ingress.
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("100.64.0.0"), 10));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("fc00::"), 7));
        return options;
    }

    internal static bool IsSecure(HttpContext context, bool railway) =>
        context.Request.IsHttps || (!railway && context.Connection.RemoteIpAddress is { } remote &&
            IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote));

    private static bool Single(HttpContext context, string name, out string value)
    {
        var values = context.Request.Headers[name];
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return value.Length > 0 && !value.Contains(',', StringComparison.Ordinal);
    }

    public static IResult HealthResult(IServiceProvider services)
    {
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var ready = lifetime.ApplicationStarted.IsCancellationRequested &&
            !lifetime.ApplicationStopping.IsCancellationRequested &&
            !services.GetRequiredService<ClientCredentialRegistry>().IsFaulted &&
            services.GetService<BackendConnectionHealth>()?.Current.State == BackendConnectionHealthState.Ready;
        return Results.Json(new { status = ready ? "ok" : "unavailable" },
            statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
