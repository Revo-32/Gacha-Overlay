using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales;

public sealed class M753ProductAggregationTests
{
    [Fact]
    public void OneProduct_HasNoQuantitySuffix()
    {
        var products = Catalog().MapAll("guild", Emojis("bunker-a"), "ko");

        Assert.Equal("벙커", SalesProductSummaryFormatter.Format(products));
        Assert.DoesNotContain("x1", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void SameEmojiTwice_IsAggregatedWithLowercaseAsciiX()
    {
        var products = Catalog().MapAll("guild", Emojis("bunker-a", "bunker-a"), "ko");

        Assert.Equal(2, Assert.Single(products).Quantity);
        Assert.Equal("벙커 x2", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void DifferentEmojiWithSameProductId_AreAggregated()
    {
        var products = Catalog().MapAll("guild", Emojis("bunker-a", "bunker-b"), "ko");

        Assert.Equal("벙커 x2", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void DifferentProducts_AreAllPreserved()
    {
        var products = Catalog().MapAll("guild", Emojis("bunker-a", "nightclub"), "ko");

        Assert.Equal(2, products.Count);
        Assert.Equal("벙커 · 나클", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void QuantityAndDifferentProducts_UseOneCommonFormatter()
    {
        var products = Catalog().MapAll(
            "guild",
            Emojis("bunker-a", "bunker-b", "nightclub"),
            "ko");

        Assert.Equal("벙커 x2 · 나클", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void ThreeProducts_UseMiddleDotSeparator()
    {
        var products = Catalog().MapAll(
            "guild",
            Emojis("bunker-a", "nightclub", "acid"),
            "ko");

        Assert.Equal("벙커 · 나클 · 산성 연구실", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void FirstAppearanceOrder_IsPreserved()
    {
        var products = Catalog().MapAll(
            "guild",
            Emojis("nightclub", "bunker-a", "acid"),
            "ko");

        Assert.Equal(new[] { "nightclub", "bunker", "acid" }, products.Select(x => x.ProductId));
        Assert.Equal("나클 · 벙커 · 산성 연구실", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void UnmappedEmoji_IsIgnoredWithoutDroppingMappedProducts()
    {
        var products = Catalog().MapAll(
            "guild",
            Emojis("unknown", "bunker-a", "unknown"),
            "ko");

        Assert.Equal("벙커", SalesProductSummaryFormatter.Format(products));
    }

    [Fact]
    public void DisabledMapping_IsIgnored()
    {
        var catalog = SalesTestFactory.Catalog(
            Product("bunker", "bunker-a", "벙커") with { Enabled = false });

        Assert.Empty(catalog.MapAll("guild", Emojis("bunker-a"), "ko"));
    }

    [Fact]
    public void Formatter_NeverUsesMultiplicationSignOrComma()
    {
        var summary = SalesProductSummaryFormatter.Format(
            Catalog().MapAll("guild", Emojis("bunker-a", "bunker-a", "nightclub"), "ko"));

        Assert.DoesNotContain('×', summary);
        Assert.DoesNotContain(',', summary);
        Assert.Contains("x2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineSnapshotAndDetailModel_RetainEveryProduct()
    {
        var engine = SalesTestFactory.Engine(Catalog());
        engine.SetLocale("ko");
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "100",
            emojis: Emojis("bunker-a", "bunker-b", "nightclub")));

        var entry = Assert.Single(engine.Current.ActiveItems);
        Assert.Equal(2, entry.AllProducts.Count);
        Assert.Equal("벙커 x2 · 나클", entry.ProductSummary);
        Assert.Equal("bunker", entry.Product?.ProductId);
    }

    [Fact]
    public void MessageUpdate_RecalculatesAllProductQuantities()
    {
        var engine = SalesTestFactory.Engine(Catalog());
        engine.SetLocale("ko");
        engine.ApplySourceCreate(SalesTestFactory.Message("100", emojis: Emojis("bunker-a")));

        engine.ApplySourceUpdate(SalesTestFactory.Message(
            "100",
            content: "changed",
            emojis: Emojis("bunker-a", "bunker-b", "nightclub")));

        Assert.Equal("벙커 x2 · 나클", engine.Current.CurrentSeller!.ProductSummary);
    }

    [Fact]
    public void MessageDelete_RemovesMultiProductEntry()
    {
        var engine = SalesTestFactory.Engine(Catalog());
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "100",
            emojis: Emojis("bunker-a", "nightclub")));

        engine.ApplySourceDelete("100");

        Assert.Empty(engine.Current.ActiveItems);
    }

    [Fact]
    public void LocaleChange_RelocalizesEveryAggregatedProduct()
    {
        var engine = SalesTestFactory.Engine(Catalog());
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "100",
            emojis: Emojis("bunker-a", "nightclub")));

        Assert.True(engine.SetLocale("ko"));

        Assert.Equal("벙커 · 나클", engine.Current.CurrentSeller!.ProductSummary);
    }

    [Fact]
    public void QueueSummaryAndDetail_UseAllProductsWithoutProductPrefix()
    {
        var engine = SalesTestFactory.Engine(Catalog());
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "100",
            emojis: Emojis("bunker-a", "bunker-b", "nightclub")));
        SalesTestFactory.TrustPending(engine, "100");
        engine.SetLocale("ko");
        var viewModel = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
        viewModel.UpdateHudContext(true, false, false, true);

        viewModel.Apply(engine.Current, AppSettings.CreateDefault() with { SalesShowProduct = true });

        Assert.Equal("벙커 x2 · 나클", Assert.Single(viewModel.DetailItems).ProductName);
        Assert.Contains("벙커 x2 · 나클", viewModel.PrimaryLine + viewModel.SecondaryLine, StringComparison.Ordinal);
        Assert.DoesNotContain("상품 벙커", viewModel.PrimaryLine + viewModel.SecondaryLine, StringComparison.Ordinal);
    }

    private static SalesProductCatalog Catalog() => SalesTestFactory.Catalog(
        Product("bunker", "bunker-a", "벙커"),
        Product("bunker", "bunker-b", "벙커"),
        Product("nightclub", "nightclub", "나클"),
        Product("acid", "acid", "산성 연구실"));

    private static SalesProductDefinition Product(string id, string emoji, string korean) =>
        SalesTestFactory.Product(id, emoji, emoji, id, korean, "guild") with
        {
            GroupName = korean,
        };

    private static IReadOnlyList<DiscordCustomEmoji> Emojis(params string[] names) =>
        names.Select(name => new DiscordCustomEmoji(name, name, false)).ToArray();
}
