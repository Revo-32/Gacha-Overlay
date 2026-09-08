using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Gta.Localization;
using Microsoft.Extensions.Logging;

namespace GachaOverlay.Tests;

public sealed class GtaPlaceholderReliabilityTests
{
    private static PreparedLocalization Prepare() => LocalizationValidation.Prepare(new("public-fixture",
        [new("field.000", "goal", "Earn GTA$500,000"), new("field.001", "goal", "Complete three Contact Missions")]), GtaLocalizationGlossary.Default);

    private static string Mutate(PreparedLocalization prepared, string mutation)
    {
        var json = JsonNode.Parse(GtaLocalizationTests.ValidResponse(prepared.ProtectedInput))!;
        var items = json["items"]!.AsArray();
        var first = prepared.Fields[0].Tokens.Keys.First();
        var second = prepared.Fields[1].Tokens.Keys.First();
        var body = items[0]!["textKo"]!.GetValue<string>();
        switch (mutation)
        {
            case "missing": items[0]!["textKo"] = body.Replace(first, "", StringComparison.Ordinal); break;
            case "duplicate": items[0]!["textKo"] = body + " " + first; break;
            case "mutated": items[0]!["textKo"] = body.Replace(first, first[1..^1], StringComparison.Ordinal); break;
            case "character": items[0]!["textKo"] = body.Replace(first, first.Replace("L", "l", StringComparison.Ordinal), StringComparison.Ordinal); break;
            case "unknown": items[0]!["textKo"] = body + " [[L999_9999]]"; break;
            case "moved":
                items[0]!["textKo"] = body.Replace(first, second, StringComparison.Ordinal);
                items[1]!["textKo"] = items[1]!["textKo"]!.GetValue<string>().Replace(second, first, StringComparison.Ordinal);
                break;
            case "order": var original = items[0]!.DeepClone(); items[0] = items[1]!.DeepClone(); items[1] = original; break;
            case "id": items[0]!["id"] = "DO-NOT-LOG-EXTERNAL-ID"; break;
            case "numeric": items[0]!["textKo"] = body.Replace(first, "GTA$600,000", StringComparison.Ordinal); break;
            case "extraNumber": items[0]!["textKo"] = body + " 99"; break;
            case "private": items[0]!["textKo"] = "[L_API_KEY_PRIVATE_SENTINEL]"; break;
            case "scope":
                var target = prepared.Fields.Select((f, i) => (f, i)).First(p => p.f.Tokens.Values.Contains("2배") && p.f.Tokens.Values.Contains("GTA$"));
                var money = target.f.Tokens.Single(t => t.Value == "GTA$").Key;
                var multiplier = target.f.Tokens.Single(t => t.Value == "2배").Key;
                items[target.i]!["textKo"] = money + " 와 " + multiplier + ", " + string.Join(' ', target.f.Tokens.Keys.Except([money, multiplier])) + " 안내";
                break;
        }
        return json.ToJsonString();
    }

    [Theory]
    [InlineData("missing", PlaceholderFailure.Missing)]
    [InlineData("duplicate", PlaceholderFailure.Duplicate)]
    [InlineData("mutated", PlaceholderFailure.Mutated)]
    [InlineData("character", PlaceholderFailure.Mutated)]
    [InlineData("unknown", PlaceholderFailure.Unknown)]
    [InlineData("moved", PlaceholderFailure.CrossField)]
    [InlineData("order", PlaceholderFailure.Structural)]
    [InlineData("id", PlaceholderFailure.Structural)]
    [InlineData("numeric", PlaceholderFailure.Missing)]
    [InlineData("extraNumber", PlaceholderFailure.ValueValidation)]
    public void ExactViolationsRemainStrictAndFieldLocal(string mutation, PlaceholderFailure category)
    {
        var p = Prepare();
        Assert.False(LocalizationValidation.TryValidate(p, Mutate(p, mutation), out var values, out _, out var diagnostics));
        Assert.Empty(values);
        Assert.Contains(diagnostics, d => d.FieldId == "field.000" && d.Failures.Contains(category));
        if (mutation is "missing" or "duplicate" or "unknown" or "moved")
            Assert.Contains(PlaceholderFailure.CountMismatch, diagnostics[0].Failures);
        if (mutation == "moved")
        {
            Assert.Equal(2, diagnostics.Count);
            Assert.Equal(p.Fields[1].Tokens.Keys.First(), Assert.Single(diagnostics[0].MovedTokenIds));
            Assert.Equal(p.Fields[0].Tokens.Keys.First(), Assert.Single(diagnostics[1].MovedTokenIds));
        }
    }

    [Fact]
    public void RealStagingModifierScopeFailureIsRejectedAndNaturalGroupingPasses()
    {
        const string source = "2X GTA$, RP & Research Speed: Bunker Research Missions (requested via Agent 14).";
        var p = LocalizationValidation.Prepare(new("public-fixture", [new("field.000", "reward", source)]), GtaLocalizationGlossary.Default);
        string Key(string value) => p.Fields[0].Tokens.Single(t => t.Value == value).Key;
        string Response(string body) => JsonSerializer.Serialize(new { items = new[] { new { id = "field.000", textKo = body } } });
        var tail = $": {Key("벙커 연구")} 임무 (요원 {Key("14")}에게 요청)";
        var bad = $"{Key("GTA$")} 와 {Key("2배")}, {Key("RP")} 및 연구 속도" + tail;
        Assert.False(LocalizationValidation.TryValidate(p, Response(bad), out var values, out var reason, out var diagnostics));
        Assert.Empty(values);
        Assert.Equal("ModifierScope", reason);
        Assert.Equal(LocalizationValueFailure.ModifierScope, Assert.Single(diagnostics).ValueFailure);
        var wrongSide = $"{Key("벙커 연구")} 임무 (요원 {Key("14")} 요청) 및 연구 속도: {Key("GTA$")} 와 {Key("RP")} {Key("2배")}";
        Assert.False(LocalizationValidation.TryValidate(p, Response(wrongSide), out _, out reason));
        Assert.Equal("ModifierScope", reason);
        foreach (var body in new[]
        {
            $"{Key("GTA$")} 및 {Key("RP")}, 연구 속도 모두 {Key("2배")}" + tail,
            $"{Key("2배")} {Key("GTA$")} 및 {Key("RP")}, 연구 속도" + tail,
            $"{Key("벙커 연구")} 임무(요원 {Key("14")} 요청) 연구 속도, {Key("GTA$")} 및 {Key("RP")}: {Key("2배")}" // all metrics before the shared multiplier is valid too
        })
            Assert.True(LocalizationValidation.TryValidate(p, Response(body), out _, out _));
        Assert.Equal("fields-validation-2", LocalizationValidation.Version);
    }

    [Fact]
    public void RepeatedTermCarriesExactSafeRuleWithoutRelaxingValidation()
    {
        var p = LocalizationValidation.Prepare(new("public-fixture", [new("field.000", "goal", "Complete three Bunker Research Missions")]), GtaLocalizationGlossary.Default);
        var terms = p.Fields[0].Tokens;
        var bad = JsonSerializer.Serialize(new { items = new[] { new { id = "field.000", textKo = string.Join(' ', terms.Keys) + " 연구 완료" } } });
        Assert.False(LocalizationValidation.TryValidate(p, bad, out var values, out _, out var diagnostics));
        Assert.Empty(values);
        Assert.Equal(LocalizationValueFailure.RepeatedTerm, Assert.Single(diagnostics).ValueFailure);
        var feedback = LocalizationRepairFeedback.Create(diagnostics, bad);
        Assert.Contains("RepeatedTerm", feedback.Instructions, StringComparison.Ordinal);
        Assert.Contains("remove the repeated word, not the token", feedback.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("벙커 연구", JsonSerializer.Serialize(diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsNeverEchoBodiesOrResponseIdsAndAreBounded()
    {
        var p = Prepare();
        foreach (var mutation in new[] { "private", "id" })
        {
            LocalizationValidation.TryValidate(p, Mutate(p, mutation), out _, out _, out var diagnostics);
            var safe = JsonSerializer.Serialize(diagnostics, LocalizationRepairFeedback.DiagnosticJson);
            foreach (var forbidden in new[] { "PRIVATE_SENTINEL", "DO-NOT-LOG", "GTA$500,000", "Contact Missions", "textKo", "Authorization" })
                Assert.DoesNotContain(forbidden, safe, StringComparison.Ordinal);
        }
        var many = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect(string.Join(' ', Enumerable.Repeat("1", 40)));
        var owners = many.Tokens.Keys.ToDictionary(key => key, _ => 0);
        var bounded = PlaceholderDiagnostics.Inspect(0, many, "", owners)!;
        Assert.True(bounded.TokenListsTruncated);
        Assert.Equal(40, bounded.ExpectedCount);
        Assert.Equal(16, bounded.ExpectedTokenIds.Count);
        Assert.Equal(16, bounded.MissingTokenIds.Count);
    }

    [Fact]
    public void SyntaxIsDeterministicJsonSafeAndStable()
    {
        var p = Prepare();
        Assert.All(p.Fields.SelectMany(f => f.Tokens.Keys), token => Assert.Equal(13, token.Length));
        Assert.Equal(p.ProtectedInput, Prepare().ProtectedInput with { Items = p.ProtectedInput.Items });
        var roundTrip = JsonSerializer.Deserialize<PublicGtaLocalizationInput>(JsonSerializer.Serialize(p.ProtectedInput))!;
        Assert.Equal(p.ProtectedInput.Items[0].Text, roundTrip.Items[0].Text);
        Assert.Equal("gta-protect-5-weekly-conditions", LocalizationProtection.Version);
        Assert.Equal("gta-repair-3-weekly-conditions", LocalizationRepairFeedback.Version);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("moved")]
    [InlineData("scope")]
    public async Task ExistingSingleRetryReceivesExactFeedbackAndMustPassWholeValidator(string mutation)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LSO-Repair-" + Guid.NewGuid().ToString("N"));
        var logger = new CaptureLogger();
        var provider = new RepairProvider(mutation, false);
        var document = GtaLocalizationTests.Document(mutation == "scope" ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GtaLocalization", "real-business-rivalries.txt")) : null);
        try
        {
            using var service = new GtaLocalizationService(provider, folder, logger);
            await service.StartAsync(default);
            Assert.True(await service.Submit(document));
            Assert.True(await service.Submit(document));
            Assert.True(await service.Submit(document));
            await service.StopAsync(default);
            Assert.Equal(2, provider.Calls);
            Assert.NotNull(provider.Feedback);
            if (mutation == "scope") Assert.Contains(provider.Feedback.Fields, d => d.ValueFailure == LocalizationValueFailure.ModifierScope);
            else Assert.Contains(provider.Feedback.Fields, d => d.FieldId == "field.000");
            Assert.Contains("FULL required JSON", provider.Feedback.Instructions, StringComparison.Ordinal);
            Assert.NotNull(provider.Feedback.PreviousResponse);
            Assert.DoesNotContain("PreviousResponse", JsonSerializer.Serialize(provider.Feedback), StringComparison.Ordinal);
            Assert.All(logger.Messages, text => Assert.DoesNotContain("Complete 5 Contact Missions", text, StringComparison.Ordinal));
            Assert.Contains(logger.Messages, text => text.Contains("diagnostics=", StringComparison.Ordinal) && text.Contains("field.", StringComparison.Ordinal));
            Assert.Single(logger.Messages.Where(text => text.Contains("first cache hit", StringComparison.Ordinal)));
            Assert.True(File.Exists(Path.Combine(folder, "gta-localization-memory.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("missing", 2)]
    [InlineData("scope", 2)]
    [InlineData("Timeout", 1)]
    [InlineData("RateLimited", 1)]
    [InlineData("Http500", 1)]
    public async Task FailedRepairOrHttpFailureNeverLoopsOrPromotes(string failure, int calls)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LSO-Repair-Failure-" + Guid.NewGuid().ToString("N"));
        var provider = new RepairProvider(failure, true);
        var document = GtaLocalizationTests.Document(failure == "scope" ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GtaLocalization", "real-business-rivalries.txt")) : null);
        try
        {
            using var service = new GtaLocalizationService(provider, folder, new CaptureLogger());
            await service.StartAsync(default);
            Assert.False(await service.Submit(document));
            await service.StopAsync(default);
            Assert.Equal(calls, provider.Calls);
            Assert.Equal(0, service.Diagnostics().Cached);
            Assert.False(File.Exists(Path.Combine(folder, "gta-localization-memory.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ActualProviderSendsOriginalContractPreviousResponseAndStructuralFeedback()
    {
        var p = Prepare();
        var bad = Mutate(p, "duplicate");
        LocalizationValidation.TryValidate(p, bad, out _, out _, out var diagnostics);
        string? captured = null;
        using var provider = new GeminiGtaLocalizationProvider("synthetic-key-not-real", handler: new Handler(async request =>
        {
            captured = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.TooManyRequests);
        }));
        var result = await provider.TranslateAsync(p.ProtectedInput, true, default, LocalizationRepairFeedback.Create(diagnostics, bad));
        Assert.Equal("RateLimited", result.Category);
        using var json = JsonDocument.Parse(captured!);
        var contents = json.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal(bad, contents[1].GetProperty("parts")[0].GetProperty("text").GetString());
        var instruction = contents[2].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("Duplicate", instruction, StringComparison.Ordinal);
        Assert.Contains("[[L000_0000]]", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-key-not-real", captured!, StringComparison.Ordinal);
    }

    private sealed class RepairProvider(string mutation, bool failAgain) : IGtaLocalizationProvider
    {
        public string Model => GeminiGtaLocalizationProvider.DefaultModel;
        public int Calls;
        public LocalizationRepairFeedback? Feedback;
        public Task<LocalizationProviderResult> TranslateAsync(PublicGtaLocalizationInput input, bool repair, CancellationToken token, LocalizationRepairFeedback? feedback = null)
        {
            Calls++;
            if (repair) Feedback = feedback;
            else Assert.Null(feedback);
            if (mutation is "Timeout" or "RateLimited" or "Http500") return Task.FromResult(new LocalizationProviderResult(null, mutation, 0));
            if (repair && !failAgain) return Task.FromResult(new LocalizationProviderResult(GtaLocalizationTests.ValidResponse(input), "Success", 0));
            var fields = input.Items.Select(i => new ProtectedLocalizationText("public", i.Text, i.ProtectedTerms!)).ToArray();
            var p = new PreparedLocalization(input, input, fields);
            return Task.FromResult(new LocalizationProviderResult(Mutate(p, mutation), "Success", 0));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
    private sealed class CaptureLogger : ILogger<GtaLocalizationService>
    {
        public List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
