using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesProductCatalogTests
{
    [Fact]
    public void Test53_EmojiIdMapping_Succeeds()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("bunker", "100", "bunker", "GTA Bunker"));
        var mapped = catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "renamed", false) },
            "en");
        Assert.Equal("bunker", mapped!.ProductId);
    }

    [Fact]
    public void Test54_EmojiId_HasPriorityOverNameFallback()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("by-id", "100", "other", "By ID"),
            SalesTestFactory.Product("by-name", "200", "shown", "By Name"));
        var mapped = catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "shown", false) },
            "en");
        Assert.Equal("by-id", mapped!.ProductId);
    }

    [Fact]
    public void Test55_UnmappedEmoji_ProducesNullProduct()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("one", "100", "one"));
        Assert.Null(catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("999", "missing", false) },
            "en"));
    }

    [Fact]
    public void Test56_NoEmoji_ProducesNullProduct()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("one", "100", "one"));
        Assert.Null(catalog.MapFirst("guild", Array.Empty<DiscordCustomEmoji>(), "en"));
    }

    [Fact]
    public void Test57_MultipleMappedEmoji_SelectsFirstContentOrder()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("first", "100", "first"),
            SalesTestFactory.Product("second", "200", "second"));
        var mapped = catalog.MapFirst(
            "guild",
            new[]
            {
                new DiscordCustomEmoji("200", "second", false),
                new DiscordCustomEmoji("100", "first", false),
            },
            "en");
        Assert.Equal("second", mapped!.ProductId);
    }

    [Fact]
    public void Test58_MultipleEmojiInSameProductGroup_AreCountedTogether()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("same", "100", "one"),
            SalesTestFactory.Product("same", "200", "two"));
        var mapped = catalog.MapFirst(
            "guild",
            new[]
            {
                new DiscordCustomEmoji("100", "one", false),
                new DiscordCustomEmoji("200", "two", false),
            },
            "en");
        Assert.Equal("same", mapped!.ProductId);
        Assert.Equal(2, mapped.Quantity);
        Assert.Equal("Product x2", mapped.QuantityDisplayName);
    }

    [Fact]
    public void RepeatedSameEmojiInOneMessage_IncreasesGroupedQuantity()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("same", "100", "one", "Bunker"));

        var mapped = catalog.MapFirst(
            "guild",
            new[]
            {
                new DiscordCustomEmoji("100", "one", false),
                new DiscordCustomEmoji("100", "one", false),
                new DiscordCustomEmoji("100", "one", false),
            },
            "en");

        Assert.Equal(3, mapped!.Quantity);
        Assert.Equal("Bunker x3", mapped.QuantityDisplayName);
    }

    [Fact]
    public void Test59_MalformedCatalog_FallsBackSafelyToEmpty()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "products.json");
        File.WriteAllText(path, "{ malformed");
        var store = new JsonSalesProductCatalogStore(path, NullAppLogger.Instance);
        Assert.Empty(store.Load().Products);
    }

    [Fact]
    public void Test60_DuplicateEmojiId_IsRejected()
    {
        var document = new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            new[]
            {
                SalesTestFactory.Product("one", "100", "one"),
                SalesTestFactory.Product("two", "100", "two"),
            });
        Assert.Throws<InvalidDataException>(() =>
            SalesProductCatalog.CreateValidated(document));
    }

    [Fact]
    public void Test61_LocalizedProductName_FallsBackToEnglish()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("one", "100", "one", english: "English"));
        var mapped = catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "one", false) },
            "ja");
        Assert.Equal("English", mapped!.DisplayName);
    }

    [Fact]
    public void LocalizedProductName_FallsBackFromEnglishToEmojiName()
    {
        var catalog = SalesTestFactory.Catalog(
            new SalesProductDefinition(
                "one",
                "100",
                "capsule",
                null,
                new Dictionary<string, string>()));

        var mapped = catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "renamed", false) },
            "ja");

        Assert.Equal("capsule", mapped!.DisplayName);
    }

    [Fact]
    public void MappingWithoutAnyDisplayName_ProducesNoProductPlaceholder()
    {
        var catalog = SalesTestFactory.Catalog(
            new SalesProductDefinition(
                "technical-product-id",
                "100",
                null,
                null,
                new Dictionary<string, string>()));

        Assert.Null(catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "renamed", false) },
            "ja"));
    }

    [Fact]
    public void DisabledMapping_DoesNotParticipateInGroupedCounting()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("bunker", "100", "one", "Bunker"),
            SalesTestFactory.Product("bunker", "200", "two", "Bunker") with { Enabled = false });

        var mapped = catalog.MapFirst(
            "guild",
            new[]
            {
                new DiscordCustomEmoji("100", "one", false),
                new DiscordCustomEmoji("200", "two", false),
            },
            "en");

        Assert.Equal(1, mapped!.Quantity);
    }

    [Fact]
    public void ProductCatalog_SaveIsAtomicAndVersioned()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "products.json");
        var store = new JsonSalesProductCatalogStore(path, NullAppLogger.Instance);
        var document = new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            new[] { SalesTestFactory.Product("one", "100", "one") });
        Assert.True(store.Save(document));
        Assert.Single(store.Load().Products);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            SalesProductCatalogDocument.CurrentVersion,
            saved.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void VersionOneCatalog_MigratesFriendlyGroupNameWithoutChangingProductId()
    {
        var legacy = new SalesProductCatalogDocument(
            SalesProductCatalogDocument.LegacyVersion,
            new[]
            {
                new SalesProductDefinition(
                    "legacy-bunker",
                    "100",
                    "bunker",
                    null,
                    new Dictionary<string, string> { ["ko"] = "벙커" }),
            });

        var migrated = SalesProductCatalog.CreateValidated(legacy);
        var product = Assert.Single(migrated.Products);

        Assert.Equal("legacy-bunker", product.ProductId);
        Assert.Equal("벙커", product.GroupName);
    }

    [Fact]
    public void ProductCatalog_ReplacementKeepsPreviousValidFileAsBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "products.json");
        var store = new JsonSalesProductCatalogStore(path, NullAppLogger.Instance);
        Assert.True(store.Save(new SalesProductCatalogDocument(
            1,
            new[] { SalesTestFactory.Product("old", "100", "old") })));

        Assert.True(store.Save(new SalesProductCatalogDocument(
            1,
            new[] { SalesTestFactory.Product("new", "200", "new") })));

        Assert.Equal("new", Assert.Single(store.Load().Products).ProductId);
        Assert.Equal(
            "old",
            Assert.Single(new JsonSalesProductCatalogStore(path + ".bak").Load().Products).ProductId);
    }

    [Fact]
    public void GuildScopedMapping_OverridesGlobalMapping()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("global", "100", "one"),
            SalesTestFactory.Product("scoped", "100", "one", guildId: "guild"));
        var mapped = catalog.MapFirst(
            "guild",
            new[] { new DiscordCustomEmoji("100", "one", false) },
            "en");
        Assert.Equal("scoped", mapped!.ProductId);
    }
}
