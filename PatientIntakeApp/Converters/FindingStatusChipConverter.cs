using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

/// <summary>
/// Returns text/brush/border for the status chip (NEEDS REVIEW / CLEARED / FLAGGED).
/// parameter:
///  - "Text"
///  - "Background"
///  - "BorderBrush"
///  - "Foreground"
/// </summary>
public class FindingStatusChipConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(255, 193, 7));   // Amber 500
    private static readonly SolidColorBrush Green = new(Color.FromRgb(56, 142, 60));   // Green 600
    private static readonly SolidColorBrush Transparent = Brushes.Transparent;
    private static readonly SolidColorBrush White = Brushes.White;
    private static readonly SolidColorBrush Black = Brushes.Black;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isReviewed = values.Length > 0 && values[0] is bool b && b;
        var status = values.Length > 1 && values[1] is ReviewStatus rs ? rs : ReviewStatus.Pending;

        var mode = parameter as string ?? "Text";

        if (!isReviewed)
        {
            return mode switch
            {
                "Text" => "NEEDS REVIEW",
                "Background" => Transparent,
                "BorderBrush" => Amber,
                "Foreground" => Black,
                _ => "NEEDS REVIEW"
            };
        }

        return status switch
        {
            ReviewStatus.Passed => mode switch
            {
                "Text" => "CLEARED",
                "Background" => Green,
                "BorderBrush" => Green,
                "Foreground" => White,
                _ => "CLEARED"
            },
            ReviewStatus.Rejected => mode switch
            {
                "Text" => "FLAGGED",
                "Background" => Amber,
                "BorderBrush" => Amber,
                "Foreground" => Black,
                _ => "FLAGGED"
            },
            _ => mode switch
            {
                "Text" => "NEEDS REVIEW",
                "Background" => Transparent,
                "BorderBrush" => Amber,
                "Foreground" => Black,
                _ => "NEEDS REVIEW"
            }
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}





