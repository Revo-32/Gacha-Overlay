using System.Text.Json;
using GachaOverlay.Infrastructure.Logging;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed class M92ProtocolAndSecurityTests
{
    [Fact]
    public void ProtocolVersionOne_RoundTripsWithExplicitEventDiscriminator()
    {
        var payload = new HostPresenceSnapshot(
            1,
            HostPresenceState.GtaOnline,
            11,
            32,
            DateTimeOffset.UnixEpoch);
        var value = new ProtocolEventEnvelope(
            1,
            "generation",
            7,
            OverlayTransportProtocol.HostPresenceChanged,
            payload);

        var json = JsonSerializer.Serialize(value, OverlayProtocolJson.Options);
        var roundTrip = JsonSerializer.Deserialize<ProtocolEventEnvelope>(
            json,
            OverlayProtocolJson.Options);

        Assert.Equal(value, roundTrip);
        Assert.Contains("\"eventType\":\"host_presence_changed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Assembly", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedProtocolVersion_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => OverlayProtocolJson.EnsureVersion(2));
    }

    [Fact]
    public void HostPresenceContract_HasNoSecretOrRawDiscordFields()
    {
        var names = typeof(HostPresenceSnapshot).GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("DiscordUserId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("RichGame", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://example.test", true)]
    [InlineData("wss://example.test", true)]
    [InlineData("http://127.0.0.1:5188", true)]
    [InlineData("ws://127.0.0.1:5188", true)]
    [InlineData("http://localhost:5188", true)]
    [InlineData("ws://localhost:5188", true)]
    [InlineData("http://public.example", false)]
    [InlineData("ws://public.example", false)]
    public void EndpointPolicy_EnforcesTlsOutsideLoopback(string value, bool expected)
    {
        Assert.Equal(expected, TransportEndpointSecurity.IsAllowed(new Uri(value)));
    }

    [Fact]
    public void RemoteClient_RejectsPublicPlaintextEndpoint()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LSOverlayRemoteClient(new Uri("http://public.example")));
    }

    [Theory]
    [InlineData("accessToken=lso_secret")]
    [InlineData("pairingClaimSecret=claim_secret")]
    [InlineData("Authorization: Bearer token_secret")]
    [InlineData("Authorization: LSOPairing claim_secret")]
    public void Redactor_ProtectsM92CredentialsAndRemainsIdempotent(string input)
    {
        var once = SensitiveDataRedactor.Sanitize(input);
        var twice = SensitiveDataRedactor.Sanitize(once);

        Assert.Contains(SensitiveDataRedactor.Replacement, once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
        Assert.DoesNotContain("lso_secret", once, StringComparison.Ordinal);
        Assert.DoesNotContain("claim_secret", once, StringComparison.Ordinal);
        Assert.DoesNotContain("token_secret", once, StringComparison.Ordinal);
    }
}
