using System.Net;

namespace LSOverlay.CoreDevBridge;

// Ordinary authorized Remote reads, never Discord REST/Gateway or a proxy for
// arbitrary endpoints. The native client logs in directly with this issuer.
internal static class ReadOnlyPolicy
{
    public const string Origin = "https://overlay.revo32.cloud";
    public static bool Allows(HttpMethod method, Uri? uri) => uri is not null &&
        uri.GetLeftPart(UriPartial.Authority) == Origin && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        ((method == HttpMethod.Get && uri.AbsolutePath is "/api/v1/bootstrap" or "/api/v1/chat/channels") ||
         (method == HttpMethod.Post && uri.AbsolutePath is "/api/v1/chat/bootstrap" or "/api/v1/sales/bootstrap"));
    public static bool IsToken(string? value) => value is { Length: 47 } && value.StartsWith("lso_",StringComparison.Ordinal) &&
        value.AsSpan(4).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
}
internal sealed class ReadOnlyHandler(HttpMessageHandler inner,BridgeTelemetry? telemetry=null) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        if (!ReadOnlyPolicy.Allows(request.Method,request.RequestUri)) throw new InvalidOperationException("Read-only upstream path rejected.");
        telemetry?.Count(request.RequestUri!.AbsolutePath switch {"/api/v1/bootstrap"=>BridgeSignal.HttpIdentity,"/api/v1/chat/channels"=>BridgeSignal.HttpCatalog,
            "/api/v1/chat/bootstrap"=>BridgeSignal.HttpChat,_=>BridgeSignal.HttpSales});
        return base.SendAsync(request,cancellationToken);
    }
}
