using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class RemainderLinesFromMultilineTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var lines = s
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => (l ?? string.Empty).TrimEnd())
            .ToList();

        // Find the first non-empty line, then return everything after it (trim leading empties).
        var firstIdx = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (firstIdx < 0) return string.Empty;

        var remainder = lines
            .Skip(firstIdx + 1)
            .ToList();

        while (remainder.Count > 0 && string.IsNullOrWhiteSpace(remainder[0]))
            remainder.RemoveAt(0);

        var result = string.Join(Environment.NewLine, remainder).Trim();
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

