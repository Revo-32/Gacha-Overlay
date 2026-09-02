using System.Net;
using System.Text.Json;
using LSOverlay.Backend;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GachaOverlay.Tests.Backend;

public sealed class M913DeploymentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"LSOverlay-M913-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null)]
    [InlineData("http://+:${PORT}")]
    [InlineData("http://+:${{PORT}}")]
    [InlineData("http://+:8123")]
    [InlineData("http://0.0.0.0:8123")]
    public void RailwayUsesMountedVolumeAndOnePortAuthority(string? urls)
    {
        var env = RailwayEnvironment();
        env["ASPNETCORE_URLS"] = urls;
        var result = BackendDeploymentOptions.Resolve(env.GetValueOrDefault);
        Assert.True(result.IsRailway);
        Assert.Equal(_directory, result.DataDirectory);
        Assert.Equal(_directory, result.VolumeMountPath);
        Assert.Equal(new Uri("http://0.0.0.0:8123"), result.ListenUri);
    }

    [Theory]
    [InlineData("LSO_BACKEND_DATA_DIR")]
    [InlineData("LSO_STATE_DIRECTORY")]
    public void ExplicitDataOverrideWithinVolumeTakesPrecedence(string variable)
    {
        var env = RailwayEnvironment();
        env[variable] = Path.Combine(_directory, "nested", "credentials");
        Assert.Equal(env[variable], BackendDeploymentOptions.Resolve(env.GetValueOrDefault).DataDirectory);
    }

    [Fact]
    public void NewOverrideWinsOverLegacyLocalHelperOverride()
    {
        var env = RailwayEnvironment();
        env["LSO_BACKEND_DATA_DIR"] = Path.Combine(_directory, "new");
        env["LSO_STATE_DIRECTORY"] = Path.Combine(_directory, "legacy");
        Assert.Equal(env["LSO_BACKEND_DATA_DIR"], BackendDeploymentOptions.Resolve(env.GetValueOrDefault).DataDirectory);
    }

    [Fact]
    public void NoRailwayVariablesPreservesLocalDefaultsAndHelperOverrides()
    {
        var env = new Dictionary<string, string?>();
        var defaults = BackendDeploymentOptions.Resolve(env.GetValueOrDefault);
        Assert.False(defaults.IsRailway);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "state"), defaults.DataDirectory);
        Assert.Equal(new Uri("http://127.0.0.1:5188"), defaults.ListenUri);
        env["LSO_STATE_DIRECTORY"] = _directory;
        env["LSO_LISTEN_URL"] = "http://127.0.0.1:5123";
        var local = BackendDeploymentOptions.Resolve(env.GetValueOrDefault);
        Assert.Equal(_directory, local.DataDirectory);
        Assert.Equal(5123, local.ListenUri.Port);
        env.Remove("LSO_LISTEN_URL");
        env["ASPNETCORE_URLS"] = "https://localhost:5443";
        Assert.Equal(5443, BackendDeploymentOptions.Resolve(env.GetValueOrDefault).ListenUri.Port);
    }

    [Theory]
    [InlineData("PORT", null)]
    [InlineData("PORT", "0")]
    [InlineData("PORT", "65536")]
    [InlineData("PORT", "not-a-port")]
    [InlineData("ASPNETCORE_URLS", "http://+:5188")]
    [InlineData("ASPNETCORE_URLS", "https://+:8123")]
    [InlineData("ASPNETCORE_URLS", "http://+:8123;http://+:9123")]
    [InlineData("LSO_LISTEN_URL", "http://127.0.0.1:5188")]
    [InlineData("RAILWAY_VOLUME_MOUNT_PATH", null)]
    [InlineData("RAILWAY_VOLUME_MOUNT_PATH", "relative-directory")]
    public void InvalidRailwayConfigurationFailsWithoutFallback(string key, string? value)
    {
        var env = RailwayEnvironment();
        env[key] = value;
        Assert.ThrowsAny<ArgumentException>(() => BackendDeploymentOptions.Resolve(env.GetValueOrDefault));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void RailwayRejectsSiblingDataRootEvenWithMatchingPrefix()
    {
        var env = RailwayEnvironment();
        env["LSO_BACKEND_DATA_DIR"] = _directory + "-outside";
        Assert.ThrowsAny<ArgumentException>(() => BackendDeploymentOptions.Resolve(env.GetValueOrDefault));
        env["LSO_BACKEND_DATA_DIR"] = Path.Combine(_directory, "..", "outside");
        Assert.ThrowsAny<ArgumentException>(() => BackendDeploymentOptions.Resolve(env.GetValueOrDefault));
    }

    [Fact]
    public void PreflightChecksMountBeforeCreatingNestedDurableDirectory()
    {
        Directory.CreateDirectory(_directory);
        var nested = Path.Combine(_directory, "nested", "state");
        var config = Configuration(nested, true);
        Assert.Throws<IOException>(() => BackendStoragePreflight.Validate(config, _ => false));
        Assert.False(Directory.Exists(nested));
        BackendStoragePreflight.Validate(config, path => path == _directory);
        Assert.True(Directory.Exists(nested));
        Assert.Empty(Directory.EnumerateFiles(nested));
    }

    [Fact]
    public void PreflightRejectsMissingVolumeWithoutCreatingIt()
    {
        Assert.Throws<IOException>(() => BackendStoragePreflight.Validate(Configuration(_directory, true), _ => true));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void OrdinaryDirectoryDoesNotPassAsAnAttachedRailwayVolume()
    {
        Directory.CreateDirectory(_directory);
        Assert.Throws<IOException>(() => BackendStoragePreflight.Validate(Configuration(_directory, true)));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void DeploymentConfigurationErrorNamesTheSettingWithoutEchoingItsValue()
    {
        var env = RailwayEnvironment();
        env["LSO_DISCORD_BOT_TOKEN"] = "synthetic-test-token";
        env["LSO_DISCORD_GUILD_ID"] = "11";
        env["PORT"] = "synthetic-private-value";
        var result = BackendConfigurationLoader.Load(env.GetValueOrDefault);
        Assert.False(result.IsValid);
        Assert.Contains("PORT", result.Error);
        Assert.DoesNotContain("synthetic-private-value", result.Error);
        Assert.DoesNotContain("synthetic-test-token", result.Error);
        Assert.DoesNotContain(_directory, result.Error);
    }

    [Fact]
    public void DockerRecipeIsBackendOnlyAndUsesSecretFreeExecFormRuntime()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var docker = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var ignore = File.ReadAllText(Path.Combine(root, ".dockerignore"));
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build", docker);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime", docker);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"LSOverlay.Backend.dll\"]", docker);
        Assert.Contains("--self-contained false", docker);
        Assert.Contains("src/LSOverlay.Backend/LSOverlay.Backend.csproj", docker);
        foreach (var forbidden in new[] { "GachaOverlay.sln", "GachaOverlay.App", "ARG ", "LSO_DISCORD_", "COPY . ", "COPY . ." })
            Assert.DoesNotContain(forbidden, docker);
        foreach (var excluded in new[] { "**/bin/**", "**/obj/**", "**/.git/**", "**/.env*", "**/*.dat", "**/*.log", "**/*.zip", "**/state/**", "**/TestResults/**" })
            Assert.Contains(excluded, ignore);
        foreach (var project in new[] { "GachaOverlay.Core", "LSOverlay.Protocol", "LSOverlay.Backend" })
            Assert.Contains($"!src/{project}/**", ignore);
        Assert.DoesNotContain("!tests", ignore);
        Assert.DoesNotContain("!src/GachaOverlay.App", ignore);
    }

    [Fact]
    public void InvalidStorageFailsBeforeHostCanStartOrIssueCredentials()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocked, "synthetic fixture");
        Assert.ThrowsAny<IOException>(() => Program.CreateHost(Configuration(Path.Combine(blocked, "state"), false)));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void NestedRegistryReloadAndAtomicReplacementPreserveHashOnlyCredentials()
    {
        var config = Configuration(Path.Combine(_directory, "nested", "state"), false);
        BackendStoragePreflight.Validate(config);
        var first = new ClientCredentialRegistry(config).Issue(Guid.NewGuid(), 22, 11);
        var second = new ClientCredentialRegistry(config).Issue(Guid.NewGuid(), 33, 11);
        var primary = Path.Combine(config.StateDirectory, "client-credentials.v1.json");
        var backup = primary + ".bak";
        var reloaded = new ClientCredentialRegistry(config);
        Assert.NotNull(reloaded.Authenticate(first.AccessToken));
        Assert.NotNull(reloaded.Authenticate(second.AccessToken));
        Assert.Equal(2, Directory.GetFiles(config.StateDirectory).Length);
        foreach (var path in new[] { primary, backup })
        {
            var json = File.ReadAllText(path);
            Assert.DoesNotContain(first.AccessToken, json);
            Assert.DoesNotContain(second.AccessToken, json);
            using var document = JsonDocument.Parse(json);
        }

        File.WriteAllText(primary, "corrupt synthetic primary");
        var recovered = new ClientCredentialRegistry(config);
        Assert.False(recovered.IsFaulted);
        Assert.NotNull(recovered.Authenticate(first.AccessToken));
        // Backup is the previous committed generation, not an invented latest snapshot.
        Assert.Null(recovered.Authenticate(second.AccessToken));
        Assert.NotNull(new ClientCredentialRegistry(config).Authenticate(first.AccessToken));
    }

    [Fact]
    public void CorruptPrimaryAndBackupRefuseHostStartup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "client-credentials.v1.json"), "invalid");
        File.WriteAllText(Path.Combine(_directory, "client-credentials.v1.json.bak"), "invalid");
        Assert.Throws<IOException>(() => Program.CreateHost(Configuration(_directory, false)));
    }

    [Theory]
    [InlineData(true, "10.0.0.2", "http", "https", "198.51.100.8", 204, "https", "198.51.100.8")]
    [InlineData(true, "::ffff:10.0.0.2", "http", "https", "198.51.100.8", 204, "https", "198.51.100.8")]
    [InlineData(true, "::ffff:198.51.100.2", "http", "https", "198.51.100.8", 403, "http", "::ffff:198.51.100.2")]
    [InlineData(true, "fd00::2", "http", "https", "2001:db8::8", 204, "https", "2001:db8::8")]
    [InlineData(true, "100.64.0.2", "http", "https", "198.51.100.8", 204, "https", "198.51.100.8")]
    [InlineData(true, "198.51.100.2", "http", "https", "198.51.100.8", 403, "http", "198.51.100.2")]
    [InlineData(false, "10.0.0.2", "http", "https", "198.51.100.8", 403, "http", "10.0.0.2")]
    [InlineData(false, "198.51.100.2", "https", null, null, 204, "https", "198.51.100.2")]
    [InlineData(false, "127.0.0.1", "http", null, null, 204, "http", "127.0.0.1")]
    [InlineData(false, "::ffff:127.0.0.1", "http", null, null, 204, "http", "::ffff:127.0.0.1")]
    [InlineData(false, "198.51.100.2", "http", null, null, 403, "http", "198.51.100.2")]
    [InlineData(true, "127.0.0.1", "http", null, null, 403, "http", "127.0.0.1")]
    [InlineData(true, "10.0.0.2", "http", "http", "198.51.100.8", 403, "http", "198.51.100.8")]
    [InlineData(true, "10.0.0.2", "http", "https,http", "198.51.100.8", 403, "http", "10.0.0.2")]
    [InlineData(true, "10.0.0.2", "http", "https", null, 403, "http", "10.0.0.2")]
    [InlineData(true, "10.0.0.2", "http", "https", "not-an-address", 403, "http", "10.0.0.2")]
    public async Task RealForwardedHeadersPipelineEnforcesTrustBoundary(
        bool railway, string peer, string scheme, string? proto, string? realIp,
        int status, string expectedScheme, string expectedPeer)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(Configuration(_directory, railway)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseBackendTransportSecurity();
        app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = scheme;
        context.Request.Path = "/api/v1/stream";
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1"; // Never the rate-limit identity.
        if (proto is not null) context.Request.Headers["X-Forwarded-Proto"] = proto;
        if (realIp is not null) context.Request.Headers["X-Real-IP"] = realIp;
        await app.Build()(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(expectedScheme, context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(expectedPeer), context.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("GET", "/healthz", 204)]
    [InlineData("POST", "/healthz", 403)]
    [InlineData("GET", "/healthz/anything", 403)]
    [InlineData("GET", "/api/v1/bootstrap", 403)]
    public async Task PlaintextHealthExceptionIsRestrictedToExactGet(string method, string path, int status)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(Configuration(_directory, true)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseBackendTransportSecurity();
        app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.2");
        await app.Build()(context);
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("Ready", true, false, 200)]
    [InlineData("Ready", false, false, 503)]
    [InlineData("Ready", true, true, 503)]
    [InlineData("Starting", true, false, 503)]
    [InlineData("Disconnected", true, false, 503)]
    [InlineData("TargetGuildUnavailable", true, false, 503)]
    [InlineData("Faulted", true, false, 503)]
    public void HealthIsBoundedAndUsesReadinessWithoutSecrets(string state, bool started, bool stopping, int status)
    {
        using var lifetime = new TestLifetime(started, stopping);
        var health = new BackendConnectionHealth();
        health.Transition(Enum.Parse<BackendConnectionHealthState>(state), BackendConnectionHealthReason.None);
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime).AddSingleton(health)
            .AddSingleton(new ClientCredentialRegistry(Configuration(_directory, false)))
            .BuildServiceProvider();
        var result = BackendTransportHosting.HealthResult(services);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(status == 200 ? "{\"status\":\"ok\"}" : "{\"status\":\"unavailable\"}", json);
    }

    [Fact]
    public void HealthRejectsFaultedRegistryEvenWhenGatewayIsReady()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "client-credentials.v1.json"), "corrupt");
        using var lifetime = new TestLifetime(true, false);
        var health = new BackendConnectionHealth();
        health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime).AddSingleton(health)
            .AddSingleton(new ClientCredentialRegistry(Configuration(_directory, false))).BuildServiceProvider();
        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(BackendTransportHosting.HealthResult(services)).StatusCode);
    }

    private Dictionary<string, string?> RailwayEnvironment() => new()
    {
        ["RAILWAY_SERVICE_ID"] = "synthetic-service",
        ["RAILWAY_VOLUME_MOUNT_PATH"] = _directory,
        ["PORT"] = "8123",
    };

    private BackendConfiguration Configuration(string data, bool railway) => new(
        new BackendBotCredential("synthetic-test-token"), 11, new ulong[] { 22 },
        deployment: new BackendDeploymentOptions(railway, new Uri("http://127.0.0.1:0"), data,
            railway ? _directory : null));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        public TestLifetime(bool started, bool stopping)
        {
            if (started) _started.Cancel();
            if (stopping) _stopping.Cancel();
        }
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() { _started.Dispose(); _stopping.Dispose(); }
    }
}
