using System;
using System.Globalization;
using System.Windows.Data;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

public class SeverityDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SeverityLevel severity)
        {
            return severity switch
            {
                SeverityLevel.Green => "Green",
                SeverityLevel.Yellow => "Yellow - Needs Review",
                SeverityLevel.Red => "Red - High Priority",
                _ => value.ToString() ?? string.Empty
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}