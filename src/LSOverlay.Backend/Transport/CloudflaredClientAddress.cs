using System.Net;

namespace LSOverlay.Backend.Transport;

internal static class CloudflaredClientAddress
{
    internal static bool Apply(HttpContext context, IPAddress trustedPeer)
    {
        // Inspect the actual socket peer BEFORE rewriting anything. Host-only
        // loopback ingress and trusted local processes are deployment requirements.
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !Normalize(peer).Equals(Normalize(trustedPeer))) return true;
        var headers = context.Request.Headers;
        if (!headers.ContainsKey("CF-Connecting-IP") && !headers.ContainsKey("X-Forwarded-Proto"))
            return true; // Direct local probes retain their original identity.

        var addresses = headers["CF-Connecting-IP"];
        var schemes = headers["X-Forwarded-Proto"];
        if (addresses.Count != 1 || schemes.Count != 1 || schemes[0] != "https" ||
            !TryParse(addresses[0], out var address)) return false;

        context.Connection.RemoteIpAddress = address;
        context.Request.Scheme = "https";
        // Never forward Host, X-Forwarded-For, or an arbitrary forwarding chain.
        return true;
    }

    private static bool TryParse(string? value, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.IndexOfAny([',', '%', '[', ']']) >= 0 || !IPAddress.TryParse(value, out var parsed)) return false;
        // Reject legacy numeric, octal and shortened IPv4 forms. Accept equivalent
        // IPv6 spellings, but canonicalize to one rate-limit key (including mapped IPv4).
        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && value != parsed.ToString()) return false;
        address = Normalize(parsed);
        return true;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
