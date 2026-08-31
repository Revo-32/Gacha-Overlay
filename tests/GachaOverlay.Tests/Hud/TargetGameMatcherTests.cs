using GachaOverlay.Core.Hud.Game;

namespace GachaOverlay.Tests.Hud;

public sealed class TargetGameMatcherTests
{
    [Theory]
    [InlineData("GTA5")]
    [InlineData("gta5.exe")]
    [InlineData("C:\\Games\\GTA5_Enhanced.EXE")]
    public void Defaults_MatchConfiguredLegacyAndEnhancedCandidates(string processName)
    {
        var matcher = new TargetGameMatcher();

        Assert.True(matcher.IsTarget(processName));
    }

    [Fact]
    public void NonTargetProcess_DoesNotMatch()
    {
        var matcher = new TargetGameMatcher();

        Assert.False(matcher.IsTarget("Discord.exe"));
    }

    [Fact]
    public void CustomConfiguration_IsNormalized()
    {
        var matcher = new TargetGameMatcher(new[] { " CustomGame.EXE " });

        Assert.True(matcher.IsTarget("customgame"));
        Assert.False(matcher.IsTarget("GTA5"));
    }

    [Fact]
    public void InvalidConfiguration_FallsBackToSafeDefaults()
    {
        var matcher = new TargetGameMatcher(new[] { "", "   " });

        Assert.True(matcher.IsTarget("GTA5.exe"));
    }
}
