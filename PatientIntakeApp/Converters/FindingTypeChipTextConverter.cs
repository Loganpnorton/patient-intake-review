using System;
using System.Globalization;
using System.Windows.Data;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Converters;

/// <summary>
/// Produces a chip label like "LOCAL WARNING" or "AI WARNING" from Finding.Source + Finding.Severity.
/// </summary>
public class FindingTypeChipTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var source = values.Length > 0 && values[0] is FindingSource fs ? fs : FindingSource.Unknown;
        var severity = values.Length > 1 && values[1] is SeverityLevel sl ? sl : SeverityLevel.Yellow;

        var sourceText = source switch
        {
            FindingSource.Local => "LOCAL",
            FindingSource.AI => "AI",
            _ => "FLAG"
        };

        var severityText = severity switch
        {
            SeverityLevel.Red => "RED",
            SeverityLevel.Yellow => "YELLOW",
            SeverityLevel.Green => "GREEN",
            _ => "YELLOW"
        };

        return $"{sourceText} {severityText}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}





