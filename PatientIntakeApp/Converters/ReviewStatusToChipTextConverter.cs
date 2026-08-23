using PatientIntakeApp.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class ReviewStatusToChipTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ReviewStatus status)
        {
            return status switch
            {
                ReviewStatus.Passed => "Passed",
                ReviewStatus.Rejected => "Flagged",
                _ => "Needs Review"
            };
        }

        return "Needs Review";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

