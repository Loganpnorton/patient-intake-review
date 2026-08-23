using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

public class ReviewChipBrushConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Amber500 = new(Color.FromRgb(255, 193, 7)); // Material Design Amber 500
    private static readonly SolidColorBrush Green600 = new(Color.FromRgb(56, 142, 60)); // Material Design Green 600
    private static readonly SolidColorBrush Red600 = new(Color.FromRgb(211, 47, 47)); // Material Design Red 600

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isReviewed = values.Length > 0 && values[0] is bool b && b;
        var status = values.Length > 1 && values[1] is ReviewStatus rs ? rs : ReviewStatus.Pending;

        if (!isReviewed)
        {
            return Amber500;
        }

        return status switch
        {
            ReviewStatus.Passed => Green600,
            ReviewStatus.Rejected => Red600,
            _ => Amber500
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}





