using GachaOverlay.Core.Chat;

namespace GachaOverlay.Tests.Chat;

public sealed class ChatSettingsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1.5, 1.5)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(99, 10)]
    public void OutlineThickness_SupportsZeroThroughTen(double input, double expected) =>
        Assert.Equal(expected, ChatSettings.NormalizeOutlineThickness(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 3)]
    public void MaxLines_AreBounded(int input, int expected)
    {
        Assert.Equal(expected, ChatSettings.NormalizeMaxLines(input));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(18, 18)]
    [InlineData(100, 32)]
    public void FontSize_IsBounded(double input, double expected)
    {
        Assert.Equal(expected, ChatSettings.NormalizeFontSize(input));
    }

    [Theory]
    [InlineData(0.2, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.65, 1.65)]
    public void LineHeight_AllowsDenseButNonOverlappingMinimum(double input, double expected) =>
        Assert.Equal(expected, ChatSettings.NormalizeLineHeightMultiplier(input));

    [Theory]
    [InlineData(-99, -2)]
    [InlineData(-2, -2)]
    [InlineData(6, 6)]
    public void MessageSpacing_AllowsControlledDenseOverlapRange(double input, double expected) =>
        Assert.Equal(expected, ChatSettings.NormalizeMessageSpacing(input));
}
