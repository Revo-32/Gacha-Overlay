using System.Net;
using System.Reflection;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.PublicWeb;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests.Backend;

public sealed class M101PublicServiceTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static readonly PublicReadiness Ready = new(true, false, BackendConnectionHealthState.Ready, true, true, true, true);

    [Fact]
    public async Task ActualSdkUsesCustomActivityAndExactStateWithoutLoginOrNetwork()
    {
        using var client = new DiscordSocketClient(DiscordGatewayPolicy.CreateSocketConfiguration());
        await client.SetCustomStatusAsync(BotCustomStatus.Text);
        Assert.Equal("LS Overlay - 정상 가동 중", BotCustomStatus.Text);
        Assert.Equal(ActivityType.CustomStatus, client.Activity.Type);
        Assert.Equal(BotCustomStatus.Text, Assert.IsType<CustomStatusGame>(client.Activity).State);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayReadyReappliesCosmeticStatusAndFailureDoesNotFaultLifecycle(bool fail)
    {
        using var client = new DiscordSocketClient(DiscordGatewayPolicy.CreateSocketConfiguration());
        var calls = new List<string>(); var warnings = 0;
        var status = new BotCustomStatus(text =>
        {
            calls.Add(text);
            return fail ? Task.FromException(new InvalidOperationException("private-secret-do-not-log")) : Task.CompletedTask;
        }, () => warnings++);
        var health = new BackendConnectionHealth();
        var adapter = new DiscordGatewayAdapter(client,
            new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>()),
            new TargetGuildFilter(123), new BackendEventJournal(1), new BackendMetrics(), health,
            new TrackedHostPresenceStore(Array.Empty<ulong>()), new GtaPresenceNormalizer(),
            NullLogger<DiscordGatewayAdapter>.Instance, customStatus: status);
        var ready = typeof(DiscordGatewayAdapter).GetMethod("OnReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)ready.Invoke(adapter, null)!;
        await (Task)ready.Invoke(adapter, null)!;
        Assert.Equal(new[] { BotCustomStatus.Text, BotCustomStatus.Text }, calls);
        Assert.Equal(fail ? 2 : 0, warnings);
        Assert.False(health.HasFaulted);
        Assert.Equal(BackendConnectionHealthState.TargetGuildUnavailable, health.Current.State);
        // Health transitions do not drive cosmetic presence or retry it.
        health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
        for (var i = 0; i < 10; i++) _ = PublicStatusService.Map(Ready, DateTimeOffset.UtcNow);
        Assert.Equal(2, calls.Count);
        await adapter.StopAsync();
        await (Task)ready.Invoke(adapter, null)!;
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task StalledCosmeticSendTimesOutWithoutAutomaticRetry()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var warnings = 0;
        var status = new BotCustomStatus(_ => { calls++; return pending.Task; }, () => warnings++);
        await status.ApplyAfterReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); Assert.Equal(1, warnings);
        pending.SetResult();
    }

    [Fact]
    public void OnlyReadyCallsCosmeticStatusAndPermissionsAreUnchanged()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src/LSOverlay.Backend/Discord/DiscordGatewayAdapter.cs"));
        Assert.Equal(1, source.Split("_customStatus.ApplyAfterReadyAsync()", StringSplitOptions.None).Length - 1);
        var ready = source[source.IndexOf("private Task OnReadyAsync()", StringComparison.Ordinal)..source.IndexOf("private void ProcessReady()", StringComparison.Ordinal)];
        Assert.Contains("_customStatus.ApplyAfterReadyAsync()", ready);
        foreach (var section in new[] { "OnMessageReceivedAsync", "OnReactionAddedAsync", "OnReactionRemovedAsync" })
        {
            var start = source.IndexOf("private Task " + section, StringComparison.Ordinal);
            var end = source.IndexOf("private ", start + 10, StringComparison.Ordinal);
            Assert.DoesNotContain("_customStatus", source[start..end]);
        }
        Assert.Equal(GatewayIntents.None, DiscordGatewayPolicy.RequiredIntents & GatewayIntents.GuildMembers);
        var cosmetic = File.ReadAllText(Path.Combine(Root, "src/LSOverlay.Backend/Discord/BotCustomStatus.cs"));
        Assert.DoesNotContain("PeriodicTimer", cosmetic);
        Assert.DoesNotContain("SetGameAsync", cosmetic);
        Assert.DoesNotContain("GuildPermission", cosmetic);
    }

    [Theory]
    [InlineData("ready", PublicStatusState.Operational)]
    [InlineData("connecting", PublicStatusState.Degraded)]
    [InlineData("disconnected", PublicStatusState.Degraded)]
    [InlineData("faulted", PublicStatusState.Unavailable)]
    [InlineData("guildMissing", PublicStatusState.Unavailable)]
    [InlineData("authMissing", PublicStatusState.Unavailable)]
    [InlineData("storageFault", PublicStatusState.Unavailable)]
    [InlineData("remoteMissing", PublicStatusState.Unavailable)]
    [InlineData("capacity", PublicStatusState.Degraded)]
    [InlineData("authCapacity", PublicStatusState.Degraded)]
    [InlineData("stopping", PublicStatusState.Unavailable)]
    [InlineData("starting", PublicStatusState.Unavailable)]
    public void StateMappingIsConservative(string scenario, object expectedValue)
    {
        var expected = (PublicStatusState)expectedValue;
        var input = scenario switch
        {
            "connecting" => Ready with { Gateway = BackendConnectionHealthState.Connecting },
            "disconnected" => Ready with { Gateway = BackendConnectionHealthState.Disconnected },
            "faulted" => Ready with { Gateway = BackendConnectionHealthState.Faulted },
            "guildMissing" => Ready with { Gateway = BackendConnectionHealthState.TargetGuildUnavailable },
            "authMissing" => Ready with { AuthenticationConfigured = false },
            "storageFault" => Ready with { CredentialStorageAvailable = false },
            "remoteMissing" => Ready with { RemoteHostAvailable = false },
            "capacity" => Ready with { RemoteCapacityAvailable = false },
            "authCapacity" => Ready with { AuthenticationCapacityAvailable = false },
            "stopping" => Ready with { Stopping = true },
            "starting" => Ready with { Started = false, Gateway = BackendConnectionHealthState.Starting },
            _ => Ready,
        };
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(9));
        var snapshot = PublicStatusService.Map(input, now);
        Assert.Equal(expected, snapshot.Overall);
        Assert.Equal(TimeSpan.Zero, snapshot.UpdatedAt.Offset);
        Assert.Equal(now.ToUniversalTime(), snapshot.UpdatedAt);
        if (scenario is "connecting" or "disconnected")
        {
            Assert.Equal(PublicStatusState.Operational, snapshot.Services.Backend);
            Assert.Equal(PublicStatusState.Degraded, snapshot.Services.Authentication);
            Assert.Equal(PublicStatusState.Degraded, snapshot.Services.Remote);
        }
    }

    [Theory]
    [InlineData(PublicStatusState.Operational)]
    [InlineData(PublicStatusState.Degraded)]
    [InlineData(PublicStatusState.Maintenance)]
    [InlineData(PublicStatusState.Unavailable)]
    [InlineData(PublicStatusState.Unknown)]
    public void AggregationCannotTurnAnUnknownOrFailingComponentGreen(object rawValue)
    {
        var value = (PublicStatusState)rawValue;
        Assert.Equal(value, PublicStatusService.Aggregate(PublicStatusState.Operational, value));
        Assert.Equal(PublicStatusState.Unavailable, PublicStatusService.Aggregate(value, PublicStatusState.Unavailable));
        Assert.Equal(PublicStatusState.Unknown, PublicStatusService.Aggregate());
    }

    [Fact]
    public void PublicJsonHasExactClosedShapeAndNoInternalState()
    {
        var json = JsonSerializer.Serialize(PublicStatusService.Map(Ready, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "overall", "schemaVersion", "services", "updatedAt" }, document.RootElement.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(new[] { "authentication", "backend", "discord", "remote" }, document.RootElement.GetProperty("services").EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal("operational", document.RootElement.GetProperty("overall").GetString());
        foreach (var field in new[] { "guildId", "userId", "channelId", "token", "secret", "credential", "installationId", "messageContent", "stackTrace", "exception" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionCompositionCanCaptureLocalReadinessWithoutOpeningDiscord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M101-Composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = LSOverlay.Backend.Program.CreateHost(new BackendConfiguration(
                new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>(), directory,
                webAuth: DiscordWebAuthOptions.Resolve(M914WebAuthTests.Environment().GetValueOrDefault)));
            var snapshot = host.Services.GetRequiredService<PublicStatusService>().Capture();
            Assert.Equal(PublicStatusState.Unknown, snapshot.Services.Backend);
            Assert.Equal(PublicStatusState.Unavailable, snapshot.Services.Remote);
            var client = Assert.Single(host.Services.GetServices<DiscordSocketClient>());
            Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AuthenticationCapacityObservationDoesNotSweepSessionsOrCallDiscord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M101-Capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var config = new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>(), directory,
                webAuth: DiscordWebAuthOptions.Resolve(M914WebAuthTests.Environment().GetValueOrDefault));
            var identity = new M914WebAuthTests.IdentityFake();
            var service = new DiscordWebAuthService(config, identity, new M914WebAuthTests.MemberFake(),
                new ClientCredentialRegistry(config), new TransportMetrics(), () => now);
            for (var i = 0; i < DiscordWebAuthService.MaximumSessions; i++) service.Start(Guid.NewGuid());
            Assert.False(service.HasCapacity);
            now = now.AddMinutes(6);
            Assert.True(service.HasCapacity);
            var entries = (System.Collections.IDictionary)typeof(DiscordWebAuthService)
                .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Assert.Equal(128, entries.Count);
            Assert.Equal(0, identity.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("/privacy")]
    [InlineData("/terms")]
    public async Task LegalPagesArePublicUtf8AndNeverInterpolateLoadedSecrets(string path)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"ko\"", html); Assert.Contains("LS Overlay", html);
        Assert.Contains(PublicServicePages.UpdatedDate, html);
        Assert.Contains("최종 업데이트", html);
        Assert.Equal("revo.32.39.41@gmail.com", PublicServicePages.ContactEmail);
        Assert.Equal("mailto:revo.32.39.41@gmail.com", PublicServicePages.ContactUrl);
        Assert.Contains($"<a href=\"{PublicServicePages.ContactUrl}\">{PublicServicePages.ContactEmail}</a>", html);
        foreach (var purpose in new[] { "LS Overlay 이용 문의", "개인정보 관련 문의", "데이터 삭제 요청" }) Assert.Contains(purpose, html);
        foreach (var placeholder in new[] { "공개 문의처가 아직 등록되지 않았습니다", "등록된 후", "제출 준비가 완료되지 않았습니다", "PUBLIC CONTACT REQUIRED BEFORE DISCORD PORTAL SUBMISSION" })
            Assert.DoesNotContain(placeholder, html);
        foreach (var secret in fixture.PrivateValues) Assert.DoesNotContain(secret, html);
        foreach (var asset in Directory.GetFiles(Path.Combine(Root, "web/status/public")))
            foreach (var secret in fixture.PrivateValues) Assert.DoesNotContain(secret, File.ReadAllText(asset));
        foreach (var url in new[] { PublicServicePages.PrivacyUrl, PublicServicePages.TermsUrl, PublicServicePages.StatusOrigin }) Assert.Contains(url, html);
        Assert.Equal(PublicServicePages.LegalCsp, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain("<script", html); Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void BothLegalPagesReuseTheSingleOperatorConfirmedContactSource()
    {
        Assert.Equal("mailto:" + PublicServicePages.ContactEmail, PublicServicePages.ContactUrl);
        var source = File.ReadAllText(Path.Combine(Root, "src/LSOverlay.Backend/PublicWeb/PublicServicePages.cs"));
        Assert.Equal(1, source.Split(PublicServicePages.ContactEmail, StringSplitOptions.None).Length - 1);
        foreach (var page in new[] { "privacy", "terms" })
        {
            var body = File.ReadAllText(Path.Combine(Root, "src/LSOverlay.Backend/PublicWeb/Content", page + ".html"));
            Assert.DoesNotContain(PublicServicePages.ContactEmail, body);
            Assert.DoesNotContain("mailto:", body);
            var html = PublicServicePages.Render(page, page);
            Assert.Equal(1, html.Split($"href=\"{PublicServicePages.ContactUrl}\"", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void ContactReadinessArtifactsMatchTheVerifiedPublicContact()
    {
        Assert.Equal("PUBLIC CONTACT VERIFIED", PublicServicePages.ContactReadiness);
        using var validation = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs/architecture/M10.1-validation.json")));
        Assert.Equal(PublicServicePages.ContactReadiness, validation.RootElement.GetProperty("contactReadiness").GetString());
        foreach (var path in new[] { "docs/compliance/M10.1-data-processing-audit.md", "docs/architecture/M10.1-public-service-compliance-report.md", "docs/deployment/status-revo32-cloud.md" })
        {
            var document = File.ReadAllText(Path.Combine(Root, path));
            Assert.Contains(PublicServicePages.ContactEmail, document);
            Assert.Contains(PublicServicePages.ContactReadiness, document);
            Assert.DoesNotContain("PUBLIC CONTACT REQUIRED BEFORE DISCORD PORTAL SUBMISSION", document);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://status.revo32.cloud", true)]
    [InlineData("https://arbitrary.example", false)]
    [InlineData("https://status.revo32.cloud.evil.test", false)]
    [InlineData("http://status.revo32.cloud", false)]
    public async Task PublicApiIsNoStoreWithNarrowNonCredentialedCors(string? origin, bool allowed)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status/public");
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await fixture.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        var json = await response.Content.ReadAsStringAsync();
        foreach (var secret in fixture.PrivateValues) Assert.DoesNotContain(secret, json);
        Assert.Contains("\"schemaVersion\":1", json);
        fixture.Snapshot = PublicStatusService.Map(Ready with { CredentialStorageAvailable = false }, DateTimeOffset.UtcNow);
        using var failed = await fixture.Http.GetAsync("/status/public");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode); // Snapshot delivery, not /healthz.
        Assert.Contains("\"overall\":\"unavailable\"", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CorsPreflightIsScopedAndHealthAndOAuthStaySeparate()
    {
        await using var fixture = await Fixture.Start();
        foreach (var path in new[] { "/status/public", "/auth/discord/callback", "/healthz" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", PublicServicePages.StatusOrigin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await fixture.Http.SendAsync(request);
            Assert.Equal(path == "/status/public", response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Options, "/status/public");
        deniedRequest.Headers.Add("Origin", "https://arbitrary.example");
        deniedRequest.Headers.Add("Access-Control-Request-Method", "GET");
        using var denied = await fixture.Http.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var health = await fixture.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());
        using var callback = await fixture.Http.GetAsync("/auth/discord/callback?state=bad&code=private-code");
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.DoesNotContain(PublicServicePages.LegalCsp, callback.Headers.GetValues("Content-Security-Policy"));
        Assert.False(callback.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AssetsUseOriginalLogoBytesAndSafeTypes()
    {
        await using var fixture = await Fixture.Start();
        using var logo = await fixture.Http.GetAsync("/public/assets/ls-overlay-logo.png");
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Root, "assets/branding/LS_Overlay_logo.png")), await logo.Content.ReadAsByteArrayAsync());
        using var css = await fixture.Http.GetAsync("/public/assets/site.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Contains("focus-visible", await css.Content.ReadAsStringAsync());
    }

    [Fact]
    public void StaticSiteHasNoFrameworkStorageTrackingOrProductionMockSwitch()
    {
        var directory = Path.Combine(Root, "web/status/public");
        var html = File.ReadAllText(Path.Combine(directory, "index.html"));
        var js = File.ReadAllText(Path.Combine(directory, "status.js"));
        var css = File.ReadAllText(Path.Combine(directory, "styles.css"));
        Assert.Contains("lang=\"ko\"", html); Assert.Contains("name=\"viewport\"", html);
        Assert.Contains(PublicServicePages.PrivacyUrl, html); Assert.Contains(PublicServicePages.TermsUrl, html);
        Assert.Contains("https://overlay.revo32.cloud/status/public", js);
        Assert.Contains("REFRESH_MS = 60_000", js); Assert.Contains("cache: \"no-store\"", js);
        Assert.Contains("credentials: \"omit\"", js); Assert.Contains("상태 확인 불가", js);
        Assert.Contains("서비스 상태를 불러올 수 없습니다.", js); Assert.Contains("AbortController", js);
        Assert.Contains("pagehide", js); Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("@media", css); Assert.Contains("focus-visible", css);
        foreach (var forbidden in new[] { "localStorage", "sessionStorage", "document.cookie", "googletagmanager", "fonts.googleapis", "analytics", "WebSocket", "location.search", "URLSearchParams", "localhost", "127.0.0.1" })
            Assert.DoesNotContain(forbidden, html + js + css, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Root, "web/status/package.json")));
        Assert.Contains("connect-src https://overlay.revo32.cloud", html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M101-Test-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        public HttpClient Http = null!;
        public PublicStatusSnapshot Snapshot = PublicStatusService.Map(Ready, DateTimeOffset.UtcNow);
        public string[] PrivateValues = Array.Empty<string>();

        public static async Task<Fixture> Start()
        {
            var fixture = new Fixture();
            var options = M914WebAuthTests.Environment();
            var config = new BackendConfiguration(new BackendBotCredential("private-bot-M101-sentinel"), 123456789123456789,
                new ulong[] { 223456789123456789 }, fixture._directory,
                webAuth: DiscordWebAuthOptions.Resolve(options.GetValueOrDefault));
            var registry = new ClientCredentialRegistry(config);
            var installation = Guid.NewGuid();
            var issued = registry.Issue(installation, 323456789123456789, config.TargetGuildId);
            fixture.PrivateValues = new[] { config.Credential.RevealForDiscordLogin(), options["LSO_DISCORD_OAUTH_CLIENT_SECRET"]!,
                config.TargetGuildId.ToString(), config.SessionHostIds[0].ToString(), "323456789123456789", installation.ToString(), issued.AccessToken, registry.Snapshot()[0].AccessTokenHash, fixture._directory };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var health = new BackendConnectionHealth();
            health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
            builder.Services.AddSingleton(config); builder.Services.AddSingleton(registry); builder.Services.AddSingleton(health);
            builder.Services.AddSingleton(new DiscordWebAuthService(config, new M914WebAuthTests.IdentityFake(), new M914WebAuthTests.MemberFake(), registry, new TransportMetrics()));
            builder.Services.AddSingleton(new WebAuthRateLimiter());
            fixture._app = builder.Build();
            fixture._app.UseBackendTransportSecurity(); fixture._app.MapDiscordWebAuth();
            fixture._app.MapGet("/healthz", () => BackendTransportHosting.HealthResult(fixture._app.Services));
            fixture._app.MapPublicServicePages(() => fixture.Snapshot);
            await fixture._app.StartAsync();
            fixture.Http = new HttpClient { BaseAddress = new Uri(fixture._app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
