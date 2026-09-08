using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.Sales;

namespace GachaOverlay.Tests;

public sealed class SalesRendering23Tests
{
    [Theory]
    [InlineData("벙커")]
    [InlineData("알 수 없는 판매 상품")]
    [InlineData("벙커 추가 설명")]
    [InlineData("5대창 x3 벙커")]
    [InlineData("테스트 <:SELL_SP:1439136641330708581> <:uhh:1470668495594459237>")]
    [InlineData("테스트 <:SELL_SP:1439136641330708581> <:SELL_SP:1439136641330708581>")]
    [InlineData("텍스트 👍 <a:party:123456789012345678>")]
    public void ReceivedSourceSurvivesEveryParserOutcome(string content)
    {
        var engine = SalesTestFactory.Engine(EmbeddedSalesProductCatalogLoader.Load());
        var message = SalesTestFactory.Message("100", guildId: "1417848677074079857", content: content);
        engine.ApplySourceCreate(message);
        var entry = Assert.Single(engine.Current.ActiveItems);
        Assert.Equal(content, entry.DetailSource);
        var row = Row(entry.DetailSource!);
        var tokens = ChatPresentationSynchronizer.TokenizeDiscordMarkup(content);
        Assert.Equal(tokens.Select(x => x.Text), row.DetailTokens.Select(x => x.Text));
        Assert.All(row.DetailTokens.Where(x => x.Kind == ChatTokenKind.CustomEmoji), token =>
        {
            Assert.Null(token.Image);
            Assert.StartsWith(":", token.Text);
            Assert.EndsWith(":", token.Text);
            Assert.DoesNotContain(token.Identity!, token.Text);
        });
        engine.ApplySourceUpdate(message with { Content = "갱신 <:other:223456789012345678>" });
        Assert.Equal("갱신 <:other:223456789012345678>", Assert.Single(engine.Current.ActiveItems).DetailSource);
        engine.ApplySourceDelete("100");
        Assert.Empty(engine.Current.ActiveItems);
        Assert.All(engine.Records, x => Assert.Null(x.DetailSource));
    }

    [Fact]
    public void RawContentIsOnlyInFullDetailNotCollapsedSalesBar()
    {
        var original = string.Join('\n', Enumerable.Repeat("원본 <:product:123456789012345678>", 100));
        var row = Row(original);
        Assert.Equal(original, row.DetailSource);
        Assert.Equal(100, row.DetailTokens.Count(x => x.Kind == ChatTokenKind.CustomEmoji));
        var source = File.ReadAllText(Path.Combine(Root(), "src/GachaOverlay.App/Presentation/SalesQueueView.xaml"));
        Assert.DoesNotContain("CurrentRawItem", source);
        Assert.Contains("Tokens=\"{Binding DetailTokens}\"", source);
        Assert.Contains("FontSize=\"14\"", source);
        Assert.DoesNotContain("SharedSizeGroup=\"SalesBarEdge\"", source);
        Assert.Contains("TextAlignment=\"Left\"", source);
        Assert.DoesNotContain("MaxHeight=\"80\"", source);
    }

    private static SalesQueueDetailItem Row(string content) => new(1, "100", "판매자", "파싱 요약", true, false, true,
        "현재", "나", false, "", "완료", _ => Task.CompletedTask, detailSource: content);
    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GachaOverlay.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
