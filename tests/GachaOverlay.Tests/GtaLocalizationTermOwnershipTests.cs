using System.Text.RegularExpressions;
using GachaOverlay.Core.Gta.Localization;
using LSOverlay.Backend.Gta.Localization;

namespace GachaOverlay.Tests;

public sealed class GtaLocalizationTermOwnershipTests
{
    private static LocalizationProtection Protector => new(GtaLocalizationGlossary.Default);

    [Fact]
    public void ActualProductionStaffSourcingResponseIsRejectedBeforeRestoration()
    {
        var field = Protector.Protect("2X SPEED\nStaff Sourcing Special Cargo", 7);
        Assert.Equal("[[L007_0000]] SPEED\nStaff Sourcing [[L007_0001]]", field.Text);
        Assert.Equal("스페셜 패키지", field.Tokens["[[L007_0001]]"]);
        Assert.Contains("[[L007_0001]]", field.GlossaryTokens);
        const string actual = "스페셜 패키지 [[L007_0001]] 직원 조달 속도 [[L007_0000]]";
        Assert.False(LocalizationProtection.TryRestore(field, actual, out _, out var reason));
        Assert.Equal("RepeatedTerm", reason);
        Assert.True(LocalizationProtection.TryRestore(field,
            "[[L007_0001]] 직원 조달 속도 [[L007_0000]]", out var restored, out _));
        Assert.Single(Regex.Matches(restored, "스페셜 패키지"));
        Assert.Contains("직원", restored);
        Assert.Contains("조달", restored);
        Assert.Contains("2배", restored);
    }

    [Theory]
    [InlineData("2X Special Cargo sell bonus", "스페셜 패키지")]
    [InlineData("Special Cargo sourcing", "스페셜 패키지")]
    [InlineData("Bunker Research Missions", "벙커 연구")]
    [InlineData("Service Carbine reward", "서비스 카빈")]
    public void GlossaryOwnedCompoundsCannotBeCopiedBesideTheirToken(string source, string korean)
    {
        var field = Protector.Protect(source);
        var key = field.Tokens.Single(t => t.Value == korean).Key;
        var body = string.Join(" ", field.Tokens.Keys) + " 안내";
        foreach (var duplicate in new[] { korean + " " + key, key + " " + korean })
        {
            Assert.False(LocalizationProtection.TryRestore(field, body.Replace(key, duplicate), out _, out var reason));
            Assert.Equal("RepeatedTerm", reason);
        }
        Assert.True(LocalizationProtection.TryRestore(field, body, out _, out _));
    }

    [Fact]
    public void SeparateOccurrencesOfSameEntityKeepTheirOwnTokens()
    {
        var field = Protector.Protect("Sell Special Cargo and source Special Cargo");
        var keys = field.Tokens.Keys.ToArray();
        Assert.Equal(2, field.GlossaryTokens.Count);
        Assert.True(LocalizationProtection.TryRestore(field,
            $"{keys[0]} 판매 후 {keys[1]} 조달", out var restored, out _));
        Assert.Equal(2, Regex.Matches(restored, "스페셜 패키지").Count);
        Assert.True(LocalizationProtection.TryRestore(field,
            string.Join(" ", keys) + " 판매와 조달", out restored, out _));
        Assert.Equal(2, Regex.Matches(restored, "스페셜 패키지").Count);
    }

    [Fact]
    public void ExplicitKoreanSourceRepetitionIsNotModelAddedContent()
    {
        var field = Protector.Protect("스페셜 패키지 Special Cargo");
        Assert.True(LocalizationProtection.TryRestore(field, field.Text, out var restored, out _));
        Assert.Equal(2, Regex.Matches(restored, "스페셜 패키지").Count);
    }

    [Fact]
    public void UnownedKoreanProperNameIsNotTreatedAsGlossaryProvenance()
    {
        var field = new ProtectedLocalizationText("creator's repeated title", "[[L000_0000]]",
            new Dictionary<string, string> { ["[[L000_0000]]"] = "스페셜 패키지" });
        Assert.True(LocalizationProtection.TryRestore(field, "스페셜 패키지 [[L000_0000]]", out _, out _));
    }

    [Fact]
    public void OwnershipRepairPreservesDistinctTokensAndInvalidatesOldIdentity()
    {
        Assert.NotEqual("gta-protect-5-weekly-conditions", LocalizationProtection.Version);
        Assert.NotEqual("gta-repair-3-weekly-conditions", LocalizationRepairFeedback.Version);
        var feedback = LocalizationRepairFeedback.Create([], "{}");
        Assert.Contains("BEFORE or AFTER", feedback.Instructions);
        Assert.Contains("Distinct tokens", feedback.Instructions);
    }
}
