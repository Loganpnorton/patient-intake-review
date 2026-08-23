using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PatientIntakeApp.Converters;

public class OnlineDotBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var online = value is bool b && b;
        return online
            ? new SolidColorBrush(Color.FromRgb(56, 142, 60))   // green
            : new SolidColorBrush(Color.FromRgb(158, 158, 158)); // grey
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

