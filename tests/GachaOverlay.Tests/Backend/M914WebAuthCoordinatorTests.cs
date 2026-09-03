using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M94ProductionRemoteModeTests
{
    [Fact]
    public async Task M914WebLoginStoresOnlyRemoteCredentialAndRestartDoesNotOpenBrowser()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = "100" });
        var credentials = new MemoryCredentialStore();
        var login = new M914WebClient();
        var next = new FakeRemoteClient();
        var factoryCalls = 0; var browsers = 0;
        await using (var coordinator = new RemoteChatProductionCoordinator(store, credentials, new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance,
            _ => factoryCalls++ == 0 ? login : next, openBrowser: _ => browsers++))
        {
            var running = coordinator.BeginLoginAsync();
            Assert.Null(typeof(RemoteChatSnapshot).GetProperty("PairingCode"));
            await running;
            await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
            Assert.Equal("m914-remote-only", credentials.Value); Assert.Equal(1, browsers);
            Assert.Equal(1, login.Starts); Assert.Equal(1, login.Polls); Assert.True(login.Disposed);
            Assert.Equal(0, login.Cancellations);
        }
        await using var restarted = new RemoteChatProductionCoordinator(store, credentials, new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => new M914WebClient(), openBrowser: _ => browsers++);
        restarted.Start();
        await WaitUntilAsync(() => restarted.Snapshot.Health == RemoteChatHealthState.Live);
        Assert.Equal(1, browsers);
        var settings = File.ReadAllText(Path.Combine(directory.Path, "settings.json"));
        Assert.DoesNotContain("m914-remote-only", settings); Assert.DoesNotContain(new string('c', 43), settings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M914CancellationAndDisposeStopPollingAndMultipleClicksAreDebounced(bool dispose)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault());
        var login = new M914WebClient { Result = new(1, DiscordWebAuthStatus.Pending) };
        var credentials = new MemoryCredentialStore();
        var coordinator = new RemoteChatProductionCoordinator(store, credentials, new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => login, openBrowser: _ => { });
        try
        {
            var running = coordinator.BeginLoginAsync();
            for (var i = 0; i < 20; i++) await coordinator.BeginLoginAsync();
            Assert.Equal(1, login.Starts);
            if (dispose) await coordinator.DisposeAsync(); else coordinator.CancelLogin();
            await running.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(login.Disposed); Assert.Equal(1, login.Cancellations);
            Assert.Null(credentials.Value); Assert.Equal(0, login.Polls);
        }
        finally { await coordinator.DisposeAsync(); }
    }

    [Theory]
    [InlineData(DiscordWebAuthFailure.NotMember)]
    [InlineData(DiscordWebAuthFailure.VerificationUnavailable)]
    [InlineData(DiscordWebAuthFailure.Cancelled)]
    public async Task M914FailureOffersRetryWithoutChangingExistingCredential(DiscordWebAuthFailure failure)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault());
        var login = new M914WebClient { Result = new(1, DiscordWebAuthStatus.Denied, failure) };
        var credentials = new MemoryCredentialStore("existing-credential");
        await using var coordinator = new RemoteChatProductionCoordinator(store, credentials, new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => login, openBrowser: _ => { });
        await coordinator.BeginLoginAsync();
        Assert.Equal("WebAuth" + failure, coordinator.Snapshot.Detail);
        Assert.Equal(RemoteChatHealthState.LoginRequired, coordinator.Snapshot.Health);
        Assert.Equal("existing-credential", credentials.Value); Assert.Equal(1, login.Cancellations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M914ExistingCredentialTransientAndRevokedStartupNeverAutomaticallyLaunchesLogin(bool revoked)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = "100" });
        var credentials = new MemoryCredentialStore("existing-credential");
        var clients = new List<M914WebClient>(); var browsers = 0;
        await using var coordinator = new RemoteChatProductionCoordinator(store, credentials, new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance,
            _ => { var client = new M914WebClient(fail: !revoked, revoked: revoked); clients.Add(client); return client; }, openBrowser: _ => browsers++);
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == (revoked ? RemoteChatHealthState.AccessRevoked : RemoteChatHealthState.Reconnecting));
        Assert.Equal(0, browsers); Assert.All(clients, client => Assert.Equal(0, client.Starts));
        Assert.Equal("existing-credential", credentials.Value);
    }

    [Theory]
    [InlineData("https://overlay.revo32.cloud")]
    [InlineData("http://127.0.0.1:5188")]
    public async Task M914MissingProductionWebAuthNeverFallsBackToSlashInstructions(string endpoint)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault());
        store.Update(settings => settings with { RemoteBackendBaseUrl = endpoint });
        var client = new M914WebClient { Disabled = true };
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore(), new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => client, openBrowser: _ => throw new Exception("Browser must not open"));
        await coordinator.BeginLoginAsync();
        Assert.Equal("WebAuthUnavailable", coordinator.Snapshot.Detail); Assert.Null(typeof(RemoteChatSnapshot).GetProperty("PairingCode"));
    }

    private sealed class M914WebClient(bool fail = false, bool revoked = false)
        : FakeRemoteClient(failBootstrap: fail, rejectAuthentication: revoked), ILSOverlayDiscordWebAuthClient
    {
        public int Starts, Polls, Cancellations;
        public bool Disabled;
        public DiscordWebAuthClaimResult Result = new(1, DiscordWebAuthStatus.Approved,
            AccessToken: "m914-remote-only", CredentialExpiresAt: DateTimeOffset.UtcNow.AddDays(180));
        public override Task<DiscordWebAuthStartResponse?> StartDiscordWebAuthAsync(Guid installation, CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.FromResult<DiscordWebAuthStartResponse?>(Disabled ? null : new(1, Guid.NewGuid(), new string('c', 43),
                "https://discord.com/oauth2/authorize?scope=identify", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
        public override Task<DiscordWebAuthClaimResult> GetDiscordWebAuthStatusAsync(Guid session, string claim, CancellationToken cancellationToken = default)
        { Polls++; return Task.FromResult(Result); }
        public override Task CancelDiscordWebAuthAsync(Guid session, string claim, CancellationToken cancellationToken = default)
        { Cancellations++; return Task.CompletedTask; }
    }
}
