using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public partial class PairedSingleQuotesToDoubleQuotesConverter : IValueConverter
{
    // Replaces paired single-quotes like 'John' -> "John" (does not affect contractions like don't).
    private static readonly Regex PairedQuotes = new Regex(@"'([^']+)'", RegexOptions.Compiled);

    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        return PairedQuotes.Replace(s, "\"$1\"");
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

