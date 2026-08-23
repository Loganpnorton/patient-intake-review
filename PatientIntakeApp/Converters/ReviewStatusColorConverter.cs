using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

public class ReviewStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Finding finding)
        {
            if (!finding.IsReviewed)
            {
                // Not reviewed yet - yellow color
                return new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Material Design Amber 500
            }
            else
            {
                // Reviewed - color based on ReviewStatus
                return finding.ReviewStatus switch
                {
                    ReviewStatus.Passed => new SolidColorBrush(Color.FromRgb(56, 142, 60)), // Material Design Green 600
                    ReviewStatus.Rejected => new SolidColorBrush(Color.FromRgb(211, 47, 47)), // Material Design Red 600
                    _ => new SolidColorBrush(Color.FromRgb(255, 193, 7)) // Default to yellow
                };
            }
        }
        return new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Default to yellow
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}