using LSOverlay.Backend.Configuration;

namespace GachaOverlay.Tests.Backend;

public sealed class BackendConfigurationTests
{
    [Fact]
    public void Load_RequiresToken()
    {
        var result = LoadWith(guild: "123");

        Assert.False(result.IsValid);
        Assert.Contains(BackendEnvironmentVariables.BotToken, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not-a-discord-id")]
    public void Load_RequiresValidNonZeroGuildId(string? guild)
    {
        var result = LoadWith(token: "synthetic-token", guild: guild);

        Assert.False(result.IsValid);
        Assert.Contains(BackendEnvironmentVariables.GuildId, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LegacyConfigurationPreservesTwoHostSlotOrder()
    {
        var result = LoadWith(
            token: "synthetic-token",
            guild: " 987 ",
            hosts: " 3, 2 ");

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Equal((ulong)987, result.Configuration.TargetGuildId);
        Assert.Equal(new ulong[] { 3, 2 }, result.Configuration.SessionHostIds);
    }

    [Theory]
    [InlineData("1,invalid")]
    [InlineData("1;0")]
    [InlineData("-1")]
    public void Load_RejectsMalformedTrackedHostIds(string hosts)
    {
        var result = LoadWith("synthetic-token", "123", hosts);

        Assert.False(result.IsValid);
        Assert.Contains(BackendEnvironmentVariables.TrackedHostIds, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsMoreThanMaximumTrackedHosts()
    {
        var hosts = string.Join(',', Enumerable.Range(
            1,
            BackendConfiguration.MaximumSessionHosts + 1));

        var result = LoadWith("synthetic-token", "123", hosts);

        Assert.False(result.IsValid);
        Assert.Contains("at most 2", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsDuplicateLegacyHostIds()
    {
        var result = LoadWith("synthetic-token", "123", "1,1");

        Assert.False(result.IsValid);
        Assert.Contains("duplicate", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ExplicitHostSlotsOverrideLegacyConfiguration()
    {
        var result = LoadWith(
            "synthetic-token",
            "123",
            hosts: "9,8",
            host1: " 1 ",
            host2: "2");

        Assert.True(result.IsValid);
        Assert.Equal(new ulong[] { 1, 2 }, result.Configuration!.SessionHostIds);
    }

    [Theory]
    [InlineData("invalid", null)]
    [InlineData("1", "invalid")]
    [InlineData(null, "2")]
    [InlineData("1", "1")]
    public void Load_RejectsInvalidExplicitHostSlotConfiguration(
        string? host1,
        string? host2)
    {
        var result = LoadWith(
            "synthetic-token",
            "123",
            host1: host1,
            host2: host2);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CredentialAndConfigurationNeverRenderToken()
    {
        const string token = "synthetic-secret-value";
        var result = LoadWith(token, "123", "1");

        Assert.NotNull(result.Configuration);
        Assert.DoesNotContain(token, result.Configuration.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Configuration.Credential.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", result.Configuration.Credential.ToString());
    }

    private static BackendConfigurationLoadResult LoadWith(
        string? token = null,
        string? guild = null,
        string? hosts = null,
        string? host1 = null,
        string? host2 = null)
    {
        var values = new Dictionary<string, string?>
        {
            [BackendEnvironmentVariables.BotToken] = token,
            [BackendEnvironmentVariables.GuildId] = guild,
            [BackendEnvironmentVariables.TrackedHostIds] = hosts,
            [BackendEnvironmentVariables.SessionHost1Id] = host1,
            [BackendEnvironmentVariables.SessionHost2Id] = host2,
        };
        return BackendConfigurationLoader.Load(name => values[name]);
    }
}
