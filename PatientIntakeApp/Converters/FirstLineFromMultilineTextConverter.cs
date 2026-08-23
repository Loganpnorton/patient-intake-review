using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

public class FirstLineFromMultilineTextConverter : IValueConverter
{
    private static bool IsQuoteChar(char c)
    {
        if (c == '"' || c == '\'') return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat == UnicodeCategory.InitialQuotePunctuation
               || cat == UnicodeCategory.FinalQuotePunctuation;
    }

    private static bool IsTrimChar(char c)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return char.IsWhiteSpace(c)
               || cat == UnicodeCategory.SpaceSeparator
               || cat == UnicodeCategory.LineSeparator
               || cat == UnicodeCategory.ParagraphSeparator
               || cat == UnicodeCategory.Format
               || cat == UnicodeCategory.Control
               || c == '\u00A0' /* NBSP */
               || c == '\u200B' /* ZWSP */
               || c == '\uFEFF' /* BOM */;
    }

    private static string TrimExtended(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var start = 0;
        var end = s.Length - 1;
        while (start <= end && IsTrimChar(s[start])) start++;
        while (end >= start && IsTrimChar(s[end])) end--;
        return start > end ? string.Empty : s.Substring(start, end - start + 1);
    }

    private static string StripOuterQuotePunctuation(string s)
    {
        s = TrimExtended(s);
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var start = 0;
        var end = s.Length - 1;

        while (start <= end && IsQuoteChar(s[start])) start++;
        while (end >= start && IsQuoteChar(s[end])) end--;

        s = start > end ? string.Empty : s.Substring(start, end - start + 1);
        return TrimExtended(s);
    }

    private static string RemoveAllQuoteChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var j = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (IsQuoteChar(c)) continue;
            buf[j++] = c;
        }
        return TrimExtended(new string(buf.Slice(0, j)));
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var lines = s
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var first = lines.FirstOrDefault() ?? string.Empty;

        // Normalize so the UI can safely wrap it in double-quotes without extra spaces or nested quotes.
        first = StripOuterQuotePunctuation(first);
        first = RemoveAllQuoteChars(first);
        if (first.Length >= 2)
        {
            var c0 = first[0];
            var c1 = first[^1];
            if ((c0 == '"' && c1 == '"') || (c0 == '\'' && c1 == '\''))
            {
                first = StripOuterQuotePunctuation(first.Substring(1, first.Length - 2));
                first = RemoveAllQuoteChars(first);
            }
        }

        return first;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

