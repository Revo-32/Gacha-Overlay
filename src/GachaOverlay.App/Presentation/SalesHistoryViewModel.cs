using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.App.Presentation;

internal sealed class SalesHistoryViewModel : IDisposable
{
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();
    private readonly ISalesHistoryStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<SalesHistoryProduct> _products;
    private bool _disposed;

    public SalesHistoryViewModel(
        ISalesHistoryStore store,
        SalesProductCatalog catalog,
        Dispatcher dispatcher,
        Func<bool> confirmReset)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(catalog);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(confirmReset);
        _products = catalog.Products
            .Where(product => product.Enabled)
            .GroupBy(product => product.ProductId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(product => new SalesHistoryProduct(
                product.ProductId,
                ResolveKoreanName(product)))
            .ToArray();
        ResetCommand = new RelayCommand(() =>
        {
            if (confirmReset())
            {
                _store.Clear();
            }
        });
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    public ObservableCollection<SalesHistoryRowViewModel> Rows { get; } = new();

    public ICommand ResetCommand { get; }

    private void OnStoreChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var history = _store.Snapshot()
            .ToDictionary(entry => entry.ProductId, entry => entry.LastSoldAt, StringComparer.Ordinal);
        Rows.Clear();
        foreach (var product in _products)
        {
            history.TryGetValue(product.ProductId, out var soldAt);
            Rows.Add(new SalesHistoryRowViewModel(
                product.ProductId,
                product.DisplayName,
                soldAt == default ? "기록 없음" : FormatLocalTime(soldAt)));
        }
    }

    internal static string FormatLocalTime(DateTimeOffset soldAt, DateTimeOffset? now = null)
    {
        var local = TimeZoneInfo.ConvertTime(soldAt, KoreaTimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, KoreaTimeZone);
        if (local.Date == localNow.Date)
        {
            return $"오늘 {local:HH:mm}";
        }

        if (local.Date == localNow.Date.AddDays(-1))
        {
            return $"어제 {local:HH:mm}";
        }

        return local.Year == localNow.Year
            ? $"{local.Month}월 {local.Day}일 {local:HH:mm}"
            : $"{local.Year}년 {local.Month}월 {local.Day}일 {local:HH:mm}";
    }

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "KST",
                TimeSpan.FromHours(9),
                "Korea Standard Time",
                "Korea Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "KST",
                TimeSpan.FromHours(9),
                "Korea Standard Time",
                "Korea Standard Time");
        }
    }

    private static string ResolveKoreanName(SalesProductDefinition product)
    {
        if (product.DisplayNames.TryGetValue("ko", out var korean) &&
            !string.IsNullOrWhiteSpace(korean))
        {
            return korean;
        }

        return !string.IsNullOrWhiteSpace(product.GroupName)
            ? product.GroupName
            : !string.IsNullOrWhiteSpace(product.EmojiName)
                ? product.EmojiName
                : product.ProductId;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }

    private sealed record SalesHistoryProduct(string ProductId, string DisplayName);
}

internal sealed record SalesHistoryRowViewModel(
    string ProductId,
    string DisplayName,
    string LastSoldText);
