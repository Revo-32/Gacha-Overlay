using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Gta.Localization;
using LSOverlay.Backend.Gta;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Runtime;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests;

public sealed class GtaLocalizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LSO-Localization-Test-" + Guid.NewGuid().ToString("N"));
    internal const string Bulletin = """
        A new GTA Online event starts on SEP 3-9
        WEEKLY CHALLENGE
        Complete 5 Contact Missions to receive GTA$500,000
        BONUSES
        Earn 2X GTA$ and RP on Community Series this week.
        DISCOUNTS
        30% OFF Service Carbine
        GUN VAN INVENTORY AND DISCOUNTS
        Pistol Mk II and Combat MG Mk II
        """;
    internal static CanonicalEventDocument Document(string? text = null, ulong? channel = null, ulong? author = null,
        IReadOnlyList<GtaEventForwardInput>? forwards = null) => new CanonicalEventDocumentBuilder().Build(new(
            42, channel ?? TrustedGtaLocalizationSourcePolicy.ChannelId, DateTimeOffset.Parse("2026-09-08T03:00:00Z"), null,
            text ?? Bulletin, [], forwards ?? [], "GTA Series Videos", null, author ?? TrustedGtaLocalizationSourcePolicy.AuthorId));
    private GtaLocalizationService Service(IGtaLocalizationProvider provider) => new(provider, _directory, NullLogger<GtaLocalizationService>.Instance);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    public static IEnumerable<object[]> GlossaryEntries() => GtaLocalizationGlossary.Default.Entries.Select(e => new object[] { e.Source, e.Ko, e.Policy });
    [Theory]
    [MemberData(nameof(GlossaryEntries))]
    public void EveryApprovedEntryEnforcesItsPolicy(string source, string ko, string policy)
    {
        var match = GtaLocalizationGlossary.Default.Match(source, 0);
        if (policy == "contextual") return;
        Assert.NotNull(match);
        Assert.Equal(source.Length, match!.Value.Length);
        Assert.Equal(ko, match.Value.Output);
    }
    [Theory]
    [InlineData("id")]
    [InlineData("source")]
    [InlineData("alias")]
    [InlineData("policy")]
    [InlineData("ko")]
    [InlineData("preserve")]
    [InlineData("category")]
    [InlineData("placeholder")]
    public void InvalidGlossaryRejected(string mutation)
    {
        var node = JsonNode.Parse(GtaLocalizationGlossary.ReadResource("gta-online-ko-glossary.json"))!;
        var entries = node["entries"]!.AsArray();
        switch (mutation)
        {
            case "id": entries[1]!["id"] = entries[0]!["id"]!.GetValue<string>(); break;
            case "source": entries[1]!["source"] = entries[0]!["source"]!.GetValue<string>(); break;
            case "alias": entries[1]!["aliases"] = new JsonArray(entries[0]!["source"]!.GetValue<string>()); break;
            case "policy": entries[0]!["policy"] = "anything"; break;
            case "ko": entries[0]!["ko"] = ""; break;
            case "preserve": entries[0]!["policy"] = "preserve"; break;
            case "category": entries[0]!["category"] = "vehicle_model"; break;
            case "placeholder": entries[0]!["source"] = "[[L000_0000]]"; break;
        }
        Assert.Throws<InvalidDataException>(() => GtaLocalizationGlossary.Parse(node.ToJsonString()));
    }
    [Fact]
    public void LongestBoundaryAliasesAndProperNames()
    {
        var glossary = GtaLocalizationGlossary.Default;
        Assert.Equal("클러킹 벨 농장 기습", glossary.Match("The Cluckin' Bell Farm Raid", 0)!.Value.Output);
        Assert.Null(glossary.Match("Freecrawler", 0));
        Assert.Equal("플리카 습격", glossary.Match("플리카 작업", 0)!.Value.Output);
        foreach (var name in new[] { "Bravado Banshee GTS", "Grotti Turismo Omaggio", "KnoWay Out", "Mansion Raid" })
        {
            var prepared = new LocalizationProtection(glossary).Protect("Get " + name + " for free");
            Assert.Contains(name, prepared.Tokens.Values);
        }
        Assert.Equal("스페셜 패키지", new GtaKoreanFormatter().TranslateKnownTerms("Special Cargo"));
        Assert.Equal("금주의 도전", new GtaKoreanFormatter().TranslateKnownTerms("Weekly Challenge"));
    }
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void ExactSourceBoundary(bool channel, bool author, bool allowed)
    {
        var doc = Document(channel: channel ? TrustedGtaLocalizationSourcePolicy.ChannelId : 123,
            author: author ? TrustedGtaLocalizationSourcePolicy.AuthorId : 456);
        Assert.Equal(allowed, TrustedGtaLocalizationSourcePolicy.Extract(doc) is not null);
    }
    [Fact]
    public void NestedContentNeverInheritsTrustAndMetadataNeverLeavesDto()
    {
        var input = TrustedGtaLocalizationSourcePolicy.Extract(Document(forwards: [new("NESTED-PRIVATE-SENTINEL", [])]))!;
        var json = JsonSerializer.Serialize(input);
        foreach (var banned in new[] { "NESTED-PRIVATE-SENTINEL", "1417898156187713577", "1417898538385539085", "GTA Series Videos", "Author", "Guild", "Channel", "MessageId", "OAuth", "Username", "Nickname" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        var onlyForward = Document("", forwards: [new(Bulletin, [])]);
        Assert.Null(TrustedGtaLocalizationSourcePolicy.Extract(onlyForward));
        Assert.Null(TrustedGtaLocalizationSourcePolicy.Extract(Document(Bulletin + "\n<@123456789012345678>")));
    }
    [Fact]
    public void FullOwnHashTracksBodyAndEmbedFieldsButNotMetadataOrForward()
    {
        var original = Document();
        var hash = TrustedGtaLocalizationSourcePolicy.Extract(original)!.SourceRevision;
        Assert.NotEqual(hash, TrustedGtaLocalizationSourcePolicy.Extract(Document(Bulletin + "\nNew note"))!.SourceRevision);
        Assert.Equal(hash, TrustedGtaLocalizationSourcePolicy.Extract(original with { SourceMessageId = 999, ReceivedAt = DateTimeOffset.UtcNow })!.SourceRevision);
        Assert.Equal(hash, TrustedGtaLocalizationSourcePolicy.Extract(Document(forwards: [new("private", [])]))!.SourceRevision);
        foreach (var embed in new[] { new GtaEventEmbedInput("Title", null, []), new GtaEventEmbedInput(null, "Description", []),
            new GtaEventEmbedInput(null, null, [new("Name", "Value")]) })
        {
            var input = new GtaEventSourceInput(42, TrustedGtaLocalizationSourcePolicy.ChannelId, original.ReceivedAt, null, Bulletin,
                [embed], [], AuthorId: TrustedGtaLocalizationSourcePolicy.AuthorId);
            Assert.NotEqual(hash, TrustedGtaLocalizationSourcePolicy.Extract(new CanonicalEventDocumentBuilder().Build(input))!.SourceRevision);
        }
    }
    internal static string ValidResponse(PublicGtaLocalizationInput input) => JsonSerializer.Serialize(new
    {
        items = input.Items.Select(i => new { id = i.Id, textKo = "안내 " + string.Join(" ", Regex.Matches(i.Text, @"\[\[L\d{3}_\d{4}\]\]").Select(m => m.Value)) })
    });
    [Theory]
    [InlineData("2X")]
    [InlineData("30%")]
    [InlineData("GTA$500,000")]
    [InlineData("RP")]
    [InlineData("Mk II")]
    [InlineData("Mansion Raid")]
    [InlineData("September 10")]
    [InlineData("5")]
    [InlineData("1.5X")]
    [InlineData("15:00")]
    [InlineData("90 minutes")]
    public void ProtectedFactsCannotBeAltered(string atom)
    {
        var p = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect("Get " + atom);
        Assert.True(LocalizationProtection.TryRestore(p, "획득 " + string.Join(" ", p.Tokens.Keys), out _, out _));
        Assert.False(LocalizationProtection.TryRestore(p, string.Join(" ", p.Tokens.Keys), out _, out _));
        var bad = string.Join(" ", p.Tokens.Keys.Skip(1)) + " 변경 3X 20%";
        Assert.False(LocalizationProtection.TryRestore(p, bad, out _, out _));
        Assert.False(LocalizationProtection.TryRestore(p, string.Join(" ", p.Tokens.Keys) + p.Tokens.Keys.First(), out _, out _));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("id")]
    [InlineData("markdown")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("duplicateProperty")]
    [InlineData("crossItem")]
    public void AdversarialStructuredResponsesRejected(string mutation)
    {
        var input = TrustedGtaLocalizationSourcePolicy.Extract(Document())!;
        var prepared = LocalizationValidation.Prepare(input, GtaLocalizationGlossary.Default);
        var good = ValidResponse(prepared.ProtectedInput);
        Assert.True(LocalizationValidation.TryValidate(prepared, good, out _, out _));
        var node = JsonNode.Parse(good)!;
        var rows = node["items"]!.AsArray();
        switch (mutation)
        {
            case "missing": rows.RemoveAt(0); break;
            case "extra": rows.Add(rows[0]!.DeepClone()); break;
            case "id": rows[0]!["id"] = "unknown"; break;
            case "empty": rows[0]!["textKo"] = ""; break;
            case "crossItem": rows[0]!["textKo"] = rows[1]!["textKo"]!.GetValue<string>(); break;
        }
        var bad = mutation switch
        {
            "markdown" => "```json\n" + good + "\n```",
            "malformed" => "{",
            "duplicateProperty" => good.Replace("\"items\":", "\"items\":[],\"items\":", StringComparison.Ordinal),
            _ => node.ToJsonString()
        };
        Assert.False(LocalizationValidation.TryValidate(prepared, bad, out _, out _));
    }
    [Fact]
    public async Task CoalescesPersistsRestartsAndEditsWithoutPerClientCalls()
    {
        var provider = new FakeProvider();
        using (var service = Service(provider))
        {
            var pending = Enumerable.Range(0, 100).Select(_ => service.Submit(Document())).ToArray();
            Assert.Equal(1, service.Diagnostics().InFlight);
            await service.StartAsync(default);
            Assert.All(await Task.WhenAll(pending), Assert.True);
            Assert.Equal(1, provider.Calls);
            Assert.True(await service.Submit(Document()));
            Assert.Equal(1, provider.Calls);
            Assert.True(await service.Submit(Document(Bulletin.Replace("30%", "25%", StringComparison.Ordinal))));
            Assert.Equal(2, provider.Calls);
            Assert.Equal(0, service.Diagnostics().InFlight);
            await service.StopAsync(default);
        }
        using var restored = Service(provider);
        await restored.StartAsync(default);
        Assert.True(await restored.Submit(Document()));
        Assert.Equal(2, provider.Calls);
        Assert.NotNull(restored.Find("30% OFF Service Carbine"));
        await restored.StopAsync(default);
    }
    [Theory]
    [InlineData("RateLimited", 1)]
    [InlineData("Timeout", 1)]
    [InlineData("MissingCredential", 1)]
    [InlineData("Invalid", 2)]
    [InlineData("ThrowSecret", 1)]
    public async Task FailurePreservesLastGoodSuppressesStormAndDoesNotLeak(string failure, int expected)
    {
        var provider = new FakeProvider();
        var log = new CaptureLogger();
        using var service = new GtaLocalizationService(provider, _directory, log);
        await service.StartAsync(default);
        Assert.True(await service.Submit(Document()));
        var good = service.Find("30% OFF Service Carbine");
        provider.Failure = failure;
        Assert.False(await service.Submit(Document(Bulletin.Replace("30%", "25%", StringComparison.Ordinal))));
        Assert.Equal(1 + expected, provider.Calls);
        Assert.Equal(good, service.Find("30% OFF Service Carbine"));
        Assert.Null(service.Find("25% OFF Service Carbine"));
        for (var i = 0; i < 100; i++) Assert.False(await service.Submit(Document(Bulletin.Replace("30%", "25%", StringComparison.Ordinal))));
        Assert.Equal(1 + expected, provider.Calls);
        Assert.DoesNotContain("SYNTHETIC-SECRET", string.Join('\n', log.Messages));
        Assert.DoesNotContain("SYNTHETIC-SECRET", File.ReadAllText(Path.Combine(_directory, "gta-localization-memory.json")));
        await service.StopAsync(default);
    }
    [Fact]
    public async Task BoundedQueueAndStopCleanPendingTasks()
    {
        using var service = Service(new FakeProvider());
        var pending = Enumerable.Range(0, 20).Select(i => service.Submit(Document(Bulletin + "\n" + i))).ToArray();
        Assert.InRange(service.Diagnostics().InFlight, 1, 8);
        await service.StartAsync(default);
        await service.StopAsync(default);
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, service.Diagnostics().InFlight);
    }
    [Fact]
    public async Task ManualOverrideHasPriorityAndValidatesProtectedFacts()
    {
        var input = TrustedGtaLocalizationSourcePolicy.Extract(Document())!;
        var prepared = LocalizationValidation.Prepare(input, GtaLocalizationGlossary.Default);
        var entries = input.Items.Select((i, n) => new
        {
            sourceHash = GtaLocalizationGlossary.Digest(i.Text),
            field = i.Type,
            protectedTextKo = "검수 " + string.Join(" ", prepared.Fields[n].Tokens.Keys),
            reason = "synthetic test"
        }).ToArray();
        var overrides = new LocalizationOverrides(JsonSerializer.Serialize(new { version = "test", entries }));
        using var service = new GtaLocalizationService(new FakeProvider(), _directory, NullLogger<GtaLocalizationService>.Instance, overrides: overrides);
        await service.StartAsync(default);
        Assert.True(await service.Submit(Document()));
        Assert.StartsWith("검수", service.Find(input.Items[0].Text));
        Assert.Equal(0, service.Diagnostics().Requests);
        await service.StopAsync(default);
    }
    [Theory]
    [InlineData("Timeout")]
    [InlineData("Invalid")]
    [InlineData("MissingCredential")]
    public async Task RealLocalBackendKeepsHealthAndSnapshotAcrossLocalizationFailures(string failure)
    {
        var provider = new FakeProvider();
        var config = new BackendConfiguration(new BackendBotCredential("synthetic-never-login"), 123, [], _directory, new Uri("http://127.0.0.1:0"));
        using var host = LSOverlay.Backend.Program.CreateHost(config, services =>
        {
            services.RemoveAll<IDiscordGatewayLifecycle>();
            services.AddSingleton<IDiscordGatewayLifecycle, LocalGateway>();
            services.RemoveAll<IGtaLocalizationProvider>();
            services.AddSingleton<IGtaLocalizationProvider>(provider);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTime());
        });
        await host.StartAsync();
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var events = host.Services.GetRequiredService<GtaEventService>();
            var localization = host.Services.GetRequiredService<GtaLocalizationService>();
            events.ProcessDocument(Document());
            Assert.True(await localization.Submit(Document()));
            Assert.NotNull(events.CaptureSnapshot().CurrentWeek);
            Assert.Contains(events.CaptureSnapshot().CurrentWeek!.Discounts, i => i.DisplayTextKo.Contains("안내", StringComparison.Ordinal));
            provider.Failure = failure;
            var edit = Document(Bulletin.Replace("30%", "25%", StringComparison.Ordinal));
            events.ProcessDocument(edit);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(address + "/healthz")).StatusCode);
            Assert.False(await localization.Submit(edit));
            var snapshot = events.CaptureSnapshot();
            Assert.NotNull(snapshot.CurrentWeek);
            Assert.Contains(snapshot.CurrentWeek!.Discounts, i => i.DisplayTextKo.Contains("25%", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.CurrentWeek.Discounts, i => i.DisplayTextKo.Contains("30%", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(address + "/healthz")).StatusCode);
        }
        finally { await host.StopAsync(); }
    }
    [Fact]
    public async Task PersistentBackupAndModelIdentityAreValidated()
    {
        var provider = new FakeProvider();
        using (var service = Service(provider))
        {
            await service.StartAsync(default);
            Assert.True(await service.Submit(Document()));
            Assert.True(await service.Submit(Document(Bulletin.Replace("30%", "25%", StringComparison.Ordinal))));
            await service.StopAsync(default);
        }
        File.WriteAllText(Path.Combine(_directory, "gta-localization-memory.json"), "{corrupt");
        using (var recovered = Service(provider))
        {
            Assert.NotNull(recovered.Find("30% OFF Service Carbine"));
            Assert.Equal(1, recovered.Diagnostics().Cached);
        }
        provider.Model = "gemini-other-flash";
        using var invalidated = Service(provider);
        Assert.Equal(0, invalidated.Diagnostics().Cached);
        Assert.Null(invalidated.Find("30% OFF Service Carbine"));
    }
    [Fact]
    public async Task TranslationMemoryIsBoundedAcrossManyUniqueRevisions()
    {
        using var service = Service(new FakeProvider());
        await service.StartAsync(default);
        for (var i = 0; i < 70; i++) Assert.True(await service.Submit(Document(Bulletin + "\nRevision " + i)));
        Assert.InRange(service.Diagnostics().Cached, 1, GtaLocalizationService.MaximumEntries);
        Assert.InRange(new FileInfo(Path.Combine(_directory, "gta-localization-memory.json")).Length, 1, 4 * 1024 * 1024);
        await service.StopAsync(default);
    }
    [Theory]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "Http500")]
    [InlineData(401, "Http401")]
    public async Task ProviderSuppressesErrorBodiesAndCredentials(int status, string expected)
    {
        using var provider = new GeminiGtaLocalizationProvider("SYNTHETIC-SECRET", handler: new Handler(_ =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SYNTHETIC-SECRET") }));
        var input = LocalizationValidation.Prepare(TrustedGtaLocalizationSourcePolicy.Extract(Document())!, GtaLocalizationGlossary.Default).ProtectedInput;
        var result = await provider.TranslateAsync(input, false, default);
        Assert.Equal(expected, result.Category);
        Assert.DoesNotContain("SYNTHETIC-SECRET", JsonSerializer.Serialize(provider));
        Assert.DoesNotContain("SYNTHETIC-SECRET", JsonSerializer.Serialize(result));
    }
    [Fact]
    public async Task ProviderUsesClosedStructuredRequestAndNoMetadata()
    {
        string? body = null;
        using var provider = new GeminiGtaLocalizationProvider("SYNTHETIC-SECRET", handler: new AsyncHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            Assert.Null(request.RequestUri!.Query.Length > 0 ? request.RequestUri.Query : null);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}") };
        }));
        var input = LocalizationValidation.Prepare(TrustedGtaLocalizationSourcePolicy.Extract(Document())!, GtaLocalizationGlossary.Default).ProtectedInput;
        Assert.Equal("Success", (await provider.TranslateAsync(input, false, default)).Category);
        Assert.Contains("responseJsonSchema", body);
        Assert.DoesNotContain("SYNTHETIC-SECRET", body);
        Assert.DoesNotContain("141789", body);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
    private sealed class FakeProvider : IGtaLocalizationProvider
    {
        public string Model { get; set; } = GeminiGtaLocalizationProvider.DefaultModel;
        public int Calls;
        public string? Failure;
        public Task<LocalizationProviderResult> TranslateAsync(PublicGtaLocalizationInput input, bool repair, CancellationToken token)
        {
            Calls++;
            if (Failure == "ThrowSecret") throw new HttpRequestException("SYNTHETIC-SECRET");
            return Task.FromResult(Failure is null ? new(ValidResponse(input), "Success", 1) :
                new LocalizationProviderResult(Failure == "Invalid" ? "{}" : null, Failure, 1));
        }
    }
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-08T03:00:00Z");
    }
    private sealed class LocalGateway(BackendConnectionHealth health) : IDiscordGatewayLifecycle
    {
        public Task StartAsync(CancellationToken token)
        {
            health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
    }
    private sealed class CaptureLogger : ILogger<GtaLocalizationService>
    {
        public List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
