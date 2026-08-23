using System;
using System.Globalization;
using System.Windows.Data;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

public class ReviewStatusDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Finding finding)
        {
            if (!finding.IsReviewed)
            {
                // Not reviewed yet - show "Warning - Needs Review" in yellow
                return "Warning - Needs Review";
            }
            else
            {
                // Reviewed - show status based on ReviewStatus
                return finding.ReviewStatus switch
                {
                    ReviewStatus.Passed => "Reviewed - Passed Finding",
                    ReviewStatus.Rejected => "Rejected - Disqualifying Keyword",
                    _ => "Warning - Needs Review"
                };
            }
        }
        return "Warning - Needs Review";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}