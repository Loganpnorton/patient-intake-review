using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

/// <summary>
/// Returns Collapsed if any bound boolean is true; otherwise Visible.
/// Useful for hiding WPF UI when a modal is open (e.g., to work around WebView2 HWND z-order).
/// </summary>
public class AnyTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var anyTrue = values.OfType<bool>().Any(v => v);
        return anyTrue ? Visibility.Collapsed : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}




