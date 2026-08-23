using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class TrimStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var s = value as string ?? value.ToString() ?? string.Empty;
        return s.Trim();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

