using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class NullableGuidToYesNoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Guid g && g != Guid.Empty) return "Yes";
        return "No";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

