using System;
using System.Globalization;
using System.Windows.Data;

namespace PatientIntakeApp.Converters;

/// <summary>
/// Normalizes a string for display inside quotes:
/// - trims leading/trailing whitespace
/// - strips a single matching pair of leading/trailing quotes (single or double)
/// - trims again
/// </summary>
public class NormalizeQuotedTextConverter : IValueConverter
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
        // Handle normal whitespace plus common invisible/padding chars that show up from PDF extraction.
        // Also treat Unicode separator/format/control categories as trim chars (covers thin spaces, NNBS, etc.).
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

        // Some extracted strings come wrapped in curly quotes with padding inside, e.g. “ John ”.
        // Strip any leading/trailing quote punctuation (straight or curly), then trim again.
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
        return new string(buf.Slice(0, j)).Trim();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;

        var s = (value as string) ?? value.ToString() ?? string.Empty;
        s = StripOuterQuotePunctuation(s);
        s = RemoveAllQuoteChars(s);

        if (s.Length >= 2)
        {
            var first = s[0];
            var last = s[^1];
            var isMatchingQuotes =
                (first == '"' && last == '"') ||
                (first == '\'' && last == '\'');

            if (isMatchingQuotes)
            {
                s = StripOuterQuotePunctuation(s.Substring(1, s.Length - 2));
                s = RemoveAllQuoteChars(s);
            }
        }

        return s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

