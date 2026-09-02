using System.Globalization;
using System.Windows.Data;

namespace GachaOverlay.App.Presentation;

public sealed class SliderValueTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IConvertible convertible)
        {
            return string.Empty;
        }

        var number = convertible.ToDouble(CultureInfo.InvariantCulture);
        return (parameter as string) switch
        {
            "Percent" => number.ToString("P0", culture),
            "Percent100" => $"{number.ToString("0", culture)}%",
            "Points" => $"{number.ToString("0.0", culture)} pt",
            "Dip" => $"{number.ToString("0", culture)} DIP",
            "DipDecimal" => $"{number.ToString("0.00", culture)} DIP",
            "Decimal" => number.ToString("0.00", culture),
            _ => number.ToString("0.00", culture),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
