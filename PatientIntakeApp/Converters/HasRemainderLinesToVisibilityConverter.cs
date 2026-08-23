using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class HasRemainderLinesToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return Visibility.Collapsed;

        var lines = s
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .ToList();

        var firstIdx = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (firstIdx < 0) return Visibility.Collapsed;

        var remainder = lines
            .Skip(firstIdx + 1)
            .Any(l => !string.IsNullOrWhiteSpace(l));

        return remainder ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

