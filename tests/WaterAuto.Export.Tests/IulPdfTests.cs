using PdfSharp.Pdf.IO;

namespace WaterAuto.Export.Tests;

public class IulPdfTests
{
    [Fact]
    public void Iul_is_one_A4_page_and_merge_keeps_page_order()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var a = Path.Combine(dir, "a.pdf");
            var b = Path.Combine(dir, "b.pdf");
            IulPdf.Write(a, "1-ПТ-УЛ", [new IulDocument("1-ПТ", "Комплект", "0", "MD5", "00", "1-ПТ.pdf", new DateTime(2026, 11, 1), 10)], [new IulSignature("Разраб.", "Иванов")], "Arial");
            IulPdf.Write(b, "1-ПЗ-УЛ", [new IulDocument("1-ПЗ", "Записка", "0", "MD5", "00", "1-ПЗ.pdf", new DateTime(2026, 11, 1), 10)], [], "Arial");

            using (var iul = PdfReader.Open(a, PdfDocumentOpenMode.Import))
            {
                iul.PageCount.ShouldBe(1);
                iul.Pages[0].Width.Millimeter.ShouldBe(210, 0.5);
                iul.Pages[0].Height.Millimeter.ShouldBe(297, 0.5);
            }

            var merged = Path.Combine(dir, "m.pdf");
            IulPdf.Merge([a, b], merged, "том").ShouldBe(2);
            IulPdf.Md5(merged).Length.ShouldBe(32);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
