using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Geom;
using iText.Kernel.Colors;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Forms;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Services;

public interface IPdfProcessingService
{
    int GetPageCount(string filePath);
    List<PageContent> ExtractText(string filePath);
    string HighlightTerms(string filePath, List<string> terms);
    string HighlightTerms(string filePath, List<string> redTerms, List<string> purpleTerms);
    string HighlightTerms(string filePath, List<string> redTerms, Dictionary<int, List<string>> purpleTermsByPage);
    string CreateSubsetPdf(string filePath, IEnumerable<int> pageNumbers);
}

public class PdfProcessingService : IPdfProcessingService
{
    private void Log(string message)
    {
        try
        {
            File.AppendAllText("debug_log.txt", $"{DateTime.Now}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    public int GetPageCount(string filePath)
    {
        if (!File.Exists(filePath)) return 0;
        try
        {
            using (var reader = new PdfReader(filePath))
            using (var document = new PdfDocument(reader))
            {
                return document.GetNumberOfPages();
            }
        }
        catch (Exception ex)
        {
            Log($"Error counting pages: {ex.Message}");
            return 0;
        }
    }

    public List<PageContent> ExtractText(string filePath)
    {
        Log($"Extracting text from {filePath}");
        var results = new List<PageContent>();

        if (!File.Exists(filePath))
            return results;

        // Flatten AcroForm fields on the source document before extraction.
        // CopyPagesTo() does not transfer the document-level AcroForm dictionary,
        // so we must flatten on the source.  We open in stamp mode (PdfReader +
        // PdfWriter), flatten, and use the flattened copy for extraction.
        var workFilePath = filePath;
        var tempFlattenedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Flattened_{Guid.NewGuid()}.pdf");

        try
        {
            using (var reader = new PdfReader(filePath))
            using (var writer = new PdfWriter(tempFlattenedPath))
            using (var srcPdf = new PdfDocument(reader, writer))
            {
                var form = PdfAcroForm.GetAcroForm(srcPdf, false);
                if (form != null)
                {
                    form.FlattenFields();
                    Log("Flattened AcroForm fields on source document.");
                }
                // The flattened content is written to tempFlattenedPath when srcPdf closes.
            }

            // If flattening created a temp file, switch to it.  Otherwise clean up.
            if (File.Exists(tempFlattenedPath))
                workFilePath = tempFlattenedPath;
            else
                Log("Temp flattened file was not created; using original.");

            using (var reader = new PdfReader(workFilePath))
            using (var document = new PdfDocument(reader))
            {
                for (int i = 1; i <= document.GetNumberOfPages(); i++)
                {
                    var page = document.GetPage(i);
                    var text = PdfTextExtractor.GetTextFromPage(page);
                    Log($"Page {i} text length: {text.Length}");
                    if (text.Length > 0)
                        Log($"Page {i} snippet: {text.Substring(0, Math.Min(text.Length, 100))}");
                    else
                        Log($"Page {i} is empty.");

                    results.Add(new PageContent
                    {
                        PageNumber = i,
                        Text = text,
                        PagePdfBytes = TryExtractSinglePagePdfBytes(document, i)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error processing PDF: {ex.Message}");
        }
        finally
        {
            if (workFilePath != filePath && File.Exists(workFilePath))
            {
                try { File.Delete(workFilePath); }
                catch (Exception ex) { Log($"Failed to delete temporary flattened PDF: {ex.Message}"); }
            }
        }

        return results;
    }

    private byte[]? TryExtractSinglePagePdfBytes(PdfDocument sourceDocument, int pageNumber)
    {
        try
        {
            using var ms = new MemoryStream();
            using var writer = new PdfWriter(ms);
            using var dest = new PdfDocument(writer);

            // Copy a single page into a new 1-page PDF.
            // The source document has already been flattened in ExtractText(), so
            // checkmark appearance streams are burned into the static page content.
            sourceDocument.CopyPagesTo(pageNumber, pageNumber, dest);
            dest.Close();

            var bytes = ms.ToArray();
            Log($"Extracted 1-page PDF for page {pageNumber}. Bytes={bytes.Length}");
            return bytes;
        }
        catch (Exception ex)
        {
            Log($"Failed to extract 1-page PDF for page {pageNumber}: {ex.Message}");
            return null;
        }
    }

    public string HighlightTerms(string filePath, List<string> terms)
    {
        return HighlightTerms(filePath, terms, new List<string>());
    }

    public string HighlightTerms(string filePath, List<string> redTerms, List<string> purpleTerms)
    {
        var byPage = new Dictionary<int, List<string>>();
        // Back-compat: apply purple terms to all pages (previous behavior)
        // Callers that need page-scoped highlights should use the Dictionary overload.
        if (purpleTerms != null && purpleTerms.Any())
        {
            byPage[-1] = purpleTerms;
        }
        return HighlightTerms(filePath, redTerms, byPage);
    }

    public string HighlightTerms(string filePath, List<string> redTerms, Dictionary<int, List<string>> purpleTermsByPage)
    {
        Log($"Highlighting terms in {filePath}...");
        redTerms ??= new List<string>();
        purpleTermsByPage ??= new Dictionary<int, List<string>>();

        var hasPurple = purpleTermsByPage.Any(kv => kv.Value != null && kv.Value.Any());
        if ((!redTerms.Any() && !hasPurple) || !File.Exists(filePath))
        {
            Log("No terms to highlight or file not found.");
            return filePath;
        }

        try
        {
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Analysis_{Guid.NewGuid()}.pdf");
            Log($"Temp path created: {tempPath}");

            using (var reader = new PdfReader(filePath))
            using (var writer = new PdfWriter(tempPath))
            using (var document = new PdfDocument(reader, writer))
            {
                var redPattern = BuildPattern(redTerms);
                Log($"Using red regex pattern: {redPattern}");
                Log($"Using purple regex patterns by page: {string.Join(", ", purpleTermsByPage.Keys)}");

                for (int i = 1; i <= document.GetNumberOfPages(); i++)
                {
                    var page = document.GetPage(i);

                    var canvas = new PdfCanvas(page);
                    canvas.SetLineWidth(1);

                    // Red highlights (keywords)
                    if (!string.IsNullOrWhiteSpace(redPattern))
                    {
                        var redStrategy = new RegexBasedLocationExtractionStrategy(redPattern);
                        new PdfCanvasProcessor(redStrategy).ProcessPageContent(page);
                        var redLocations = redStrategy.GetResultantLocations();
                        if (redLocations != null && redLocations.Any())
                        {
                            Log($"Page {i}: Found {redLocations.Count} red matches.");
                            canvas.SetStrokeColor(ColorConstants.RED);
                            foreach (var loc in redLocations)
                            {
                                var rect = loc.GetRectangle();
                                if (rect == null) continue;
                                canvas.Rectangle(rect);
                                canvas.Stroke();
                            }
                        }
                    }

                    // Purple highlights (context evidence phrases)
                    // If dictionary contains key -1, treat as "all pages" (back-compat).
                    var purpleTerms = purpleTermsByPage.TryGetValue(i, out var listForPage) ? listForPage : null;
                    if (purpleTerms == null && purpleTermsByPage.TryGetValue(-1, out var allPagesList))
                    {
                        purpleTerms = allPagesList;
                    }

                    var purplePattern = BuildPattern(purpleTerms ?? new List<string>(), flexibleWhitespace: true);
                    if (!string.IsNullOrWhiteSpace(purplePattern))
                    {
                        var purpleStrategy = new RegexBasedLocationExtractionStrategy(purplePattern);
                        new PdfCanvasProcessor(purpleStrategy).ProcessPageContent(page);
                        var purpleLocations = purpleStrategy.GetResultantLocations();
                        if (purpleLocations != null && purpleLocations.Any())
                        {
                            Log($"Page {i}: Found {purpleLocations.Count} purple matches.");
                            canvas.SetStrokeColor(new DeviceRgb(0x6D, 0x28, 0xD9));
                            foreach (var loc in purpleLocations)
                            {
                                var rect = loc.GetRectangle();
                                if (rect == null) continue;
                                canvas.Rectangle(rect);
                                canvas.Stroke();
                            }
                        }
                    }
                }
            }

            Log("Highlighting complete.");
            return tempPath;
        }
        catch (Exception ex)
        {
            Log($"Error highlighting PDF: {ex.Message}");
            return filePath;
        }
    }

    public string CreateSubsetPdf(string filePath, IEnumerable<int> pageNumbers)
    {
        if (!File.Exists(filePath)) return filePath;
        var pages = (pageNumbers ?? Array.Empty<int>()).Distinct().OrderBy(n => n).ToList();
        if (pages.Count == 0) return filePath;

        try
        {
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Subset_{Guid.NewGuid()}.pdf");

            using var reader = new PdfReader(filePath);
            using var writer = new PdfWriter(tempPath);
            using var src = new PdfDocument(reader);
            using var dest = new PdfDocument(writer);

            // Copy requested pages only.
            foreach (var p in pages)
            {
                if (p < 1 || p > src.GetNumberOfPages()) continue;
                src.CopyPagesTo(p, p, dest);
            }

            dest.Close();
            Log($"Created subset PDF: {tempPath} pages=[{string.Join(",", pages)}]");
            return tempPath;
        }
        catch (Exception ex)
        {
            Log($"Error creating subset PDF: {ex.Message}");
            return filePath;
        }
    }

    private static string BuildPattern(IEnumerable<string> terms, bool flexibleWhitespace = false)
    {
        var list = (terms ?? Array.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0) return string.Empty;

        var parts = list.Select(t => flexibleWhitespace ? BuildFlexibleWhitespacePattern(t) : System.Text.RegularExpressions.Regex.Escape(t));
        return "(?i)(" + string.Join("|", parts) + ")";
    }

    private static string BuildFlexibleWhitespacePattern(string term)
    {
        // Make multi-word phrases match across line breaks/spaces: "a b" => "a\\s+b"
        var tokens = term
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tok => System.Text.RegularExpressions.Regex.Escape(tok))
            .ToList();

        return tokens.Count <= 1 ? System.Text.RegularExpressions.Regex.Escape(term) : string.Join("\\s+", tokens);
    }

    // Removed custom RegexLocationStrategy class as we are using the built-in one
}
