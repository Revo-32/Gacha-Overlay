using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class M7LocalizationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ja")]
    public void AllM7KeysExistWithoutResourceFallbackMarkers(string locale)
    {
        var localization = new ResourceLocalizationService(locale, NullAppLogger.Instance);
        var keys = new[]
        {
            "SalesHealthLiveAccessible",
            "SalesHealthConnecting",
            "SalesHealthResyncing",
            "SalesHealthPaused",
            "SalesHealthRemoteUnavailable",
            "SalesHealthRemoteAccessRevoked",
            "SalesHealthDegraded",
            "SalesHealthDisconnected",
            "SalesHealthRemoteError",
            "SalesHealthDisabled",
            "SalesCurrentSellerFormat",
            "SalesWaitingCountFormat",
            "SalesProductFormat",
            "SalesNextSellerFormat",
            "SalesQueueEmpty",
            "SalesNextTurnSelf",
            "SalesCurrentTurnSelf",
        };
        foreach (var key in keys)
        {
            var value = localization[key];
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(key, value);
        }
    }

    [Theory]
    [InlineData("en", "I'm next", "It's your turn to sell! Start selling")]
    [InlineData("ko", "다음이 내 차례", "판매할 차례입니다! 판매를 시작하세요")]
    [InlineData("ja", "次は自分の番", "販売の順番です！販売を始めてください")]
    public void PersonalAlertText_IsNaturallyLocalized(
        string locale,
        string next,
        string current)
    {
        var localization = new ResourceLocalizationService(locale, NullAppLogger.Instance);
        Assert.Equal(next, localization["SalesNextTurnSelf"]);
        Assert.Equal(current, localization["SalesCurrentTurnSelf"]);
    }

    [Fact]
    public void KoreanFixedUxPhrases_MatchSpecification()
    {
        var localization = new ResourceLocalizationService("ko", NullAppLogger.Instance);
        Assert.Equal("대기 없음", localization["SalesQueueEmpty"]);
        Assert.Equal("판매 상태 재동기화 중", localization["SalesHealthResyncing"]);
        Assert.Equal("판매 상태 일부만 확인됨", localization["SalesHealthDegraded"]);
        Assert.Equal("Discord 연결 끊김", localization["SalesHealthDisconnected"]);
        Assert.Equal(
            "판매 정보를 불러오지 못했습니다. 설정에서 다시 시도하세요.",
            localization["SalesHealthRemoteError"]);
    }
}
