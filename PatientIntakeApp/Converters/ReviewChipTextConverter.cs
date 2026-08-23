using System;
using System.Globalization;
using System.Windows.Data;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

public class ReviewChipTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isReviewed = values.Length > 0 && values[0] is bool b && b;
        var status = values.Length > 1 && values[1] is ReviewStatus rs ? rs : ReviewStatus.Pending;
        var source = values.Length > 2 && values[2] is FindingSource fs ? fs : FindingSource.Unknown;

        // Only show the "(AI)" / "(Local)" prefix while the finding still needs review.
        // Once the user clicks check/X (reviewed), we remove the prefix per UX request.
        var prefix = !isReviewed
            ? source switch
            {
                FindingSource.AI => "(AI) ",
                FindingSource.Local => "(Local) ",
                _ => ""
            }
            : "";

        if (!isReviewed)
        {
            return $"{prefix}Warning - Needs Review";
        }

        return status switch
        {
            ReviewStatus.Passed => $"{prefix}Reviewed - Passed Finding",
            ReviewStatus.Rejected => $"{prefix}Rejected - Disqualifying Keyword",
            _ => $"{prefix}Warning - Needs Review"
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


