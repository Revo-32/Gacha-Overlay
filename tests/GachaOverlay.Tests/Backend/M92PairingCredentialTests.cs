using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Pairing;
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
        var codes = Enumerable.Range(0, 128)
            .Select(_ => CryptographicSecrets.CreateUserCode())
            .ToArray();
        var claims = Enumerable.Range(0, 32)
            .Select(_ => CryptographicSecrets.CreateClaimSecret())
            .ToArray();
        var tokens = Enumerable.Range(0, 32)
            .Select(_ => CryptographicSecrets.CreateAccessToken())
            .ToArray();

        Assert.Equal(128, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code));
        Assert.Equal(32, claims.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(32, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(claims, claim => Assert.True(ConvertFromBase64Url(claim).Length >= 32));
        Assert.All(tokens, token => Assert.True(ConvertFromBase64Url(token[4..]).Length >= 32));
        Assert.DoesNotContain(tokens, token => claims.Contains(token, StringComparer.Ordinal));
    }

    [Fact]
    public void Pairing_RequiresMachineClaimAndIssuesTokenOnlyOnce()
    {
        var registry = Registry();
        var pairing = new PairingService(registry, 123, () => _now);
        var created = pairing.Create(Guid.NewGuid());

        Assert.Equal(PairingState.Pending, pairing.Claim(
            created.PairingId,
            created.PairingClaimSecret).State);
        Assert.Throws<UnauthorizedAccessException>(() => pairing.Claim(created.PairingId, "wrong"));
        Assert.Equal(PairingApprovalResult.Approved,
            pairing.Approve(123, 456, false, created.UserCode.ToLowerInvariant()));
        var issued = pairing.Claim(created.PairingId, created.PairingClaimSecret);

        Assert.Equal(PairingState.Approved, issued.State);
        Assert.NotNull(issued.Credential);
        Assert.Equal(PairingState.Consumed,
            pairing.Claim(created.PairingId, created.PairingClaimSecret).State);
    }

    [Fact]
    public void Pairing_RejectsWrongGuildBotUnknownExpiredAndOtherApprover()
    {
        var now = _now;
        var pairing = new PairingService(Registry(() => now), 123, () => now);
        var created = pairing.Create(Guid.NewGuid());

        Assert.Equal(PairingApprovalResult.InvalidGuild,
            pairing.Approve(999, 456, false, created.UserCode));
        Assert.Equal(PairingApprovalResult.InvalidCaller,
            pairing.Approve(123, 456, true, created.UserCode));
        Assert.Equal(PairingApprovalResult.UnknownCode,
            pairing.Approve(123, 456, false, "0000-0000"));
        Assert.Equal(PairingApprovalResult.Approved,
            pairing.Approve(123, 456, false, created.UserCode));
        Assert.Equal(PairingApprovalResult.ApprovedByAnotherUser,
            pairing.Approve(123, 789, false, created.UserCode));

        now = now.Add(PairingService.PairingLifetime).AddTicks(1);
        Assert.Equal(PairingApprovalResult.Expired,
            pairing.Approve(123, 456, false, created.UserCode));
    }

    [Fact]
    public void PairingStore_IsBounded()
    {
        var pairing = new PairingService(Registry(), 123, () => _now);
        for (var index = 0; index < PairingService.MaximumActivePairings; index++)
        {
            pairing.Create(Guid.NewGuid());
        }

        Assert.Equal(PairingService.MaximumActivePairings, pairing.Count);
        Assert.Throws<InvalidOperationException>(() => pairing.Create(Guid.NewGuid()));
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
