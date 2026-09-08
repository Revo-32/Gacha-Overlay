using GachaOverlay.Core.Gta;

namespace GachaOverlay.Tests;

public sealed class Translation23Tests
{
    [Theory]
    [InlineData("30% OFF Bunker", "30% 할인 벙커")]
    [InlineData("2X GTA$ and RP", "2배 GTA$ + RP")]
    [InlineData("Complete 5 Bunker missions", "벙커 임무 5회 완료")]
    [InlineData("Win 3 HSW races", "HSW 레이스 3회 승리")]
    [InlineData("HSW Time Trial", "HSW 타임 트라이얼")]
    [InlineData("FREE Crawler", "무료 Crawler")]
    [InlineData("Freecrawler", "Freecrawler")]
    [InlineData("Unknown Vehicle 300R", "Unknown Vehicle 300R")]
    [InlineData("Weekly Challenge: 5 missions", "금주의 도전: 5 missions")]
    [InlineData("Completion Reward: GTA$100,000", "완료 보상: GTA$100,000")]
    public void KnownTermsAndProtectedNames(string source, string expected)
    {
        Assert.Equal(expected, new GtaKoreanFormatter().TranslateKnownTerms(source));
        Assert.True(GtaTranslationIntegrity.IsValid(source, expected));
    }

    [Theory]
    [InlineData("2X GTA$ 100,000 RP 30%", "3배 GTA$ 100,000 RP 30%")]
    [InlineData("30% OFF", "20% 할인")]
    [InlineData("Complete 5 missions", "임무 완료")]
    [InlineData("2026-09-08 15:00", "2026-09-09 15:00")]
    [InlineData("2026-09-08 15:00", "2026-08-09 15:00")]
    [InlineData("GTA$100 RP200", "GTA$200 RP100")]
    [InlineData("HSW RP GTA$100", "100")]
    [InlineData("GTA$100", "")]
    [InlineData("GTA$100", "� GTA$100")]
    public void InvalidTranslationUsesLastGoodOrOriginal(string source, string bad)
    {
        Assert.False(GtaTranslationIntegrity.IsValid(source, bad));
        Assert.Equal(source, GtaTranslationIntegrity.Select(source, bad));
        var good = new GtaKoreanFormatter().TranslateKnownTerms(source);
        Assert.Equal(good, GtaTranslationIntegrity.Select(source, bad, good));
    }

    [Fact]
    public void StructuredGlossaryPrecedenceAndCacheVersion()
    {
        var official = GtaEventVocabulary.Glossary.Single(x => x.CanonicalId == "adversary_mode");
        Assert.Equal(GtaTranslationSource.RockstarOfficial, official.TranslationSource);
        var curated = official with { TranslationSource = GtaTranslationSource.Curated, KoreanDisplayName = "검수 표기" };
        var glossary = new GtaTranslationGlossary("test", [official, curated]);
        var formatter = new GtaKoreanFormatter(glossary: glossary);
        Assert.Equal("검수 표기", formatter.TranslateKnownTerms("Adversary Mode"));
        Assert.NotEqual(formatter.CacheVersion, new GtaKoreanFormatter().CacheVersion);
        for (var i = 0; i < 600; i++) formatter.TranslateKnownTerms("text " + i);
        Assert.Equal(256, formatter.CachedCount);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"version\":\"test\",\"entries\":[null]}")]
    public void MalformedGlossaryFailsToOriginal(string json)
    {
        var glossary = GtaTranslationGlossary.Parse(json);
        Assert.Empty(glossary.Entries);
        Assert.Equal("Unknown 5", glossary.Translate("Unknown 5"));
    }

    [Fact]
    public void ChallengeProjectionCannotDiscardQuantity()
    {
        var challenge = new GtaSemanticChallenge("test", "Complete 5 Bunker missions", "COMPLETE", "Bunker", 5, null, null, []);
        var result = new GtaKoreanFormatter().FormatChallenge(challenge);
        Assert.Contains("5", result);
        Assert.Contains("벙커", result);
    }
}
