using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using Microsoft.AspNetCore.WebUtilities;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M92KestrelIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M915HistoricalSlashAndWebAuthCredentialsReloadAndBootstrapWithoutReissue(bool webAuth)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M915-Credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var installation = Guid.NewGuid();
            var token = "lso_synthetic-historical-pair-issued-token";
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(directory, "client-credentials.v1.json");
            if (!webAuth)
            {
                // Frozen v1 wire shape from the retired issuer, not serialized by
                // today's registry model. No provenance field or token migration.
                File.WriteAllText(path, $$"""
                    {"schemaVersion":1,"credentials":[{
                      "clientInstallationId":"{{installation:D}}","discordUserId":456,"guildId":123,
                      "accessTokenHash":"{{CryptographicSecrets.HashHex(token)}}",
                      "createdAt":"{{now:O}}","expiresAt":"{{now.AddDays(180):O}}"
                    }]}
                    """);
            }
            else
            {
                var env = M914WebAuthTests.Environment();
                var configuration = new BackendConfiguration(new BackendBotCredential("synthetic"), 123, new ulong[] { 99 },
                    directory, webAuth: DiscordWebAuthOptions.Resolve(env.GetValueOrDefault));
                var registry = new ClientCredentialRegistry(configuration);
                var service = new DiscordWebAuthService(configuration, new FixtureIdentity(), new AlwaysMemberVerifier(), registry, new TransportMetrics());
                var start = service.Start(installation);
                var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
                Assert.Equal(DiscordWebAuthFailure.None, await service.CompleteAsync(state, "synthetic-code", null, default));
                token = Assert.IsType<string>(service.Claim(start.SessionId, start.ClaimSecret).AccessToken);
            }
            var original = File.ReadAllBytes(path);
            for (var restart = 0; restart < 2; restart++)
            {
                await using var fixture = await TransportFixture.StartAsync(stateDirectory: directory);
                await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
                Assert.False(fixture.Credentials.IsFaulted);
                Assert.Equal(new AuthenticatedClientIdentity(installation, 456, 123), fixture.Credentials.Authenticate(token));
                var bootstrap = await client.GetBootstrapAsync(token);
                Assert.Equal(456UL, bootstrap.SelfDiscordUserId);
                Assert.Equal(1, fixture.Credentials.Count);
                Assert.Equal(original, File.ReadAllBytes(path));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FixtureIdentity : IDiscordIdentityClient
    {
        public Task<ulong> IdentifyAsync(string code, string verifier, CancellationToken cancellationToken) => Task.FromResult(456UL);
    }
}
