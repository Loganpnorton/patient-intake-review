using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

/// <summary>
/// Maps SeverityLevel to chip text/brush.
/// parameter:
///  - "Text"
///  - "Background"
///  - "Foreground"
/// </summary>
public class SeverityChipConverter : IValueConverter
{
    // Material-ish palette (consistent with existing chip colors)
    private static readonly SolidColorBrush Red = new(Color.FromRgb(211, 47, 47));     // Red 700
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(255, 193, 7));  // Amber 500
    private static readonly SolidColorBrush Green = new(Color.FromRgb(56, 142, 60));  // Green 600

    private static readonly SolidColorBrush White = Brushes.White;
    private static readonly SolidColorBrush Black = Brushes.Black;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var sev = value is SeverityLevel s ? s : SeverityLevel.Yellow;
        var mode = parameter as string ?? "Text";

        return sev switch
        {
            SeverityLevel.Red => mode switch
            {
                "Text" => "RED",
                "Background" => Red,
                "Foreground" => White,
                _ => "RED"
            },
            SeverityLevel.Green => mode switch
            {
                "Text" => "GREEN",
                "Background" => Green,
                "Foreground" => White,
                _ => "GREEN"
            },
            _ => mode switch
            {
                "Text" => "YELLOW",
                "Background" => Amber,
                "Foreground" => Black,
                _ => "YELLOW"
            }
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

