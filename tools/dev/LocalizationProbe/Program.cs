using System.Diagnostics;
using System.Text.Json;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Gta;
using LSOverlay.Backend.Gta.Localization;
using LSOverlay.Backend.Runtime;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Explicit opt-in local validation. This never connects to Discord/Railway and
// never modifies real application state. The API credential stays in environment.
if (args.Length != 2 || args[0] is not ("--live" or "--mock"))
{
    Console.WriteLine("사용법: --live 또는 --mock 다음에 결과 폴더를 지정하세요.");
    return 2;
}
var live = args[0] == "--live";
Console.WriteLine("GEMINI_API_KEY: " + (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) ? "미감지" : "감지됨"));
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var state = Path.Combine(output, "state");
var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "public-regression-corpus.json")));
var reference = DateTimeOffset.UtcNow;
var text = "A new GTA Online event starts on this week\nWEEKLY CHALLENGE\nComplete 5 Contact Missions to receive GTA$500,000\nBONUSES\n" +
    string.Join('\n', corpus.RootElement.GetProperty("bonus").EnumerateArray().Select(x => x.GetString())) + "\nDISCOUNTS\n" +
    string.Join('\n', corpus.RootElement.GetProperty("discount").EnumerateArray().Select(x => x.GetString())) + "\nGUN VAN INVENTORY AND DISCOUNTS\n" +
    string.Join('\n', corpus.RootElement.GetProperty("detail").EnumerateArray().Select(x => x.GetString()));
// GTA+ monthly sources are intentionally excluded by the existing classifier.
// A member-specific line in an otherwise valid weekly bulletin is not a monthly source.
var document = new CanonicalEventDocumentBuilder().Build(new(42, TrustedGtaLocalizationSourcePolicy.ChannelId,
    reference, null, text, [], [], AuthorId: TrustedGtaLocalizationSourcePolicy.AuthorId));
var input = TrustedGtaLocalizationSourcePolicy.Extract(document) ?? throw new InvalidOperationException("통제 예제 분류 실패");
using var legacyStream = typeof(GtaKoreanFormatter).Assembly.GetManifestResourceStream("GachaOverlay.Core.Gta.TranslationGlossary.ko.json")!;
using var legacyReader = new StreamReader(legacyStream);
var legacy = new GtaKoreanFormatter(glossary: GtaTranslationGlossary.Parse(legacyReader.ReadToEnd()));
var observed = new List<object>();
var accepted = true;
var providerObservations = new List<object>();
for (var cycle = 0; cycle < 2; cycle++)
{
    var configuration = new BackendConfiguration(new BackendBotCredential("synthetic-no-discord-login"), 123, [], state, new Uri("http://127.0.0.1:0"));
    using var host = LSOverlay.Backend.Program.CreateHost(configuration, services =>
    {
        services.RemoveAll<IDiscordGatewayLifecycle>();
        services.AddSingleton<IDiscordGatewayLifecycle, LocalGateway>();
        // The probe has no Discord credentials and must not run even attempted command migrations.
        foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IHostedService) &&
            s.ImplementationType?.Name == "SlashPairingRetirementWorker").ToArray()) services.Remove(descriptor);
        if (live)
        {
            services.RemoveAll<IGtaLocalizationProvider>();
            services.AddSingleton<IGtaLocalizationProvider>(_ => new ObservedProvider(providerObservations));
        }
        if (!live)
        {
            services.RemoveAll<IGtaLocalizationProvider>();
            services.AddSingleton<IGtaLocalizationProvider, MockProvider>();
        }
    });
    await host.StartAsync();
    var localization = host.Services.GetRequiredService<GtaLocalizationService>();
    var events = host.Services.GetRequiredService<GtaEventService>();
    var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient();
    var healthyBefore = (int)(await http.GetAsync(address + "/healthz")).StatusCode;
    var timer = Stopwatch.StartNew();
    events.ProcessDocument(document);
    var valid = await localization.Submit(document).WaitAsync(TimeSpan.FromSeconds(100));
    // Submit completes after SnapshotChanged publication and persistence attempt.
    var elapsed = timer.ElapsedMilliseconds;
    for (var i = 0; i < 100; i++)
    {
        events.CaptureSnapshot();
        await localization.Submit(document);
    }
    var snapshot = events.CaptureSnapshot();
    var snapshotJson = JsonSerializer.Serialize(snapshot);
    var samples = input.Items.Select(i => new
    {
        원문 = i.Text,
        기존23 = legacy.TranslateKnownTerms(i.Text),
        Gemini = localization.Find(i.Text),
        검증 = localization.Find(i.Text) is null ? "미채택" : "PASS",
        Snapshot반영 = localization.Find(i.Text) is { } translated && snapshotJson.Contains(JsonSerializer.Serialize(translated).Trim('"'), StringComparison.Ordinal)
    }).ToArray();
    var surfaceFailures = samples.Count(s => s.Gemini is null || System.Text.RegularExpressions.Regex.IsMatch(s.Gemini,
        @"을\(를\)|이\(가\)|은\(는\)|과\(와\)|으로\(로\)|\d\s*(?:seconds?|minutes?|hours?|days?|weeks?|months?)\b|\d(?:[X×])로|RP을|HSW을|CEO이|MC이"));
    accepted &= valid && surfaceFailures == 0 && samples.All(s => s.Snapshot반영);
    observed.Add(new
    {
        회차 = cycle,
        실제API = live,
        전체검증 = valid,
        표면품질실패 = surfaceFailures,
        프롬프트 = GeminiGtaLocalizationProvider.PromptVersion,
        표면규칙 = KoreanLocalizationSurface.Version,
        경과ms = elapsed,
        HealthBefore = healthyBefore,
        HealthAfter = (int)(await http.GetAsync(address + "/healthz")).StatusCode,
        계측 = localization.Diagnostics(),
        예제 = samples,
        출처 = corpus.RootElement.GetProperty("provenance").GetString()
    });
    await host.StopAsync();
    // Do not retry a live quota/network failure by restarting immediately.
    if (!valid) break;
}
var report = Path.Combine(output, "evaluation.json");
await File.WriteAllTextAsync(Path.Combine(output, "provider-observations.json"), JsonSerializer.Serialize(providerObservations,
    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(observed, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));
Console.WriteLine("검증 결과: " + report);
return accepted ? 0 : 1;

internal sealed class LocalGateway(BackendConnectionHealth health) : IDiscordGatewayLifecycle
{
    public Task StartAsync(CancellationToken token)
    {
        health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
        return Task.CompletedTask;
    }
    public Task StopAsync() => Task.CompletedTask;
}
internal sealed class MockProvider : IGtaLocalizationProvider
{
    public string Model => GeminiGtaLocalizationProvider.DefaultModel;
    public Task<LocalizationProviderResult> TranslateAsync(PublicGtaLocalizationInput input, bool repair, CancellationToken token) =>
        Task.FromResult(new LocalizationProviderResult(JsonSerializer.Serialize(new
        {
            items = input.Items.Select(i => new
            {
                id = i.Id,
                textKo = "검증 " + string.Join(" ", System.Text.RegularExpressions.Regex.Matches(i.Text, @"\[\[L\d{3}_\d{4}\]\]").Select(m => m.Value))
            })
        }), "Success", 0));
}

// Only the fixed public regression corpus passes through this local diagnostic wrapper.
internal sealed class ObservedProvider(List<object> observations) : IGtaLocalizationProvider, IDisposable
{
    private readonly GeminiGtaLocalizationProvider _inner = new(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
    public string Model => _inner.Model;
    public async Task<LocalizationProviderResult> TranslateAsync(PublicGtaLocalizationInput input, bool repair, CancellationToken token)
    {
        var result = await _inner.TranslateAsync(input, repair, token);
        observations.Add(new
        {
            교정요청 = repair,
            분류 = result.Category,
            지연ms = result.ElapsedMilliseconds,
            입력 = input,
            응답 = result.Json
        });
        return result;
    }
    public void Dispose() => _inner.Dispose();
}
