using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Sales;

public sealed class M757BuiltInProductCatalogTests
{
    private const string GuildId = "1417848677074079857";

    [Fact]
    public void EmbeddedResource_ExistsAndParsesExactAuthoritativeShape()
    {
        Assert.True(EmbeddedSalesProductCatalogLoader.ResourceExists());
        var catalog = BuiltIn();

        Assert.Equal(14, catalog.Products.Count);
        Assert.Equal(10, catalog.Products.Select(product => product.ProductId).Distinct().Count());
        Assert.All(catalog.Products, product => Assert.Equal(GuildId, product.GuildId));
    }

    [Fact]
    public void EmbeddedResource_PreservesEveryProvidedEmojiIdAndGroupName()
    {
        var expected = new Dictionary<string, string>
        {
            ["1418347703552839802"] = "벙커",
            ["1418348629055504486"] = "나클",
            ["1461355402792009729"] = "항스패",
            ["1439136641330708581"] = "스패",
            ["1438464695815245904"] = "벙커",
            ["1436906875051311154"] = "필",
            ["1436478258316185731"] = "코",
            ["1436909263124561940"] = "위",
            ["1453317209815388252"] = "위",
            ["1436478165567541400"] = "마",
            ["1523061893567352884"] = "엘",
            ["1438464558523088937"] = "엘",
            ["1438464639141810249"] = "나클",
            ["1436485825767411863"] = "반반",
        };

        Assert.Equal(expected.Keys.Order(), BuiltIn().Products.Select(product => product.EmojiId).Order());
        Assert.All(BuiltIn().Products, product => Assert.Equal(expected[product.EmojiId], product.GroupName));
    }

    [Theory]
    [InlineData("GTA_Bunker", "SELL_BUNG")]
    [InlineData("GTA_Nightclub", "SELL_NA")]
    [InlineData("GTA_Bikers_cash", "GTA_Bikers_paper")]
    [InlineData("GTA_LSD", "SELL_L")]
    public void AuthoritativePairs_ShareStableProductId(string first, string second)
    {
        var catalog = BuiltIn();
        Assert.Equal(
            catalog.Products.Single(product => product.EmojiName == first).ProductId,
            catalog.Products.Single(product => product.EmojiName == second).ProductId);
    }

    [Fact]
    public void EmbeddedNames_DoNotInventEnglishOrJapaneseTranslations()
    {
        Assert.All(BuiltIn().Products, product =>
        {
            Assert.Equal(new[] { SupportedLocales.Korean }, product.DisplayNames.Keys);
            Assert.False(product.DisplayNames.ContainsKey(SupportedLocales.English));
            Assert.False(product.DisplayNames.ContainsKey(SupportedLocales.Japanese));
        });
    }

    [Fact]
    public void FreshWorkspace_UsesBuiltInWithoutAnyAppDataFileOrManager()
    {
        using var directory = new TemporaryDirectory();
        var overridePath = directory.File("sales-products.override.json");
        var workspace = new EffectiveSalesProductCatalogStore(BuiltIn(), overridePath);

        Assert.False(File.Exists(overridePath));
        Assert.Equal("벙커", Map(workspace.EffectiveCatalog, "1418347703552839802", "GTA_Bunker")!.DisplayName);
        Assert.Equal("벙커", Map(workspace.EffectiveCatalog, "1438464695815245904", "SELL_BUNG")!.DisplayName);
        Assert.Equal(0, workspace.OverrideCount);
    }

    [Fact]
    public void BuiltInGrouping_FormatsExpectedMultiProductSequence()
    {
        var mapped = BuiltIn().MapAll(
            GuildId,
            new[]
            {
                Emoji("1418347703552839802", "GTA_Bunker"),
                Emoji("1438464695815245904", "SELL_BUNG"),
                Emoji("1418348629055504486", "GTA_Nightclub"),
            },
            SupportedLocales.English);

        Assert.Equal(new[] { "벙커 x2", "나클" }, mapped.Select(product => product.QuantityDisplayName));
    }

    [Fact]
    public void OverrideWinsWithoutHidingUnrelatedBuiltIns()
    {
        using var directory = new TemporaryDirectory();
        var workspace = new EffectiveSalesProductCatalogStore(
            BuiltIn(),
            directory.File("sales-products.override.json"));
        var products = workspace.EffectiveCatalog.Products
            .Select(product => product.EmojiName == "GTA_Bunker"
                ? product with
                {
                    DisplayNames = new Dictionary<string, string> { ["ko"] = "벙커 판매" },
                    GroupName = "벙커 판매",
                }
                : product)
            .ToArray();

        Assert.True(workspace.SaveEffective(new SalesProductCatalogDocument(2, products)));

        Assert.Equal(SalesProductDefinitionSource.Modified, workspace.GetSource(GuildId, "1418347703552839802"));
        Assert.Equal("벙커 판매", Map(workspace.EffectiveCatalog, "1418347703552839802", "GTA_Bunker")!.DisplayName);
        Assert.Equal("나클", Map(workspace.EffectiveCatalog, "1418348629055504486", "GTA_Nightclub")!.DisplayName);
        Assert.Equal(1, workspace.OverrideCount);
    }

    [Fact]
    public void DisabledOverrideSuppressesBuiltInAndRestoreRevealsItImmediately()
    {
        using var directory = new TemporaryDirectory();
        var workspace = new EffectiveSalesProductCatalogStore(
            BuiltIn(),
            directory.File("sales-products.override.json"));
        var products = workspace.EffectiveCatalog.Products
            .Select(product => product.EmojiName == "GTA_Bunker"
                ? product with { Enabled = false }
                : product)
            .ToArray();

        Assert.True(workspace.SaveEffective(new SalesProductCatalogDocument(2, products)));
        Assert.Null(Map(workspace.EffectiveCatalog, "1418347703552839802", "GTA_Bunker"));
        Assert.Equal(SalesProductDefinitionSource.Disabled, workspace.GetSource(GuildId, "1418347703552839802"));

        Assert.True(workspace.RestoreDefault(GuildId, "1418347703552839802"));
        Assert.Equal("벙커", Map(workspace.EffectiveCatalog, "1418347703552839802", "GTA_Bunker")!.DisplayName);
        Assert.Equal(SalesProductDefinitionSource.BuiltIn, workspace.GetSource(GuildId, "1418347703552839802"));
    }

    [Fact]
    public void CustomOverrideIsDeterministicAndCanBeReset()
    {
        using var directory = new TemporaryDirectory();
        var workspace = new EffectiveSalesProductCatalogStore(
            BuiltIn(),
            directory.File("sales-products.override.json"));
        var custom = new SalesProductDefinition(
            "custom-product", "999", "custom", GuildId,
            new Dictionary<string, string> { ["ko"] = "사용자" }, true, "사용자");
        var document = new SalesProductCatalogDocument(
            2,
            workspace.EffectiveCatalog.Products.Append(custom).ToArray());

        Assert.True(workspace.SaveEffective(document));
        Assert.Equal(SalesProductDefinitionSource.Custom, workspace.GetSource(GuildId, "999"));
        Assert.Equal("사용자", Map(workspace.EffectiveCatalog, "999", "custom")!.DisplayName);
        var first = workspace.EffectiveCatalog.Products.Select(product => product.EmojiId).ToArray();
        Assert.True(workspace.SaveEffective(new SalesProductCatalogDocument(2, workspace.EffectiveCatalog.Products)));
        Assert.Equal(first, workspace.EffectiveCatalog.Products.Select(product => product.EmojiId));

        Assert.True(workspace.ResetOverrides());
        Assert.Equal(14, workspace.EffectiveCatalog.Products.Count);
        Assert.Equal(0, workspace.OverrideCount);
    }

    [Fact]
    public void BuiltInMappingNeverLeaksToAnotherGuild()
    {
        Assert.Null(BuiltIn().MapFirst(
            "other-guild",
            new[] { Emoji("1418347703552839802", "GTA_Bunker") },
            SupportedLocales.Korean));
    }

    [Fact]
    public void LegacyMigrationPreservesChangesDisabledCustomAndGroupingButNotMissingDefaults()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = directory.File("sales-products.json");
        var overridePath = directory.File("sales-products.override.json");
        var builtIn = BuiltIn();
        var bunkerPair = builtIn.Products
            .Where(product => product.ProductId == "group-b6b87a92ca10")
            .Select(product => product.EmojiName == "GTA_Bunker"
                ? product with
                {
                    DisplayNames = new Dictionary<string, string> { ["ko"] = "수정 벙커" },
                    GroupName = "수정 벙커",
                }
                : product with { Enabled = false })
            .ToArray();
        var custom = new SalesProductDefinition(
            "legacy-custom", "999", "legacy", GuildId,
            new Dictionary<string, string> { ["ko"] = "사용자" }, true, "사용자");
        Assert.True(new JsonSalesProductCatalogStore(legacyPath).Save(
            new SalesProductCatalogDocument(2, bunkerPair.Append(custom).ToArray())));

        var workspace = new EffectiveSalesProductCatalogStore(
            builtIn, overridePath, legacyPath);

        Assert.True(File.Exists(legacyPath));
        Assert.True(File.Exists(overridePath));
        Assert.Equal(3, workspace.OverrideCount);
        Assert.Equal("수정 벙커", Map(workspace.EffectiveCatalog, "1418347703552839802", "GTA_Bunker")!.DisplayName);
        Assert.Null(Map(workspace.EffectiveCatalog, "1438464695815245904", "SELL_BUNG"));
        Assert.Equal("나클", Map(workspace.EffectiveCatalog, "1418348629055504486", "GTA_Nightclub")!.DisplayName);
        Assert.Equal("group-b6b87a92ca10", workspace.EffectiveCatalog.Products.Single(product => product.EmojiName == "GTA_Bunker").ProductId);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void OverrideAtomicSave_KeepsPreviousFileAndOneBackup()
    {
        using var directory = new TemporaryDirectory();
        var overridePath = directory.File("sales-products.override.json");
        var store = new JsonSalesProductCatalogStore(overridePath);
        var first = new SalesProductDefinition(
            "first", "901", "first", GuildId,
            new Dictionary<string, string> { ["en"] = "First" });
        var second = first with
        {
            ProductId = "second",
            DisplayNames = new Dictionary<string, string> { ["en"] = "Second" },
        };
        Assert.True(store.Save(new SalesProductCatalogDocument(2, new[] { first })));

        Assert.True(store.Save(new SalesProductCatalogDocument(2, new[] { second })));

        Assert.Equal("second", Assert.Single(store.Load().Products).ProductId);
        Assert.Equal(
            "first",
            Assert.Single(new JsonSalesProductCatalogStore(overridePath + ".bak").Load().Products)
                .ProductId);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void OverrideReplacementFailure_PreservesPreviousValidFile()
    {
        using var directory = new TemporaryDirectory();
        var overridePath = directory.File("sales-products.override.json");
        var store = new JsonSalesProductCatalogStore(overridePath);
        var original = new SalesProductDefinition(
            "original", "902", "original", GuildId,
            new Dictionary<string, string> { ["en"] = "Original" });
        Assert.True(store.Save(new SalesProductCatalogDocument(2, new[] { original })));
        Directory.CreateDirectory(overridePath + ".bak");

        var saved = store.Save(new SalesProductCatalogDocument(
            2,
            new[] { original with { ProductId = "replacement" } }));

        Assert.False(saved);
        Assert.Equal("original", Assert.Single(store.Load().Products).ProductId);
    }

    [Fact]
    public void ManagerMarksBuiltInAndKeepsSharedProductNameConsistent()
    {
        using var directory = new TemporaryDirectory();
        var workspace = new EffectiveSalesProductCatalogStore(
            BuiltIn(), directory.File("sales-products.override.json"));
        SalesProductCatalog? applied = null;
        var viewModel = new ProductMappingManagerViewModel(
            workspace,
            () => Array.Empty<SalesEmojiInventoryItem>(),
            catalog => applied = catalog,
            new ResourceLocalizationService(SupportedLocales.Korean));
        var bunker = viewModel.Mappings.First(mapping => mapping.EmojiName == "GTA_Bunker");
        viewModel.SelectedMapping = bunker;
        bunker.ProductName = "벙커 판매";

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.All(
            applied.Products.Where(product => product.ProductId == "group-b6b87a92ca10"),
            product => Assert.Equal("벙커 판매", product.GroupName));
        Assert.All(
            viewModel.Mappings.Where(mapping => mapping.ProductId == "group-b6b87a92ca10"),
            mapping => Assert.Equal(SalesProductDefinitionSource.Modified, mapping.Source));
    }

    private static SalesProductCatalog BuiltIn() => EmbeddedSalesProductCatalogLoader.Load();

    private static SaleProduct? Map(SalesProductCatalog catalog, string id, string name) =>
        catalog.MapFirst(GuildId, new[] { Emoji(id, name) }, SupportedLocales.English);

    private static DiscordCustomEmoji Emoji(string id, string name) => new(id, name, false);
}
