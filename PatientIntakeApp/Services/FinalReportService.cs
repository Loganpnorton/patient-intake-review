using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Services;

public interface IFinalReportService
{
    string GenerateFinalReportPdf(string sourcePdfPath, List<Finding> findings, string? agentOverview, List<Finding>? contextFindings = null);
}

public class FinalReportService : IFinalReportService
{
    // Palette (match UI chips)
    private static readonly DeviceRgb ColorLocalWarning = new DeviceRgb(0xFF, 0xC1, 0x07); // #FFC107
    private static readonly DeviceRgb ColorAiWarning = new DeviceRgb(0x6D, 0x28, 0xD9);    // #6D28D9
    private static readonly DeviceRgb ColorCleared = new DeviceRgb(0x38, 0x8E, 0x3C);      // #388E3C
    private static readonly DeviceRgb ColorText = new DeviceRgb(0x11, 0x11, 0x11);
    private static readonly DeviceRgb ColorMuted = new DeviceRgb(0x66, 0x66, 0x66);
    private static readonly DeviceRgb ColorBorder = new DeviceRgb(0xDD, 0xDD, 0xDD);
    private static readonly DeviceRgb ColorPurpleLight = new DeviceRgb(0xF3, 0xE8, 0xFF);   // #F3E8FF

    public string GenerateFinalReportPdf(string sourcePdfPath, List<Finding> findings, string? agentOverview, List<Finding>? contextFindings = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePdfPath)) throw new ArgumentException("Source PDF path is required.", nameof(sourcePdfPath));
        findings ??= new List<Finding>();
        contextFindings ??= new List<Finding>();

        var reportDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PatientIntakeReports");
        Directory.CreateDirectory(reportDir);

        var baseName = System.IO.Path.GetFileNameWithoutExtension(sourcePdfPath);
        var outPath = System.IO.Path.Combine(reportDir, $"{baseName}_FinalReport_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        using var writer = new PdfWriter(outPath);
        using var pdf = new PdfDocument(writer);
        var page = pdf.AddNewPage(PageSize.LETTER);
        var pageSize = page.GetPageSize();
        var canvas = new PdfCanvas(page);

        // Counts
        var localCount = findings.Count(f => f.Source == FindingSource.Local);
        var aiCount = findings.Count(f => f.Source == FindingSource.AI);
        var totalFlags = localCount + aiCount;

        var clearedCount = findings.Count(f => f.ReviewStatus == ReviewStatus.Passed);
        var flaggedCount = findings.Count(f => f.ReviewStatus == ReviewStatus.Rejected);
        var reviewedTotal = clearedCount + flaggedCount;

        // Title
        // Use the Canvas(PdfCanvas, Rectangle) overload for compatibility with iText7 variants.
        var overlay = new iText.Layout.Canvas(canvas, pageSize);
        overlay.Add(new Paragraph("Patient Intake Final Report")
            .SetFontSize(20)
            .SetBold()
            .SetFontColor(ColorText)
            .SetFixedPosition(36, pageSize.GetTop() - 60, pageSize.GetWidth() - 72));

        overlay.Add(new Paragraph($"{System.IO.Path.GetFileName(sourcePdfPath)}  •  Generated {DateTime.Now:g}")
            .SetFontSize(10)
            .SetFontColor(ColorMuted)
            .SetFixedPosition(36, pageSize.GetTop() - 80, pageSize.GetWidth() - 72));

        // Donuts positions
        var donutY = pageSize.GetTop() - 210;
        var leftCx = 170f;
        var rightCx = pageSize.GetWidth() - 170f;
        var cy = donutY;
        var radius = 70f;
        var thickness = 18f;

        // Donut labels
        overlay.Add(new Paragraph("Flagged Items")
            .SetFontSize(12)
            .SetBold()
            .SetFontColor(ColorText)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetFixedPosition(leftCx - 120, cy + radius + 14, 240));

        overlay.Add(new Paragraph("Final Reviewed Items")
            .SetFontSize(12)
            .SetBold()
            .SetFontColor(ColorText)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetFixedPosition(rightCx - 120, cy + radius + 14, 240));

        // Donut 1: Local vs AI
        DrawDonut(canvas, leftCx, cy, radius, thickness, new[]
        {
            (localCount, (Color)ColorLocalWarning),
            (aiCount, (Color)ColorAiWarning),
        });
        overlay.Add(new Paragraph($"{totalFlags}")
            .SetFontSize(18)
            .SetBold()
            .SetTextAlignment(TextAlignment.CENTER)
            .SetFontColor(ColorText)
            .SetFixedPosition(leftCx - 30, cy - 8, 60));

        // Donut 2: Cleared vs Flagged
        DrawDonut(canvas, rightCx, cy, radius, thickness, new[]
        {
            (clearedCount, (Color)ColorCleared),
            (flaggedCount, (Color)ColorLocalWarning),
        });
        overlay.Add(new Paragraph($"{reviewedTotal}")
            .SetFontSize(18)
            .SetBold()
            .SetTextAlignment(TextAlignment.CENTER)
            .SetFontColor(ColorText)
            .SetFixedPosition(rightCx - 30, cy - 8, 60));

        // Legend
        var legendY = cy - radius - 40;
        DrawLegendItem(canvas, overlay, 70, legendY, ColorLocalWarning, "Local warning");
        DrawLegendItem(canvas, overlay, 220, legendY, ColorAiWarning, "AI warning");
        DrawLegendItem(canvas, overlay, 350, legendY, ColorCleared, "Cleared");
        DrawLegendItem(canvas, overlay, 460, legendY, ColorLocalWarning, "Flagged");

        // Summary box: X/Y Flags Cleared
        var boxX = 36f;
        var boxY = legendY - 65;
        var boxW = pageSize.GetWidth() - 72;
        var boxH = 52f;
        canvas.SaveState();
        canvas.SetLineWidth(1);
        canvas.SetStrokeColor(ColorBorder);
        canvas.RoundRectangle(boxX, boxY, boxW, boxH, 8);
        canvas.Stroke();
        canvas.RestoreState();

        overlay.Add(new Paragraph($"{clearedCount}/{reviewedTotal} Flags Cleared")
            .SetFontSize(16)
            .SetBold()
            .SetFontColor(ColorText)
            .SetFixedPosition(boxX + 16, boxY + 16, boxW - 32));

        // Context rule checks (final report only)
        var contextTitleY = boxY - 40;
        overlay.Add(new Paragraph("Context Rule Checks")
            .SetFontSize(12)
            .SetBold()
            .SetFontColor(ColorText)
            .SetFixedPosition(36, contextTitleY, pageSize.GetWidth() - 72));

        var contextBoxY = contextTitleY - 120;
        var contextBoxH = 100f;
        canvas.SaveState();
        canvas.SetLineWidth(1);
        canvas.SetStrokeColor(ColorBorder);
        canvas.RoundRectangle(36, contextBoxY, pageSize.GetWidth() - 72, contextBoxH, 8);
        canvas.Stroke();
        canvas.RestoreState();

        var hasContextViolations = contextFindings.Any();
        if (!hasContextViolations)
        {
            overlay.Add(new Paragraph("✓ No context rule violations detected")
                .SetFontSize(11)
                .SetBold()
                .SetFontColor(ColorCleared)
                .SetFixedPosition(52, contextBoxY + 26, pageSize.GetWidth() - 104));
        }
        else
        {
            // Use a sub-canvas so content flows from the top of the box.
            var contextContent = new iText.Layout.Canvas(canvas,
                new Rectangle(52, contextBoxY + 12, pageSize.GetWidth() - 104, contextBoxH - 24));

            var items = contextFindings
                .OrderBy(f => f.Page)
                .ThenBy(f => f.Term, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            foreach (var f in items)
            {
                var status = f.ReviewStatus switch
                {
                    ReviewStatus.Passed => "Cleared",
                    ReviewStatus.Rejected => "Flagged",
                    _ => "Needs Review"
                };

                var evidence = (f.Context ?? string.Empty).Trim();
                var firstLine = evidence.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
                var reason = string.IsNullOrWhiteSpace(firstLine) ? "Context violation detected." : firstLine.Trim();

                // Purple boxed reason (what triggered the flag)
                contextContent.Add(new Paragraph("• " + reason)
                    .SetFontSize(10.5f)
                    .SetFontColor(ColorText)
                    .SetMargin(0)
                    .SetMarginBottom(3));

                var pageText = f.Page > 0 ? $"Page {f.Page}" : "Document-level";

                // Small italic line citing the rule and status
                contextContent.Add(new Paragraph($"Context Rule Violation: {f.Term} ({pageText}) - {status}")
                    .SetFontSize(9.5f)
                    .SetItalic()
                    .SetFontColor(ColorMuted)
                    .SetMargin(0)
                    .SetMarginBottom(8));
            }

            if (contextFindings.Count > items.Count)
            {
                contextContent.Add(new Paragraph("…")
                    .SetFontSize(10)
                    .SetFontColor(ColorMuted)
                    .SetMargin(0));
            }

            contextContent.Close();
        }

        // Agent overview (final report only)
        var overviewTitleY = contextBoxY - 40;
        overlay.Add(new Paragraph("AI Intake Analyst Overview")
            .SetFontSize(12)
            .SetBold()
            .SetFontColor(ColorText)
            .SetFixedPosition(36, overviewTitleY, pageSize.GetWidth() - 72));

        var overviewBoxY = overviewTitleY - 250;
        var overviewBoxH = 220f;
        canvas.SaveState();
        canvas.SetLineWidth(1);
        canvas.SetStrokeColor(ColorBorder);
        canvas.RoundRectangle(36, overviewBoxY, pageSize.GetWidth() - 72, overviewBoxH, 8);
        canvas.Stroke();
        canvas.RestoreState();

        var overviewText = string.IsNullOrWhiteSpace(agentOverview) ? "No overview available." : agentOverview!.Trim();
        // Use a sub-canvas so text starts at the top of the box (avoids "stuck to bottom" look).
        var overviewContent = new iText.Layout.Canvas(canvas,
            new Rectangle(52, overviewBoxY + 12, pageSize.GetWidth() - 104, overviewBoxH - 24));
        overviewContent.Add(new Paragraph(overviewText)
            .SetFontSize(10.5f)
            .SetFontColor(ColorText)
            .SetMultipliedLeading(1.2f)
            .SetMargin(0));
        overviewContent.Close();

        overlay.Close();
        pdf.Close();

        return outPath;
    }

    private static void DrawLegendItem(PdfCanvas canvas, iText.Layout.Canvas overlay, float x, float y, Color color, string label)
    {
        canvas.SaveState();
        canvas.SetFillColor(color);
        canvas.Rectangle(x, y, 10, 10);
        canvas.Fill();
        canvas.RestoreState();

        overlay.Add(new Paragraph(label)
            .SetFontSize(10)
            .SetFontColor(ColorMuted)
            .SetFixedPosition(x + 14, y - 2, 130));
    }

    private static void DrawDonut(PdfCanvas canvas, float cx, float cy, float radius, float thickness, IEnumerable<(int count, Color color)> segments)
    {
        var segList = segments.Where(s => s.count > 0).ToList();
        var total = segList.Sum(s => s.count);

        // If there is no data, draw a light grey ring.
        if (total <= 0)
        {
            canvas.SaveState();
            canvas.SetLineWidth(thickness);
            canvas.SetStrokeColor(new DeviceRgb(0xEE, 0xEE, 0xEE));
            canvas.Arc(cx - radius, cy - radius, cx + radius, cy + radius, 0, 360);
            canvas.Stroke();
            canvas.RestoreState();
            return;
        }

        // Draw each segment as a thick stroked arc.
        var startAngle = 90f; // start at top
        var gap = 0.0f; // No gaps; user wants a continuous ring

        foreach (var (count, color) in segList)
        {
            var fraction = (float)count / total;
            var sweep = fraction * 360f;
            var sweepAdj = Math.Max(0.0f, sweep - gap);
            var startAdj = startAngle + (gap / 2f);

            canvas.SaveState();
            canvas.SetLineWidth(thickness);
            canvas.SetStrokeColor(color);
            canvas.SetLineCapStyle(0); // butt cap to avoid rounded ends creating visible gaps
            // Negative extent to go clockwise.
            canvas.Arc(cx - radius, cy - radius, cx + radius, cy + radius, startAdj, -sweepAdj);
            canvas.Stroke();
            canvas.RestoreState();

            startAngle -= sweep;
        }
    }
}

