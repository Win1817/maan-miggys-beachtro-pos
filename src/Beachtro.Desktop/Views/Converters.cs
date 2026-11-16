using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Beachtro.Desktop.Views;

/// <summary>Integer minor units → "1,234.50" (display only).</summary>
public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long minor ? Formatting.Money(minor) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Quantity Q (thousandths) → units.</summary>
public sealed class UnitsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long q ? Formatting.Units(q) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>UTC Unix milliseconds → local "yyyy-MM-dd HH:mm".</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long ms ? Formatting.LocalTime(ms) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Non-empty string or non-null object → Visible.</summary>
public sealed class PresentToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            int i => i > 0,
            _ => true,
        };
        if (parameter is "invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
