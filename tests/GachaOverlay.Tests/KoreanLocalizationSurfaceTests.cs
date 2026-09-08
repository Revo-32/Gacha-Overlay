using System.Text.Json;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Gta.Localization;

namespace GachaOverlay.Tests;

public sealed class KoreanLocalizationSurfaceTests
{
    public static IEnumerable<object[]> KoreanParticles()
    {
        foreach (var (term, consonant, rieul) in new[]
        {
            ("습격", true, false), ("사무소", false, false), ("나이트클럽", true, false),
            ("튜닝 샵", true, false), ("LSD 제조실", true, true), ("폐차장", true, false),
            ("레이스", false, false), ("임무", false, false), ("카빈", true, false), ("피스톨", true, true)
        })
        {
            yield return [term, "을(를)", consonant ? "을" : "를"];
            yield return [term, "은(는)", consonant ? "은" : "는"];
            yield return [term, "이(가)", consonant ? "이" : "가"];
            yield return [term, "과(와)", consonant ? "과" : "와"];
            yield return [term, "으로(로)", consonant && !rieul ? "으로" : "로"];
        }
    }
    [Theory]
    [MemberData(nameof(KoreanParticles))]
    [InlineData("RP", "을", "를")]
    [InlineData("GTA$", "과", "와")]
    [InlineData("HSW", "을", "를")]
    [InlineData("CEO", "이", "가")]
    [InlineData("CEO", "은", "는")]
    [InlineData("MC", "이", "가")]
    [InlineData("MC", "은", "는")]
    public void ParticlesAreDeterministic(string term, string supplied, string expected)
    {
        Assert.True(KoreanLocalizationSurface.TryParticle(term, supplied, out var actual));
        Assert.Equal(expected, actual);
        var input = new ProtectedLocalizationText(term, "[[L000_0000]]", new Dictionary<string, string> { ["[[L000_0000]]"] = term });
        Assert.True(LocalizationProtection.TryRestore(input, "[[L000_0000]]" + supplied + " 안내", out var restored, out _));
        Assert.Equal(term + expected + " 안내", restored);
    }

    [Theory]
    [InlineData("1 second", "1", "second", "1초")]
    [InlineData("30 seconds", "30", "second", "30초")]
    [InlineData("1 minute", "1", "minute", "1분")]
    [InlineData("90 minutes", "90", "minute", "90분")]
    [InlineData("1 hour", "1", "hour", "1시간")]
    [InlineData("1.5 hours", "1.5", "hour", "1.5시간")]
    [InlineData("1 day", "1", "day", "1일")]
    [InlineData("7 days", "7", "day", "7일")]
    [InlineData("1 week", "1", "week", "1주")]
    [InlineData("2 weeks", "2", "week", "2주")]
    [InlineData("1 month", "1", "month", "1개월")]
    [InlineData("3 months", "3", "month", "3개월")]
    [InlineData("1.5X", "1.5", "multiplier", "1.5배")]
    [InlineData("2X", "2", "multiplier", "2배")]
    [InlineData("3X", "3", "multiplier", "3배")]
    [InlineData("4×", "4", "multiplier", "4배")]
    public void QuantityValuesAndUnitsAreProtectedSeparately(string source, string value, string unit, string display)
    {
        var p = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect(source);
        var quantity = Assert.Single(p.Quantities).Value;
        Assert.Equal(new LocalizationQuantity(value, unit, display), quantity);
        Assert.True(LocalizationProtection.TryRestore(p, p.Text, out var restored, out _));
        Assert.Equal(display, restored);
        Assert.False(LocalizationProtection.TryRestore(p, display, out _, out _)); // Literal numbers cannot bypass tokens.
        Assert.False(LocalizationProtection.TryRestore(p, p.Text + "시간", out _, out _));
    }

    [Theory]
    [InlineData("90 minutes", "60분")]
    [InlineData("3X", "2배")]
    [InlineData("1.5X", "15배")]
    public void ChangedQuantityRejectedByActualStructuredValidator(string source, string corrupted)
    {
        var p = LocalizationValidation.Prepare(new("source", [new("field.000", "Note", source)]), GtaLocalizationGlossary.Default);
        string Response(string text) => JsonSerializer.Serialize(new { items = new[] { new { id = "field.000", textKo = text } } });
        Assert.True(LocalizationValidation.TryValidate(p, Response(p.Fields[0].Text), out var good, out _));
        Assert.Equal(KoreanLocalizationSurface.ParseQuantity(source)!.Display, good[source]);
        Assert.False(LocalizationValidation.TryValidate(p, Response(corrupted), out _, out _));
        Assert.False(LocalizationValidation.TryValidate(p, Response(p.Fields[0].Text + corrupted), out _, out _));
    }

    [Fact]
    public void KnownBadRewardProseIsCorrectedWithoutChangingFacts()
    {
        var p = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect("Earn 3X GTA$ and RP on Acid Lab Sell Missions.");
        string Key(string value) => p.Tokens.Single(t => t.Value == value).Key;
        var response = $"{Key("LSD 제조실 판매 임무")}에서 {Key("GTA$")} 및 {Key("RP")}을 {Key("3배")}로 획득할 수 있습니다.";
        Assert.True(LocalizationProtection.TryRestore(p, response, out var text, out _));
        Assert.Equal("LSD 제조실 판매 임무에서 GTA 달러와 RP를 3배로 획득할 수 있습니다.", text);
    }

    [Fact]
    public void DurationAndHeistKnownFailuresAreCorrected()
    {
        var protector = new LocalizationProtection(GtaLocalizationGlossary.Default);
        var duration = protector.Protect("90 minutes");
        Assert.True(LocalizationProtection.TryRestore(duration, duration.Text + " 동안", out var text, out _));
        Assert.Equal("90분 동안", text);
        var heist = protector.Protect("The Cayo Perico Heist");
        Assert.True(LocalizationProtection.TryRestore(heist, heist.Text + "을(를) 완료하면", out text, out _));
        Assert.Equal("카요 페리코 습격을 완료하면", text);
        var amount = protector.Protect("GTA$500,000");
        Assert.True(LocalizationProtection.TryRestore(amount, amount.Text, out text, out _));
        Assert.Equal("GTA$500,000", text);
    }

    [Theory]
    [InlineData("Grotti Turismo Omaggio")]
    [InlineData("Mansion Raid")]
    [InlineData("KnoWay Out")]
    [InlineData("\"my strange creator job\"")]
    [InlineData("\"90 minutes GTA$ 및 RP 3X 을(를)\"")]
    [InlineData("2Xtreme")]
    public void ProtectedNamesAreNotSurfaceRewritten(string name)
    {
        var p = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect(name, entityKind: LocalizationEntityKind.CreatorJobTitle);
        Assert.True(LocalizationProtection.TryRestore(p, p.Text, out var restored, out _));
        Assert.Equal(name, restored);
    }

    [Fact]
    public void UnknownPronunciationDoesNotGuessOrLeakMechanicalParticles()
    {
        var p = new LocalizationProtection(GtaLocalizationGlossary.Default).Protect("UnknownCreatorName", entityKind: LocalizationEntityKind.CreatorJobTitle);
        Assert.False(LocalizationProtection.TryRestore(p, p.Text + "을(를) 완료", out _, out var reason));
        Assert.Equal("KoreanSurface", reason);
        Assert.False(LocalizationProtection.TryRestore(p, p.Text + " 안내를(을) 확인", out _, out _));
    }

    [Fact]
    public void PromptAndSurfaceVersionsInvalidateOldMemory()
    {
        Assert.Equal("gta-ko-3.7-weekly-conditions", GeminiGtaLocalizationProvider.PromptVersion);
        Assert.Equal("ko-surface-1", KoreanLocalizationSurface.Version);
    }
}
