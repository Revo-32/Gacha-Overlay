using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using LSOverlay.Backend.Security;

namespace LSOverlay.Backend.Transport;

internal static class TransportAuthentication
{
    private static readonly HashSet<string> ForbiddenQueryKeys = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "claim",
        "pairing_claim_secret",
    };

    public static bool HasForbiddenCredentialQuery(HttpRequest request) =>
        request.Query.Keys.Any(ForbiddenQueryKeys.Contains);

    public static AuthenticatedClientIdentity? AuthenticateBearer(
        HttpRequest request,
        ClientCredentialRegistry registry)
    {
        if (!TryReadScheme(request.Headers.Authorization, "Bearer", out var token))
        {
            return null;
        }

        return registry.Authenticate(token);
    }

    public static bool TryReadPairingClaim(HttpRequest request, out string secret) =>
        TryReadScheme(request.Headers.Authorization, "LSOPairing", out secret);

    private static bool TryReadScheme(
        StringValues values,
        string expectedScheme,
        out string credential)
    {
        credential = string.Empty;
        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(' ');
        if (separator <= 0 ||
            !value[..separator].Equals(expectedScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        credential = value[(separator + 1)..].Trim();
        return credential.Length > 0 && !credential.Any(char.IsWhiteSpace);
    }
}
