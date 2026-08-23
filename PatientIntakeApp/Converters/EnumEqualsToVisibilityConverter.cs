using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter == null) return Visibility.Collapsed;
        if (value == null) return Visibility.Collapsed;

        var expected = parameter.ToString();
        var actual = value.ToString();
        var isMatch = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        // Allow use for both Visibility and bool targets (e.g., binding to IsEnabled).
        if (targetType == typeof(bool) || targetType == typeof(bool?))
        {
            return isMatch;
        }

        return isMatch ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}


