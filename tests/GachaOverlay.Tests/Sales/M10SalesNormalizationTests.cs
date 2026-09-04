using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Sales;

namespace GachaOverlay.Tests.Sales;

public sealed class M10SalesNormalizationTests
{
    private const string Guild = "1417848677074079857";
    private static readonly SalesProductCatalog Catalog = EmbeddedSalesProductCatalogLoader.Load();
    private static NormalizedSalePost Parse(string text, params DiscordCustomEmoji[] emojis) =>
        SalesPostNormalizer.Parse(Guild, text, emojis, Catalog, "ko");

    [Theory]
    [InlineData("벙커", "벙커")]
    [InlineData("붕키", "벙커")]
    [InlineData("봉카", "벙커")]
    [InlineData("붕", "벙커")]
    [InlineData("나클", "나클")]
    [InlineData("나클만", "나클")]
    [InlineData("나끌", "나클")]
    [InlineData("낙글", "나클")]
    [InlineData("낙굴", "나클")]
    [InlineData("나", "나클")]
    [InlineData("낙", "나클")]
    [InlineData("대창", "스패")]
    [InlineData("1대창", "스패")]
    [InlineData("2대창", "스패 x2")]
    [InlineData("3대창", "스패 x3")]
    [InlineData("4대창", "스패 x4")]
    [InlineData("5대창", "스패 x5")]
    [InlineData("격납고", "항스패")]
    [InlineData("항스패", "항스패")]
    [InlineData("벙나", "벙커 · 나클")]
    [InlineData("벙나항", "벙커 · 나클 · 항스패")]
    [InlineData("엘", "엘")]
    public void CanonicalAliasesReuseExistingIdentity(string text, string expected)
    {
        var result = Parse(text);
        Assert.Equal(expected, SalesProductSummaryFormatter.Format(result.Products));
        Assert.Equal(SaleParseStatus.Parsed, result.Status);
        Assert.All(result.Products, product => Assert.Contains(Catalog.Products, definition => definition.ProductId == product.ProductId));
    }

    [Theory]
    [InlineData("나는 오늘 게임을 합니다")]
    [InlineData("낙하산 붕어빵 엘리베이터")]
    [InlineData("엘리트 진행합니다")]
    public void ShortAliasesNeverMatchInsideProse(string text)
    {
        var result = Parse(text);
        Assert.Empty(result.Products);
        Assert.Equal(SaleParseStatus.Unknown, result.Status);
        Assert.Equal(text, result.DetailSource);
    }

    [Fact]
    public void ExplicitBunkerCanBeExtractedFromDecorativeProseWithoutGuessingOthers()
    {
        var result = Parse("우리벙커판매합니다 아무 상품");
        Assert.Equal("벙커", Assert.Single(result.Products).DisplayName);
        Assert.Equal(SaleParseStatus.PartiallyParsed, result.Status);
    }

    [Theory]
    [InlineData("1440756004404072479", 2)]
    [InlineData("1440755981960216596", 3)]
    [InlineData("1440755960451698741", 4)]
    [InlineData("1444224678817169519", 5)]
    public void QuantityCustomEmojiUsesIdEvenWhenNameDisagrees(string id, int quantity)
    {
        var result = Parse($"스패 <:misleading_x9:{id}> 벙커 엘");
        Assert.Equal(quantity, result.Products[0].Quantity);
        Assert.All(result.Products.Skip(1), product => Assert.Equal(1, product.Quantity));
    }

    [Theory]
    [InlineData("1️⃣", 1)]
    [InlineData("2️⃣", 2)]
    [InlineData("3️⃣", 3)]
    [InlineData("4️⃣", 4)]
    [InlineData("5️⃣", 5)]
    public void UnicodeKeycapsNeedNoId(string keycap, int quantity) =>
        Assert.Equal(quantity, Assert.Single(Parse("대창 " + keycap).Products).Quantity);

    [Fact]
    public void QuantityNameFallbackOnlyWhenIdMissing()
    {
        Assert.Equal(3, Assert.Single(Parse("대창", new DiscordCustomEmoji("", "x3", false)).Products).Quantity);
        var unknownId = Parse("대창 <:x3:999999999999999999>");
        Assert.Equal(1, Assert.Single(unknownId.Products).Quantity);
        Assert.Equal(SaleParseStatus.PartiallyParsed, unknownId.Status);
    }

    [Theory]
    [InlineData("6대창")]
    [InlineData("7대창")]
    [InlineData("10대창")]
    [InlineData("5대창 <:x3:1440755981960216596>")]
    [InlineData("대창 2️⃣ 5️⃣")]
    public void InvalidOrConflictingQuantityIsNotInvented(string source)
    {
        var result = Parse(source + " 벙커");
        Assert.Equal(SaleParseStatus.Ambiguous, result.Status);
        Assert.Equal("벙커", Assert.Single(result.Products).DisplayName);
        Assert.NotNull(result.DetailSource);
    }

    [Fact]
    public void MixedTextEmojiOrderAndNonSpecialDeduplicationAreDeterministic()
    {
        var bunker = Catalog.ResolveCanonical(Guild, "벙커", "ko")!;
        var result = Parse($"엘 <:{bunker.EmojiName}:{bunker.EmojiId}> 벙나항 붕");
        Assert.Equal("엘 · 벙커 · 나클 · 항스패", SalesProductSummaryFormatter.Format(result.Products));
        Assert.All(result.Products, product => Assert.Equal(1, product.Quantity));
    }

    [Fact]
    public void UnknownEmojiIdNeverBorrowsKnownProductIdentityFromItsName()
    {
        var result = Parse("", new DiscordCustomEmoji("999999999999999999", "BUNKER", false));
        Assert.Empty(result.Products);
        Assert.Equal(SaleParseStatus.Unknown, result.Status);
    }

    [Fact]
    public void ExactCanonicalEmojiNameMayFallbackOnlyWhenIdIsUnavailable()
    {
        var bunker = Catalog.ResolveCanonical(Guild, "벙커", "ko")!;
        var result = Parse("", new DiscordCustomEmoji(string.Empty, bunker.EmojiName, false));
        Assert.Equal("벙커", Assert.Single(result.Products).DisplayName);
    }

    [Fact]
    public void SourceIsBoundedAndExcludedFromSerializedDiagnostics()
    {
        var result = Parse(new string('가', 8000));
        Assert.Equal(SalesPostNormalizer.MaximumSourceLength, result.DetailSource!.Length);
        Assert.DoesNotContain("가", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var engine = SalesTestFactory.Engine(Catalog);
        engine.ApplySourceCreate(SalesTestFactory.Message("100", guildId: Guild, content: "private-original"));
        Assert.DoesNotContain("private-original", JsonSerializer.Serialize(engine.Current), StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(engine.Current.ActiveItems).DetailSource);
        engine.ApplySourceDelete("100");
        Assert.All(engine.Records, record => Assert.Null(record.DetailSource));
    }

    [Fact]
    public void SourceUpdateReusesSameEngineAndDropsStaleParsedProducts()
    {
        var engine = SalesTestFactory.Engine(Catalog);
        engine.SetLocale("ko");
        engine.ApplySourceCreate(SalesTestFactory.Message("100", guildId: Guild, content: "벙나항"));
        engine.ApplySourceUpdate(SalesTestFactory.Message("100", guildId: Guild, content: "3대창"));
        Assert.Equal("스패 x3", Assert.Single(engine.Current.ActiveItems).ProductSummary);
        engine.ApplySourceUpdate(SalesTestFactory.Message("100", guildId: Guild, content: "5대창 x3 벙커"));
        Assert.Equal(SaleParseStatus.Ambiguous, Assert.Single(engine.Current.ActiveItems).ParseStatus);
        Assert.Equal("5대창 x3 벙커", Assert.Single(engine.Current.ActiveItems).DetailSource);
        engine.ApplySourceUpdate(SalesTestFactory.Message("100", guildId: Guild, content: "벙나"));
        Assert.Equal(SaleParseStatus.Parsed, Assert.Single(engine.Current.ActiveItems).ParseStatus);
        Assert.Null(Assert.Single(engine.Current.ActiveItems).DetailSource);
    }
}
