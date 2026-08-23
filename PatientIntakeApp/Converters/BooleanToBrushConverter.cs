using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PatientIntakeApp.Converters;

public class BooleanToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            if (parameter as string == "FalseFlag")
            {
                // For false flags, use a different color (orange)
                return boolValue ? new SolidColorBrush(Color.FromRgb(255, 140, 0)) : new SolidColorBrush(Color.FromRgb(211, 47, 47));
            }

            // Default: red for true, transparent for false
            return boolValue ? new SolidColorBrush(Color.FromRgb(211, 47, 47)) : Brushes.Transparent;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}