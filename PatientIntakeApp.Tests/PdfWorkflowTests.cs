using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using PatientIntakeApp.Services;

namespace PatientIntakeApp.Tests;

public class PdfWorkflowTests
{
    [Fact]
    public void SyntheticPdfCanBeCountedExtractedAndSubset()
    {
        var root = Directory.CreateTempSubdirectory("patient-intake-pdf-");
        try
        {
            var source = Path.Combine(root.FullName, "SYNTHETIC_PACKET.pdf");
            using (var writer = new PdfWriter(source))
            using (var pdf = new PdfDocument(writer))
            using (var document = new Document(pdf))
            {
                document.Add(new Paragraph("SYNTHETIC PATIENT PACKET - PAGE ONE"));
                document.Add(new AreaBreak());
                document.Add(new Paragraph("SYNTHETIC REVIEW NOTES - PAGE TWO"));
            }
            var service = new PdfProcessingService();
            Assert.Equal(2, service.GetPageCount(source));
            var pages = service.ExtractText(source);
            Assert.Equal(2, pages.Count);
            Assert.Contains("SYNTHETIC PATIENT PACKET", pages[0].Text);
            Assert.NotEmpty(pages[0].PagePdfBytes!);
            var subset = service.CreateSubsetPdf(source, [2]);
            Assert.Equal(1, service.GetPageCount(subset));
            File.Delete(subset);
        }
        finally
        {
            root.Delete(true);
        }
    }
}

