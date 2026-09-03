using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed class M92PairingCredentialTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"LSOverlay-M92-Tests-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SecretGenerators_UseExpectedEntropyAndIndependentValues()
    {
        var claims = Enumerable.Range(0, 32)
            .Select(_ => CryptographicSecrets.CreateClaimSecret())
            .ToArray();
        var tokens = Enumerable.Range(0, 32)
            .Select(_ => CryptographicSecrets.CreateAccessToken())
            .ToArray();

        Assert.Equal(32, claims.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(32, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(claims, claim => Assert.True(ConvertFromBase64Url(claim).Length >= 32));
        Assert.All(tokens, token => Assert.True(ConvertFromBase64Url(token[4..]).Length >= 32));
        Assert.DoesNotContain(tokens, token => claims.Contains(token, StringComparer.Ordinal));
    }

    [Fact]
    public void CredentialRegistry_PersistsHashOnlyReloadsAndExpires()
    {
        var now = _now;
        var registry = Registry(() => now);
        var installation = Guid.NewGuid();
        var issued = registry.Issue(installation, 456, 123);
        var path = Path.Combine(_directory, "client-credentials.v1.json");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain(issued.AccessToken, text, StringComparison.Ordinal);
        Assert.Contains(CryptographicSecrets.HashHex(issued.AccessToken), text, StringComparison.Ordinal);
        var reloaded = Registry(() => now);
        Assert.Equal(new AuthenticatedClientIdentity(installation, 456, 123),
            reloaded.Authenticate(issued.AccessToken));

        now = issued.ExpiresAt;
        Assert.Null(reloaded.Authenticate(issued.AccessToken));
    }

    [Fact]
    public void CredentialRegistry_ReplacesSameInstallationAndAllowsSameUserOnTwoInstallations()
    {
        var registry = Registry();
        var firstInstallation = Guid.NewGuid();
        var first = registry.Issue(firstInstallation, 456, 123);
        var replacement = registry.Issue(firstInstallation, 456, 123);
        var second = registry.Issue(Guid.NewGuid(), 456, 123);

        Assert.Null(registry.Authenticate(first.AccessToken));
        Assert.NotNull(registry.Authenticate(replacement.AccessToken));
        Assert.NotNull(registry.Authenticate(second.AccessToken));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void CredentialRegistry_RejectsCredentialFromDifferentConfiguredGuild()
    {
        var registry = Registry();
        var issued = registry.Issue(Guid.NewGuid(), 456, 999);
        var targetGuildRegistry = new ClientCredentialRegistry(
            _directory,
            () => _now,
            expectedGuildId: 123);

        Assert.Null(targetGuildRegistry.Authenticate(issued.AccessToken));
    }

    [Fact]
    public void CredentialRegistry_RecoversBackupAndFailsClosedWhenBothCopiesCorrupt()
    {
        var registry = Registry();
        var issued = registry.Issue(Guid.NewGuid(), 456, 123);
        var primary = Path.Combine(_directory, "client-credentials.v1.json");
        var backup = primary + ".bak";
        File.WriteAllText(primary, "not-json");

        var recovered = Registry();
        Assert.False(recovered.IsFaulted);
        Assert.NotNull(recovered.Authenticate(issued.AccessToken));

        File.WriteAllText(primary, "bad-primary");
        File.WriteAllText(backup, "bad-backup");
        var failedClosed = Registry();
        Assert.True(failedClosed.IsFaulted);
        Assert.Null(failedClosed.Authenticate(issued.AccessToken));
        Assert.Throws<InvalidOperationException>(() =>
            failedClosed.Issue(Guid.NewGuid(), 456, 123));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ClientCredentialRegistry Registry(Func<DateTimeOffset>? clock = null) =>
        new(_directory, clock ?? (() => _now));

    private static byte[] ConvertFromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
