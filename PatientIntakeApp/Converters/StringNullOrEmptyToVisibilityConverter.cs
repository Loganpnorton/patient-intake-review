using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    // Default: Visible when NOT null/empty, Collapsed when null/empty.
    // If ConverterParameter == "Inverse", behavior is inverted.
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        var isEmpty = string.IsNullOrWhiteSpace(s);
        var inverse = string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase);

        if (inverse) isEmpty = !isEmpty;
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

