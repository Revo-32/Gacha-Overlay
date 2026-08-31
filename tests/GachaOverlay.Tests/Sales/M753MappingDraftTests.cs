using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Sales;

public sealed class M753MappingDraftTests
{
    [Fact]
    public void CreateCommand_SelectsVisibleDraftImmediatelyWithoutCatalogMutation()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, _, path) = Create(directory);

        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);

        Assert.True(viewModel.IsDraftMapping);
        Assert.True(viewModel.HasSelectedMapping);
        Assert.Empty(viewModel.Mappings);
        Assert.False(File.Exists(path));
        Assert.Equal("100", viewModel.SelectedMapping!.EmojiId);
    }

    [Fact]
    public void CancelDraft_LeavesCatalogAndFileUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, _, path) = Create(directory);
        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);
        viewModel.SelectedMapping!.ProductName = "Bunker";

        viewModel.CancelDraftCommand.Execute(null);

        Assert.False(viewModel.IsDraftMapping);
        Assert.Empty(viewModel.Mappings);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveDraft_AtomicallyPersistsAndAppliesMapping()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, applied, path) = Create(directory);
        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);
        viewModel.SelectedMapping!.ProductName = "Bunker";

        viewModel.CommitDraftCommand.Execute(null);

        Assert.False(viewModel.IsDraftMapping);
        Assert.Single(viewModel.Mappings);
        Assert.True(File.Exists(path));
        Assert.Single(applied.Value!.Products);
    }

    [Fact]
    public void ExistingProductSelection_AttachesStableProductId()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("products.json");
        var store = new JsonSalesProductCatalogStore(path);
        Assert.True(store.Save(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            new[]
            {
                Definition("stable-bunker", "100", "Bunker"),
            })));
        var inventory = new[]
        {
            Item("100", "BunkerA"),
            Item("200", "BunkerB"),
        };
        var viewModel = new ProductMappingManagerViewModel(
            store,
            () => inventory,
            _ => { },
            new ResourceLocalizationService());
        viewModel.SelectedInventory = viewModel.Inventory.Single(item => item.EmojiId == "200");
        viewModel.AddSelectedCommand.Execute(null);

        viewModel.SelectedProductNameSuggestion = "Bunker";
        viewModel.CommitDraftCommand.Execute(null);

        Assert.Equal(
            new[] { "stable-bunker", "stable-bunker" },
            store.Load().Products.Select(product => product.ProductId));
    }

    [Fact]
    public void NewProduct_CreatesStableNonDisplayIdentity()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, _, _) = Create(directory);
        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);
        viewModel.SelectedMapping!.ProductName = "Night Club";

        viewModel.CommitDraftCommand.Execute(null);

        var mapping = Assert.Single(viewModel.Mappings);
        Assert.StartsWith("group-", mapping.ProductId, StringComparison.Ordinal);
        Assert.NotEqual(mapping.ProductName, mapping.ProductId);
    }

    [Fact]
    public void IncompleteDraft_IsNeverPersisted()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, applied, path) = Create(directory);
        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);

        viewModel.CommitDraftCommand.Execute(null);

        Assert.True(viewModel.IsDraftMapping);
        Assert.Empty(viewModel.Mappings);
        Assert.Null(applied.Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ChangingInventory_DiscardsUncommittedDraft()
    {
        using var directory = new TemporaryDirectory();
        var (viewModel, _, _) = Create(directory);
        viewModel.SelectedInventory = viewModel.Inventory[0];
        viewModel.AddSelectedCommand.Execute(null);
        viewModel.SelectedMapping!.ProductName = "Unsaved";

        viewModel.SelectedInventory = viewModel.Inventory[1];

        Assert.False(viewModel.IsDraftMapping);
        Assert.Null(viewModel.SelectedMapping);
        Assert.Empty(viewModel.Mappings);
    }

    [Fact]
    public void LegacyMappingsWithSameGroupName_MigrateToSharedProductIdentity()
    {
        var catalog = SalesProductCatalog.CreateValidated(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.LegacyVersion,
            new[]
            {
                Definition("old-a", "100", "Bunker"),
                Definition("old-b", "200", "Bunker"),
            }));

        Assert.Equal(2, catalog.Products.Count);
        Assert.Single(catalog.Products.Select(product => product.ProductId).Distinct());
        Assert.All(catalog.Products, product => Assert.Equal("Bunker", product.GroupName));
    }

    private static (
        ProductMappingManagerViewModel ViewModel,
        Box<SalesProductCatalog> Applied,
        string Path) Create(TemporaryDirectory directory)
    {
        var path = directory.File("products.json");
        var applied = new Box<SalesProductCatalog>();
        var inventory = new[] { Item("100", "BunkerA"), Item("200", "Nightclub") };
        return (
            new ProductMappingManagerViewModel(
                new JsonSalesProductCatalogStore(path),
                () => inventory,
                catalog => applied.Value = catalog,
                new ResourceLocalizationService(SupportedLocales.English)),
            applied,
            path);
    }

    private static SalesEmojiInventoryItem Item(string id, string name) =>
        new(id, name, "guild", false, 1, false);

    private static SalesProductDefinition Definition(string id, string emoji, string name) =>
        new(
            id,
            emoji,
            emoji,
            "guild",
            new Dictionary<string, string> { ["en"] = name },
            true,
            name);

    private sealed class Box<T>
    {
        public T? Value { get; set; }
    }
}
