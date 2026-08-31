using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Data;
using System.IO;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Sales;

namespace GachaOverlay.App.Presentation;

internal sealed class ProductMappingManagerViewModel : INotifyPropertyChanged
{
    private readonly ISalesProductCatalogWorkspace _store;
    private readonly Func<IReadOnlyList<SalesEmojiInventoryItem>> _getInventory;
    private readonly Action<SalesProductCatalog> _applyCatalog;
    private readonly Func<bool> _confirmDelete;
    private SalesEmojiInventoryItem? _selectedInventory;
    private ProductMappingRow? _selectedMapping;
    private bool _isDraftMapping;
    private string? _selectedProductNameSuggestion;
    private string _filterText = string.Empty;
    private bool _showUnmappedOnly;
    private string _status = string.Empty;

    public ProductMappingManagerViewModel(
        ISalesProductCatalogWorkspace store,
        Func<IReadOnlyList<SalesEmojiInventoryItem>> getInventory,
        Action<SalesProductCatalog> applyCatalog,
        ILocalizationService localization,
        Func<bool>? confirmDelete = null)
    {
        _store = store;
        _getInventory = getInventory;
        _applyCatalog = applyCatalog;
        _confirmDelete = confirmDelete ?? (() => true);
        Localization = localization;
        FilteredInventory = CollectionViewSource.GetDefaultView(Inventory);
        FilteredInventory.Filter = MatchesFilter;
        AddSelectedCommand = new RelayCommand(AddSelected);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        RefreshCommand = new RelayCommand(Refresh);
        SaveCommand = new RelayCommand(Save);
        CommitDraftCommand = new RelayCommand(CommitDraft);
        CancelDraftCommand = new RelayCommand(CancelDraft);
        RestoreDefaultCommand = new RelayCommand(RestoreDefault);
        Load();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ILocalizationService Localization { get; }

    public ObservableCollection<SalesEmojiInventoryItem> Inventory { get; } = new();

    public ObservableCollection<ProductMappingRow> Mappings { get; } = new();

    public ObservableCollection<string> ProductNameSuggestions { get; } = new();

    public ICollectionView FilteredInventory { get; }

    public ICommand AddSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CommitDraftCommand { get; }

    public ICommand CancelDraftCommand { get; }

    public ICommand RestoreDefaultCommand { get; }

    public SalesEmojiInventoryItem? SelectedInventory
    {
        get => _selectedInventory;
        set
        {
            if (ReferenceEquals(_selectedInventory, value))
            {
                return;
            }

            if (IsDraftMapping)
            {
                ClearDraft();
            }

            _selectedInventory = value;
            SetSelectedMapping(
                value is null
                    ? null
                    : Mappings.FirstOrDefault(mapping => Matches(mapping, value)),
                isDraft: false);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedInventory));
            OnPropertyChanged(nameof(CanCreateMapping));
        }
    }

    public ProductMappingRow? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            SetSelectedMapping(value, isDraft: false);
        }
    }

    public bool HasSelectedInventory => SelectedInventory is not null;

    public bool HasSelectedMapping => SelectedMapping is not null;

    public bool CanCreateMapping => HasSelectedInventory && !HasSelectedMapping;

    public bool CanRestoreDefault => SelectedMapping?.CanRestoreDefault == true;

    public bool IsDraftMapping
    {
        get => _isDraftMapping;
        private set
        {
            if (_isDraftMapping == value)
            {
                return;
            }

            _isDraftMapping = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedProductNameSuggestion
    {
        get => _selectedProductNameSuggestion;
        set
        {
            _selectedProductNameSuggestion = value;
            if (SelectedMapping is not null && !string.IsNullOrWhiteSpace(value))
            {
                SelectedMapping.ProductName = value;
                var existing = Mappings.FirstOrDefault(mapping =>
                    string.Equals(
                        mapping.ProductName.Trim(),
                        value.Trim(),
                        StringComparison.CurrentCultureIgnoreCase));
                if (existing is not null)
                {
                    SelectedMapping.ProductId = existing.ProductId;
                }
            }

            OnPropertyChanged();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value ?? string.Empty;
            OnPropertyChanged();
            FilteredInventory.Refresh();
        }
    }

    public bool ShowUnmappedOnly
    {
        get => _showUnmappedOnly;
        set
        {
            _showUnmappedOnly = value;
            OnPropertyChanged();
            FilteredInventory.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    private void Load()
    {
        Mappings.Clear();
        foreach (var product in _store.EffectiveCatalog.Products)
        {
            Mappings.Add(ProductMappingRow.From(
                product,
                Localization.CurrentLocale,
                _store.GetSource(product.GuildId, product.EmojiId),
                ResolveSourceText(_store.GetSource(product.GuildId, product.EmojiId))));
        }

        RefreshProductNameSuggestions();
        Refresh();
    }

    private void Refresh()
    {
        Inventory.Clear();
        var inventory = _getInventory().ToList();
        foreach (var mapping in Mappings.Where(mapping => !inventory.Any(item => Matches(mapping, item))))
        {
            inventory.Add(new SalesEmojiInventoryItem(
                mapping.EmojiId,
                mapping.EmojiName,
                mapping.GuildId ?? string.Empty,
                false,
                0,
                true));
        }

        foreach (var item in inventory
                     .OrderBy(item => item.IsMapped)
                     .ThenByDescending(item => item.UsageCount)
                     .ThenBy(item => item.EmojiName, StringComparer.CurrentCultureIgnoreCase))
        {
            var mapping = Mappings.FirstOrDefault(candidate => Matches(candidate, item));
            Inventory.Add(item with
            {
                IsMapped = mapping is not null,
                SourceText = mapping?.SourceText,
            });
        }

        FilteredInventory.Refresh();
    }

    private void AddSelected()
    {
        if (SelectedInventory is null)
        {
            Status = Localization["SettingsProductSelectEmoji"];
            return;
        }

        if (Mappings.Any(mapping =>
                string.Equals(mapping.EmojiId, SelectedInventory.EmojiId, StringComparison.Ordinal) &&
                string.Equals(mapping.GuildId ?? string.Empty, SelectedInventory.GuildId, StringComparison.Ordinal)))
        {
            Status = Localization["SettingsProductDuplicate"];
            return;
        }

        var mapping = new ProductMappingRow
        {
            CurrentLocale = Localization.CurrentLocale,
            EmojiId = SelectedInventory.EmojiId,
            EmojiName = SelectedInventory.EmojiName,
            GuildId = SelectedInventory.GuildId,
            Enabled = true,
            ProductName = string.Empty,
        };
        SetSelectedMapping(mapping, isDraft: true);
        Status = string.Empty;
    }

    private void CommitDraft()
    {
        if (!IsDraftMapping || SelectedMapping is null)
        {
            return;
        }

        var productName = NormalizeGroup(SelectedMapping.ProductName);
        if (productName.Length == 0)
        {
            Status = Localization["SettingsProductInvalid"];
            return;
        }

        var existingGroup = Mappings.FirstOrDefault(mapping =>
            string.Equals(
                NormalizeGroup(mapping.ProductName),
                productName,
                StringComparison.CurrentCultureIgnoreCase));
        SelectedMapping.ProductId = existingGroup?.ProductId ??
            (string.IsNullOrWhiteSpace(SelectedMapping.ProductId)
                ? CreateProductId(productName)
                : SelectedMapping.ProductId.Trim());
        var draft = SelectedMapping;
        Mappings.Add(draft);
        SetSelectedMapping(draft, isDraft: false);
        if (!TryPersistMappings())
        {
            Mappings.Remove(draft);
            SetSelectedMapping(draft, isDraft: true);
            return;
        }

        RefreshProductNameSuggestions();
        Refresh();
    }

    private void CancelDraft()
    {
        if (!IsDraftMapping)
        {
            return;
        }

        ClearDraft();
        if (SelectedInventory is not null)
        {
            SetSelectedMapping(
                Mappings.FirstOrDefault(mapping => Matches(mapping, SelectedInventory)),
                isDraft: false);
        }

        Status = string.Empty;
    }

    private void DeleteSelected()
    {
        if (SelectedMapping is not null && _confirmDelete())
        {
            if (SelectedMapping.Source is SalesProductDefinitionSource.BuiltIn or
                SalesProductDefinitionSource.Modified or SalesProductDefinitionSource.Disabled)
            {
                SelectedMapping.Enabled = false;
                TryPersistMappings();
            }
            else
            {
                Mappings.Remove(SelectedMapping);
                SelectedMapping = null;
            }

            RefreshProductNameSuggestions();
            Refresh();
        }
    }

    private void RestoreDefault()
    {
        if (SelectedMapping is not { CanRestoreDefault: true } selected ||
            !_store.RestoreDefault(selected.GuildId, selected.EmojiId))
        {
            return;
        }

        var key = (selected.GuildId, selected.EmojiId);
        _applyCatalog(_store.EffectiveCatalog);
        Load();
        SelectedMapping = Mappings.FirstOrDefault(mapping =>
            string.Equals(mapping.GuildId, key.GuildId, StringComparison.Ordinal) &&
            string.Equals(mapping.EmojiId, key.EmojiId, StringComparison.Ordinal));
        Status = Localization["SettingsProductDefaultRestored"];
    }

    private void Save() => TryPersistMappings();

    private bool TryPersistMappings()
    {
        try
        {
            ApplySelectedGroupNameToSharedProduct();
            var groupIds = BuildGroupIds();
            var document = new SalesProductCatalogDocument(
                SalesProductCatalogDocument.CurrentVersion,
                Mappings.Select(mapping => mapping.ToDefinition(groupIds[NormalizeGroup(mapping.ProductName)]))
                    .ToArray());
            var catalog = SalesProductCatalog.CreateValidated(document);
            if (!_store.SaveEffective(document))
            {
                Status = Localization["SettingsProductSaveFailed"];
                return false;
            }

            catalog = _store.EffectiveCatalog;
            _applyCatalog(catalog);
            RefreshSources();
            Refresh();
            Status = Localization["SettingsProductSaved"];
            return true;
        }
        catch (InvalidDataException)
        {
            Status = Localization["SettingsProductInvalid"];
            return false;
        }
    }

    private void SetSelectedMapping(ProductMappingRow? value, bool isDraft)
    {
        _selectedMapping = value;
        IsDraftMapping = isDraft;
        _selectedProductNameSuggestion = null;
        OnPropertyChanged(nameof(SelectedMapping));
        OnPropertyChanged(nameof(SelectedProductNameSuggestion));
        OnPropertyChanged(nameof(HasSelectedMapping));
        OnPropertyChanged(nameof(CanCreateMapping));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    private void ClearDraft()
    {
        if (!IsDraftMapping)
        {
            return;
        }

        SetSelectedMapping(null, isDraft: false);
    }

    private Dictionary<string, string> BuildGroupIds()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in Mappings)
        {
            var key = NormalizeGroup(mapping.ProductName);
            if (key.Length == 0)
            {
                throw new InvalidDataException("Product name is required.");
            }

            if (!result.ContainsKey(key))
            {
                result[key] = string.IsNullOrWhiteSpace(mapping.ProductId)
                    ? CreateProductId(key)
                    : mapping.ProductId.Trim();
            }
        }

        return result;
    }

    private void ApplySelectedGroupNameToSharedProduct()
    {
        if (IsDraftMapping || SelectedMapping is null ||
            string.IsNullOrWhiteSpace(SelectedMapping.ProductId))
        {
            return;
        }

        foreach (var mapping in Mappings.Where(mapping =>
                     string.Equals(
                         mapping.ProductId,
                         SelectedMapping.ProductId,
                         StringComparison.Ordinal)))
        {
            mapping.ProductName = SelectedMapping.ProductName;
            mapping.EnglishName = SelectedMapping.EnglishName;
            mapping.KoreanName = SelectedMapping.KoreanName;
            mapping.JapaneseName = SelectedMapping.JapaneseName;
        }
    }

    private void RefreshSources()
    {
        foreach (var mapping in Mappings)
        {
            mapping.Source = _store.GetSource(mapping.GuildId, mapping.EmojiId);
            mapping.SourceText = ResolveSourceText(mapping.Source);
        }

        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    private string ResolveSourceText(SalesProductDefinitionSource source) => source switch
    {
        SalesProductDefinitionSource.BuiltIn => Localization["SettingsProductSourceBuiltIn"],
        SalesProductDefinitionSource.Modified => Localization["SettingsProductSourceModified"],
        SalesProductDefinitionSource.Disabled => Localization["SettingsProductSourceDisabled"],
        _ => Localization["SettingsProductSourceCustom"],
    };

    private void RefreshProductNameSuggestions()
    {
        ProductNameSuggestions.Clear();
        foreach (var name in Mappings
                     .Select(mapping => mapping.ProductName.Trim())
                     .Where(name => name.Length > 0)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            ProductNameSuggestions.Add(name);
        }
    }

    private bool MatchesFilter(object candidate)
    {
        if (candidate is not SalesEmojiInventoryItem item || ShowUnmappedOnly && item.IsMapped)
        {
            return false;
        }

        var filter = FilterText.Trim();
        return filter.Length == 0 ||
            item.EmojiName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            item.EmojiId.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(ProductMappingRow mapping, SalesEmojiInventoryItem item) =>
        string.Equals(mapping.EmojiId, item.EmojiId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(mapping.GuildId) ||
         string.Equals(mapping.GuildId, item.GuildId, StringComparison.Ordinal));

    private static string NormalizeGroup(string? name) => name?.Trim() ?? string.Empty;

    private static string CreateProductId(string groupName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(groupName.ToUpperInvariant()));
        return $"group-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class ProductMappingRow : INotifyPropertyChanged
{
    private string _productId = string.Empty;
    private string _emojiId = string.Empty;
    private string _emojiName = string.Empty;
    private string? _guildId;
    private bool _enabled = true;
    private string _englishName = string.Empty;
    private string _koreanName = string.Empty;
    private string _japaneseName = string.Empty;
    private string _productName = string.Empty;
    private SalesProductDefinitionSource _source = SalesProductDefinitionSource.Custom;
    private string _sourceText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProductId { get => _productId; set => SetField(ref _productId, value); }
    public string EmojiId { get => _emojiId; set => SetField(ref _emojiId, value); }
    public string EmojiName { get => _emojiName; set => SetField(ref _emojiName, value); }
    public string? GuildId { get => _guildId; set => SetField(ref _guildId, value); }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
    public string EnglishName { get => _englishName; set => SetField(ref _englishName, value); }
    public string KoreanName { get => _koreanName; set => SetField(ref _koreanName, value); }
    public string JapaneseName { get => _japaneseName; set => SetField(ref _japaneseName, value); }
    public string ProductName { get => _productName; set => SetField(ref _productName, value); }
    public SalesProductDefinitionSource Source
    {
        get => _source;
        set
        {
            if (SetField(ref _source, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRestoreDefault)));
            }
        }
    }
    public string SourceText { get => _sourceText; set => SetField(ref _sourceText, value); }
    public bool CanRestoreDefault => Source is SalesProductDefinitionSource.Modified or
        SalesProductDefinitionSource.Disabled;
    public string CurrentLocale { get; init; } = SupportedLocales.English;

    public static ProductMappingRow From(
        SalesProductDefinition product,
        string? locale = null,
        SalesProductDefinitionSource source = SalesProductDefinitionSource.Custom,
        string? sourceText = null) => new()
        {
            CurrentLocale = SupportedLocales.NormalizeOrEnglish(locale),
            ProductId = product.ProductId,
            EmojiId = product.EmojiId,
            EmojiName = product.EmojiName ?? string.Empty,
            GuildId = product.GuildId,
            Enabled = product.Enabled,
            EnglishName = GetName(product, SupportedLocales.English),
            KoreanName = GetName(product, SupportedLocales.Korean),
            JapaneseName = GetName(product, SupportedLocales.Japanese),
            ProductName = ResolveProductName(product, locale),
            Source = source,
            SourceText = sourceText ?? source.ToString(),
        };

    public SalesProductDefinition ToDefinition(string? groupedProductId = null)
    {
        var emojiId = EmojiId.Trim();
        var productId = string.IsNullOrWhiteSpace(groupedProductId)
            ? string.IsNullOrWhiteSpace(ProductId) ? $"emoji-{emojiId}" : ProductId.Trim()
            : groupedProductId.Trim();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddName(names, SupportedLocales.English, EnglishName);
        AddName(names, SupportedLocales.Korean, KoreanName);
        AddName(names, SupportedLocales.Japanese, JapaneseName);
        if (!string.IsNullOrWhiteSpace(ProductName))
        {
            names[SupportedLocales.NormalizeOrEnglish(CurrentLocale)] = ProductName.Trim();
        }
        return new SalesProductDefinition(
            productId,
            emojiId,
            NullIfBlank(EmojiName),
            NullIfBlank(GuildId),
            names,
            Enabled,
            NullIfBlank(ProductName));
    }

    private static string ResolveProductName(SalesProductDefinition product, string? locale)
    {
        var normalized = SupportedLocales.NormalizeOrEnglish(locale);
        return product.DisplayNames.TryGetValue(normalized, out var localized) &&
            !string.IsNullOrWhiteSpace(localized)
                ? localized
                : product.GroupName
                    ?? product.DisplayNames.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? product.EmojiName
                    ?? string.Empty;
    }

    private static string GetName(SalesProductDefinition product, string locale) =>
        product.DisplayNames.TryGetValue(locale, out var value) ? value : string.Empty;

    private static void AddName(IDictionary<string, string> names, string locale, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            names[locale] = value.Trim();
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
