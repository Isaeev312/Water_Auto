using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class DxfPdfTests
{
    private static readonly TitleBlock Stamp = new("123-ПТ", "Автостоянка", "Здание", "Схема", "Р", "Организация", [new("Разраб.", "Иванов")], Sheet: "1");

    [Fact]
    public void Sheet_prints_to_one_page_of_its_format_in_vector_form()
    {
        // Лист А2 в масштабе 1:100: страница PDF — 594 × 420 мм, один лист.
        var doc = new CadDocument();
        var sheet = new SheetPlacement(SheetFormat.A2, 100, new XY(1000, 2000));
        GostSheet.Draw(doc, sheet, Stamp);
        doc.Entities.Add(new Line(sheet.At(100, 100), sheet.At(200, 200)));
        doc.Entities.Add(new MText { Value = @"Подпись\Pвторая строка", InsertPoint = sheet.At(100, 250), Height = 350 });
        using var stream = new MemoryStream();
        var log = new List<string>();

        DxfPdf.Render(doc, stream, "Arial", log.Add).ShouldBeTrue();

        stream.Position = 0;
        var pdf = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        pdf.PageCount.ShouldBe(1);
        pdf.Pages[0].Width.Millimeter.ShouldBe(594, 0.5);
        pdf.Pages[0].Height.Millimeter.ShouldBe(420, 0.5);
        log.ShouldBeEmpty();
    }

    [Fact]
    public void Drawing_without_a_sheet_frame_is_not_printed()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1000, 0, 0)));
        var log = new List<string>();
        DxfPdf.Render(doc, new MemoryStream(), "Arial", log.Add, "план").ShouldBeFalse();
        log.ShouldContain(m => m.Contains("нет рамки листа"));
    }

    [Fact]
    public void MText_formatting_codes_are_removed()
    {
        DxfPdf.Plain(@"{\fArial|b0;Текст}\Pстрока\~2").ShouldBe("Текст\nстрока 2");
    }
}
