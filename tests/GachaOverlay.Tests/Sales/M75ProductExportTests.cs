using System.Text.Json;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.TestSupport;
using GachaOverlay.App.Presentation;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales;

public sealed class M75ProductExportTests
{
    [Fact]
    public void Export_ContainsVersionTimestampAndSafeMappingFieldsOnly()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSalesProductCatalogStore(directory.File("sales-products.json"));
        var exportPath = directory.File("export.json");
        var catalog = SalesProductCatalog.CreateValidated(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            new[]
            {
                new SalesProductDefinition(
                    "capsule",
                    "123",
                    "capsule_emoji",
                    "guild-1",
                    new Dictionary<string, string>
                    {
                        ["en"] = "Capsule",
                        ["ko"] = "캡슐",
                        ["ja"] = "カプセル",
                    }),
            }));

        Assert.True(store.Export(exportPath, catalog));
        using var document = JsonDocument.Parse(File.ReadAllText(exportPath));
        var root = document.RootElement;

        Assert.Equal(SalesProductCatalogDocument.CurrentVersion, root.GetProperty("Version").GetInt32());
        Assert.True(root.TryGetProperty("ExportedAt", out _));
        Assert.Equal("guild-1", root.GetProperty("Products")[0].GetProperty("GuildId").GetString());
        Assert.Equal("Capsule", root.GetProperty("Products")[0].GetProperty("GroupName").GetString());
        Assert.DoesNotContain("credential", File.ReadAllText(exportPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", File.ReadAllText(exportPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log", File.ReadAllText(exportPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportFailure_PreservesExistingTarget()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSalesProductCatalogStore(directory.File("sales-products.json"));
        var targetDirectory = directory.File("target-directory");
        Directory.CreateDirectory(targetDirectory);

        Assert.False(store.Export(targetDirectory, SalesProductCatalog.Empty));
        Assert.True(Directory.Exists(targetDirectory));
    }

    [Fact]
    public void DuplicateGuildScopedEmoji_IsRejected()
    {
        var products = new[]
        {
            Product("a", "1", "guild"),
            Product("b", "1", "guild"),
        };

        Assert.Throws<InvalidDataException>(() => SalesProductCatalog.CreateValidated(
            new SalesProductCatalogDocument(1, products)));
    }

    [Fact]
    public void SameEmojiAcrossDifferentGuilds_IsAllowed()
    {
        var catalog = SalesProductCatalog.CreateValidated(new SalesProductCatalogDocument(
            1,
            new[] { Product("a", "1", "guild-a"), Product("b", "1", "guild-b") }));

        Assert.Equal(2, catalog.Products.Count);
    }

    [Fact]
    public void ReplacingCatalog_RemapsExistingQueueWithoutChangingOrderOrTrust()
    {
        var engine = SalesTestFactory.Engine();
        var message = SalesTestFactory.Message(
            "1",
            emojis: new[] { new DiscordCustomEmoji("emoji-1", "capsule", false) });
        engine.ApplySourceCreate(message);
        SalesTestFactory.TrustPending(engine, "1");
        var before = engine.Current.ActiveItems[0];
        engine.ReplaceProductCatalog(SalesTestFactory.Catalog(
            SalesTestFactory.Product("capsule", "emoji-1", "capsule", "Capsule")));

        Assert.True(engine.RemapProducts(new[] { message }));

        var after = engine.Current.ActiveItems[0];
        Assert.Equal(before.MessageId, after.MessageId);
        Assert.Equal(before.ObservationTrust, after.ObservationTrust);
        Assert.Equal("Capsule", after.Product?.DisplayName);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void MappingDelete_RequiresExplicitConfirmation(bool confirmed, int expectedCount)
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSalesProductCatalogStore(directory.File("sales-products.json"));
        Assert.True(store.Save(new SalesProductCatalogDocument(
            1,
            new[] { Product("capsule", "emoji-1", "guild") with
            {
                DisplayNames = new Dictionary<string, string> { ["en"] = "Capsule" },
            } })));
        var viewModel = new ProductMappingManagerViewModel(
            store,
            () => Array.Empty<SalesEmojiInventoryItem>(),
            _ => { },
            new ResourceLocalizationService(),
            () => confirmed);
        viewModel.SelectedMapping = Assert.Single(viewModel.Mappings);

        viewModel.DeleteSelectedCommand.Execute(null);

        Assert.Equal(expectedCount, viewModel.Mappings.Count);
    }

    [Fact]
    public void Manager_GroupsMultipleEmojiByFriendlyProductNameAndAppliesCatalog()
    {
        using var directory = new TemporaryDirectory();
        var inventory = new[]
        {
            new SalesEmojiInventoryItem("emoji-1", "GTA_Bunker", "guild", false, 3, false),
            new SalesEmojiInventoryItem("emoji-2", "BunkerPlate", "guild", false, 2, false),
        };
        SalesProductCatalog? applied = null;
        var viewModel = new ProductMappingManagerViewModel(
            new JsonSalesProductCatalogStore(directory.File("sales-products.json")),
            () => inventory,
            catalog => applied = catalog,
            new ResourceLocalizationService(SupportedLocales.Korean));

        foreach (var item in viewModel.Inventory.ToArray())
        {
            viewModel.SelectedInventory = item;
            viewModel.AddSelectedCommand.Execute(null);
            viewModel.SelectedMapping!.ProductName = "벙커";
            viewModel.CommitDraftCommand.Execute(null);
        }

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Equal(2, applied.Products.Count);
        Assert.Single(applied.Products.Select(product => product.ProductId).Distinct());
        Assert.All(applied.Products, product => Assert.Equal("벙커", product.GroupName));
    }

    [Fact]
    public void Manager_SearchAndUnmappedFilter_KeepLargeInventoryUsable()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSalesProductCatalogStore(directory.File("sales-products.json"));
        Assert.True(store.Save(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            new[] { Product("bunker", "100", "guild") with { GroupName = "Bunker" } })));
        var viewModel = new ProductMappingManagerViewModel(
            store,
            () => new[]
            {
                new SalesEmojiInventoryItem("100", "GTA_Bunker", "guild", false, 8, false),
                new SalesEmojiInventoryItem("200", "Capsule", "guild", false, 3, false),
            },
            _ => { },
            new ResourceLocalizationService());

        viewModel.ShowUnmappedOnly = true;
        Assert.Equal("Capsule", Assert.Single(viewModel.FilteredInventory.Cast<SalesEmojiInventoryItem>()).EmojiName);

        viewModel.ShowUnmappedOnly = false;
        viewModel.FilterText = "100";
        Assert.Equal("GTA_Bunker", Assert.Single(viewModel.FilteredInventory.Cast<SalesEmojiInventoryItem>()).EmojiName);
    }

    private static SalesProductDefinition Product(string product, string emoji, string guild) =>
        new(product, emoji, null, guild, new Dictionary<string, string>());
}
