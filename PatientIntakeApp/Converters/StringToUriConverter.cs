using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class StringToUriConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
        {
            try
            {
                return new Uri(path);
            }
            catch
            {
                return new Uri("about:blank");
            }
        }
        return new Uri("about:blank");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Uri uri)
        {
            return uri.AbsoluteUri;
        }
        return string.Empty;
    }
}


