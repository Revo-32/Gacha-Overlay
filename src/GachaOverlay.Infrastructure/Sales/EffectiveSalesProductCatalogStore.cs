using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Infrastructure.Sales;

public sealed class EffectiveSalesProductCatalogStore : ISalesProductCatalogWorkspace
{
    private readonly JsonSalesProductCatalogStore _overrideStore;
    private readonly string _overridePath;
    private readonly string? _legacyPath;
    private readonly IAppLogger _logger;
    private SalesProductCatalog _overrides = SalesProductCatalog.Empty;
    private SalesProductCatalog _effective = SalesProductCatalog.Empty;

    public EffectiveSalesProductCatalogStore(
        SalesProductCatalog builtInCatalog,
        string overridePath,
        string? legacyPath = null,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builtInCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(overridePath);
        BuiltInCatalog = builtInCatalog;
        _overridePath = Path.GetFullPath(overridePath);
        _legacyPath = string.IsNullOrWhiteSpace(legacyPath)
            ? null
            : Path.GetFullPath(legacyPath);
        _logger = logger ?? NullAppLogger.Instance;
        _overrideStore = new JsonSalesProductCatalogStore(_overridePath, _logger);
        MigrateLegacyIfRequired();
        Reload();
    }

    public SalesProductCatalog BuiltInCatalog { get; }

    public SalesProductCatalog EffectiveCatalog => _effective;

    public int OverrideCount => _overrides.Products.Count;

    public bool BuiltInLoaded => BuiltInCatalog.Products.Count > 0;

    public bool SaveEffective(SalesProductCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SalesProductCatalog desired;
        try
        {
            desired = SalesProductCatalog.CreateValidated(document);
        }
        catch (InvalidDataException exception)
        {
            _logger.Warning("PRODUCT", $"Effective product catalog was rejected: {exception.Message}");
            return false;
        }

        var sparse = CreateSparseOverrides(desired);
        if (!_overrideStore.Save(ToDocument(sparse)))
        {
            return false;
        }

        _overrides = sparse;
        _effective = Merge(BuiltInCatalog, _overrides);
        return true;
    }

    public bool RestoreDefault(string? guildId, string emojiId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emojiId);
        var remaining = _overrides.Products
            .Where(product => !HasKey(product, guildId, emojiId))
            .ToArray();
        if (remaining.Length == _overrides.Products.Count)
        {
            return HasBuiltIn(guildId, emojiId);
        }

        if (!_overrideStore.Save(new SalesProductCatalogDocument(
                SalesProductCatalogDocument.CurrentVersion,
                remaining)))
        {
            return false;
        }

        _overrides = CreateCatalog(remaining);
        _effective = Merge(BuiltInCatalog, _overrides);
        return true;
    }

    public bool ResetOverrides()
    {
        if (!_overrideStore.Save(new SalesProductCatalogDocument(
                SalesProductCatalogDocument.CurrentVersion,
                Array.Empty<SalesProductDefinition>())))
        {
            return false;
        }

        _overrides = SalesProductCatalog.Empty;
        _effective = BuiltInCatalog;
        return true;
    }

    public SalesProductDefinitionSource GetSource(string? guildId, string emojiId)
    {
        var overridden = _overrides.Products.FirstOrDefault(product =>
            HasKey(product, guildId, emojiId));
        if (overridden is not null)
        {
            if (!overridden.Enabled)
            {
                return SalesProductDefinitionSource.Disabled;
            }

            return HasBuiltIn(guildId, emojiId)
                ? SalesProductDefinitionSource.Modified
                : SalesProductDefinitionSource.Custom;
        }

        return HasBuiltIn(guildId, emojiId)
            ? SalesProductDefinitionSource.BuiltIn
            : SalesProductDefinitionSource.Custom;
    }

    public bool Export(string exportPath, SalesProductCatalog catalog) =>
        _overrideStore.Export(exportPath, catalog);

    private void MigrateLegacyIfRequired()
    {
        if (File.Exists(_overridePath) || _legacyPath is null || !File.Exists(_legacyPath))
        {
            return;
        }

        var legacy = new JsonSalesProductCatalogStore(_legacyPath, _logger).Load();
        var sparse = CreateLegacyOverrides(legacy);
        if (_overrideStore.Save(ToDocument(sparse)))
        {
            _logger.Information(
                "PRODUCT",
                $"Legacy product catalog migrated to sparse overrides count={sparse.Products.Count}; legacy file preserved.");
        }
    }

    private void Reload()
    {
        _overrides = _overrideStore.Load();
        _effective = Merge(BuiltInCatalog, _overrides);
    }

    private SalesProductCatalog CreateSparseOverrides(SalesProductCatalog desired)
    {
        var desiredByKey = desired.Products.ToDictionary(Key, StringComparer.Ordinal);
        var sparse = new List<SalesProductDefinition>();
        foreach (var builtIn in BuiltInCatalog.Products)
        {
            if (!desiredByKey.Remove(Key(builtIn), out var current))
            {
                sparse.Add(builtIn with { Enabled = false });
                continue;
            }

            if (!SemanticallyEqual(builtIn, current))
            {
                sparse.Add(current);
            }
        }

        sparse.AddRange(desiredByKey.Values.OrderBy(Key, StringComparer.Ordinal));
        return CreateCatalog(sparse);
    }

    private SalesProductCatalog CreateLegacyOverrides(SalesProductCatalog legacy)
    {
        var builtInByKey = BuiltInCatalog.Products.ToDictionary(Key, StringComparer.Ordinal);
        var sparse = legacy.Products
            .Where(product =>
                !builtInByKey.TryGetValue(Key(product), out var builtIn) ||
                !SemanticallyEqual(builtIn, product))
            .ToArray();
        return CreateCatalog(sparse);
    }

    private static SalesProductCatalog Merge(
        SalesProductCatalog builtIn,
        SalesProductCatalog overrides)
    {
        var overrideByKey = overrides.Products.ToDictionary(Key, StringComparer.Ordinal);
        var merged = new List<SalesProductDefinition>(
            builtIn.Products.Count + overrides.Products.Count);
        foreach (var product in builtIn.Products)
        {
            if (overrideByKey.Remove(Key(product), out var replacement))
            {
                merged.Add(replacement);
            }
            else
            {
                merged.Add(product);
            }
        }

        merged.AddRange(overrideByKey.Values.OrderBy(Key, StringComparer.Ordinal));
        return CreateCatalog(merged);
    }

    private bool HasBuiltIn(string? guildId, string emojiId) =>
        BuiltInCatalog.Products.Any(product => HasKey(product, guildId, emojiId));

    private static bool HasKey(
        SalesProductDefinition product,
        string? guildId,
        string emojiId) =>
        string.Equals(product.GuildId ?? string.Empty, guildId ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(product.EmojiId, emojiId, StringComparison.Ordinal);

    private static string Key(SalesProductDefinition product) =>
        $"{product.GuildId ?? string.Empty}\u001f{product.EmojiId}";

    private static bool SemanticallyEqual(
        SalesProductDefinition left,
        SalesProductDefinition right) =>
        string.Equals(left.ProductId, right.ProductId, StringComparison.Ordinal) &&
        string.Equals(left.EmojiId, right.EmojiId, StringComparison.Ordinal) &&
        string.Equals(left.EmojiName, right.EmojiName, StringComparison.Ordinal) &&
        string.Equals(left.GuildId, right.GuildId, StringComparison.Ordinal) &&
        left.Enabled == right.Enabled &&
        string.Equals(left.GroupName, right.GroupName, StringComparison.Ordinal) &&
        left.DisplayNames.Count == right.DisplayNames.Count &&
        left.DisplayNames.All(pair =>
            right.DisplayNames.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static SalesProductCatalog CreateCatalog(
        IEnumerable<SalesProductDefinition> products) =>
        SalesProductCatalog.CreateValidated(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            products.ToArray()));

    private static SalesProductCatalogDocument ToDocument(SalesProductCatalog catalog) =>
        new(SalesProductCatalogDocument.CurrentVersion, catalog.Products);
}
