using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class UtcToLocalDateTimeStringConverter : IValueConverter
{
    // If EF returns Kind=Unspecified for UTC-stored datetimes, treat as UTC and display in local time.
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return string.Empty;

        var assumedUtc = dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt;

        var local = assumedUtc.ToLocalTime();
        return local.ToString("g", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

