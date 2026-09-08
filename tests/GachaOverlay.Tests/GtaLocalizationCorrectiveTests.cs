using System.Text.Json;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Gta.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests;

public sealed class GtaLocalizationCorrectiveTests
{
    private static LocalizationProtection Protector => new(GtaLocalizationGlossary.Default);

    [Theory]
    [InlineData("worth")]
    [InlineData("kinds")]
    [InlineData("across")]
    [InlineData("businesses")]
    [InlineData("complete")]
    [InlineData("selling")]
    [InlineData("Unknown Title Cased Prose")]
    [InlineData("\"ordinary quoted prose\"")]
    public void UnknownProseIsNotAProtectedEntity(string source)
    {
        var p = Protector.Protect(source);
        Assert.Empty(p.Tokens);
        Assert.Equal(source, p.Text);
    }

    [Theory]
    [InlineData("one", "1")]
    [InlineData("two", "2")]
    [InlineData("three", "3")]
    [InlineData("four", "4")]
    [InlineData("five", "5")]
    [InlineData("six", "6")]
    [InlineData("seven", "7")]
    [InlineData("eight", "8")]
    [InlineData("nine", "9")]
    [InlineData("ten", "10")]
    [InlineData("Four weeks", "4주")]
    public void EnglishCountsRemainImmutableFacts(string source, string display)
    {
        var p = Protector.Protect(source);
        Assert.Equal(display, Assert.Single(p.Tokens).Value);
        Assert.True(LocalizationProtection.TryRestore(p, p.Text, out var restored, out _));
        Assert.Equal(display, restored);
    }

    [Fact]
    public void CountCannotSwallowLongestApprovedActivity()
    {
        var p = Protector.Protect("Complete three Bunker Research Missions");
        Assert.Equal(["3", "벙커 연구"], p.Tokens.Values);
        Assert.StartsWith("Complete ", p.Text);
        Assert.EndsWith(" Missions", p.Text);
        Assert.True(LocalizationProtection.TryRestore(p, $"{p.Tokens.Keys.Last()} 임무를 {p.Tokens.Keys.First()}회 완료하세요.", out var ko, out _));
        Assert.Equal("벙커 연구 임무를 3회 완료하세요.", ko);
        Assert.False(LocalizationProtection.TryRestore(p, $"{p.Tokens.Keys.Last()} 연구 임무를 {p.Tokens.Keys.First()}회 완료하세요.", out _, out var reason));
        Assert.Equal("RepeatedTerm", reason);
    }

    [Theory]
    [InlineData("Service Carbine", "서비스 카빈")]
    [InlineData("Cocaine Lockup", "코카인 제조 아지트")]
    [InlineData("The Cayo Perico Heist", "카요 페리코 습격")]
    [InlineData("Grotti Turismo Omaggio", "Grotti Turismo Omaggio")]
    [InlineData("Bravado Banshee GTS", "Bravado Banshee GTS")]
    [InlineData("KnoWay Out", "KnoWay Out")]
    [InlineData("Mansion Raid", "Mansion Raid")]
    public void PositiveEntitiesRemainProtected(string source, string expected)
    {
        var p = Protector.Protect("Get " + source + " for free");
        Assert.Contains(expected, p.Tokens.Values);
        Assert.StartsWith("Get ", p.Text);
        Assert.Contains(" for ", p.Text);
    }

    [Fact]
    public void CreatorTitleRequiresExplicitSemanticEvidence()
    {
        const string title = "\"my strange lowercase job\"";
        Assert.DoesNotContain(title, Protector.Protect(title).Tokens.Values);
        Assert.Contains(title, Protector.Protect(title, entityKind: LocalizationEntityKind.CreatorJobTitle).Tokens.Values);
        Assert.Contains(title, Protector.Protect("Play the Community Series job named " + title).Tokens.Values);
    }

    [Fact]
    public void NamedRewardAndDepotOwnersNeedExplicitEntityContext()
    {
        const string name = "Yeti x LS Customs Tracksuit";
        Assert.DoesNotContain(name, Protector.Protect(name).Tokens.Values);
        Assert.Contains(name, Protector.Protect("Sell product to receive GTA$1,000,000 + the " + name + ".").Tokens.Values);
        Assert.Contains("Bobcat Security & Gruppe Sechs", Protector.Protect("Safeguard Deliveries (Bobcat Security & Gruppe Sechs depots).").Tokens.Values);
        Assert.DoesNotContain("all businesses", Protector.Protect("Deliveries (all businesses depots)").Tokens.Values);
        Assert.DoesNotContain("all kinds", Protector.Protect("selling all kinds of product").Tokens.Values);
    }

    [Theory]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(4096)]
    public void FormerCanonicalBlockThresholdNoLongerCutsText(int length)
    {
        var source = "Weekly Challenge\n" + new string('x', length - 17);
        var document = GtaLocalizationTests.Document(source);
        Assert.Equal(source, document.OwnCanonicalText);
        Assert.True(document.OwnInputIntegrity.IsComplete);
        Assert.Equal(source.Length, document.OwnInputIntegrity.OriginalLength);
    }

    [Fact]
    public void RealDiscordFixtureProvesFirstCutAndCompleteSemanticInput()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GtaLocalization", "real-business-rivalries.txt"));
        var normalized = GtaEventTextNormalizer.Normalize(raw);
        Assert.EndsWith("Bobcat Security & Gruppe Sechs de", normalized[..2048]);
        var doc = GtaLocalizationTests.Document(raw);
        Assert.True(doc.OwnInputIntegrity.IsComplete);
        Assert.Equal(normalized, doc.OwnCanonicalText);
        var input = TrustedGtaLocalizationSourcePolicy.Extract(doc)!;
        Assert.True(input.IsComplete);
        var field = Assert.Single(input.Items.Where(i => i.Text.Contains("Bobcat Security", StringComparison.Ordinal)));
        Assert.EndsWith("Bobcat Security & Gruppe Sechs depots).", field.Text);
        var p = LocalizationValidation.Prepare(input, GtaLocalizationGlossary.Default);
        Assert.DoesNotContain(p.Fields.SelectMany(f => f.Tokens.Values), t => t is "worth" or "kinds" or "businesses across" or "three Bunker Research");
        Assert.False(LocalizationValidation.TryValidate(p, JsonSerializer.Serialize(new
        {
            items = p.ProtectedInput.Items.Select(i => new { id = i.Id, textKo = i.Text })
        }), out _, out _));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(513)]
    public void UiFieldCapsDoNotTruncateSemanticTranslationInput(int length)
    {
        var line = "Sell " + new string('x', length - 5);
        var doc = GtaLocalizationTests.Document(GtaLocalizationTests.Bulletin + "\n" + line);
        var input = TrustedGtaLocalizationSourcePolicy.Extract(doc)!;
        Assert.Contains(input.Items, i => i.Text == line);
    }

    [Fact]
    public void KnownIncompleteOwnSourceIsRejectedButForwardLimitsDoNotContaminateOwnTrust()
    {
        var doc = GtaLocalizationTests.Document(GtaLocalizationTests.Bulletin + new string('x', 16384));
        Assert.False(doc.OwnInputIntegrity.IsComplete);
        Assert.Equal("CanonicalLengthLimit", doc.OwnInputIntegrity.TruncationReason);
        Assert.Null(TrustedGtaLocalizationSourcePolicy.Extract(doc));
        var valid = TrustedGtaLocalizationSourcePolicy.Extract(GtaLocalizationTests.Document())!;
        Assert.Throws<InvalidDataException>(() => LocalizationValidation.Prepare(valid with { IsComplete = false }, GtaLocalizationGlossary.Default));
        var prepared = LocalizationValidation.Prepare(valid, GtaLocalizationGlossary.Default);
        Assert.False(LocalizationValidation.TryValidate(prepared with { Input = valid with { IsComplete = false } },
            GtaLocalizationTests.ValidResponse(prepared.ProtectedInput), out _, out _));
        Assert.NotNull(TrustedGtaLocalizationSourcePolicy.Extract(GtaLocalizationTests.Document(forwards: [new(new string('x', 17000), [])])));
    }

    [Theory]
    [InlineData("Gun Van")]
    [InlineData("30% OFF Agency")]
    [InlineData("Community Mission Series")]
    public void CompleteShortFieldsDoNotRequireSentencePunctuation(string source)
    {
        var input = new PublicGtaLocalizationInput("public-fixture", [new("field.000", "Note", source)]);
        var prepared = LocalizationValidation.Prepare(input, GtaLocalizationGlossary.Default);
        Assert.True(LocalizationValidation.TryValidate(prepared, GtaLocalizationTests.ValidResponse(prepared.ProtectedInput), out _, out _));
    }

    [Fact]
    public async Task RetiredPolicyIsPreservedButNeverReused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSO-Protection-Version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = TrustedGtaLocalizationSourcePolicy.Extract(GtaLocalizationTests.Document())!;
            var prepared = LocalizationValidation.Prepare(input, GtaLocalizationGlossary.Default);
            var path = Path.Combine(directory, "gta-localization-memory.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { Identity = new string('A', 64), Input = input, Response = GtaLocalizationTests.ValidResponse(prepared.ProtectedInput) } }));
            using var service = new GtaLocalizationService(new Provider(), directory, NullLogger<GtaLocalizationService>.Instance);
            Assert.Null(service.Find(input.Items[0].Text));
            await service.StartAsync(default);
            Assert.True(await service.Submit(GtaLocalizationTests.Document()));
            Assert.Equal(1, service.Diagnostics().Requests);
            Assert.True(await service.Submit(GtaLocalizationTests.Document()));
            Assert.Equal(1, service.Diagnostics().Requests);
            await service.StopAsync(default);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, saved.RootElement.GetArrayLength());
            Assert.Contains(saved.RootElement.EnumerateArray(), e => e.GetProperty("identity").GetString() == new string('A', 64));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Provider : IGtaLocalizationProvider
    {
        public string Model => GeminiGtaLocalizationProvider.DefaultModel;
        public Task<LocalizationProviderResult> TranslateAsync(PublicGtaLocalizationInput input, bool retry, CancellationToken cancellationToken, LocalizationRepairFeedback? feedback = null) =>
            Task.FromResult(new LocalizationProviderResult(GtaLocalizationTests.ValidResponse(input), "Success", 0));
    }
}
