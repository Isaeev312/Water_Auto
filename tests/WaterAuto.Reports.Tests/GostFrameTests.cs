using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports.Tests;

public class GostFrameTests
{
    private static readonly TitleBlock Stamp = new(
        "123-ПТ.СО",
        "Подземная автостоянка",
        "Автостоянка",
        "Спецификация оборудования, изделий и материалов",
        "Р",
        "Организация",
        [new("Разраб.", "Иванов"), new("Н. контр.", "Петров")]);

    private static readonly MaterialTakeoff Takeoff = new(
    [
        new TakeoffItem(TakeoffGroup.Sprinklers, "Ороситель спринклерный", "СВВ-12", 10, "шт.", Note: "в т.ч. запас 2"),
        new TakeoffItem(TakeoffGroup.Pipes, "Труба 57×3,5", "ГОСТ 10704-91", 12.5, "м", UnitMass: 4.62),
    ], []);

    private static T Open<T>(Action<string> write, Func<WordprocessingDocument, T> read)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-frame-{Guid.NewGuid():N}.docx");
        write(path);
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            return read(doc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Ширины граф основной надписи, мм (из сетки первой таблицы колонтитула).
    private static double[] Widths(Footer footer) =>
        [.. footer.Elements<Table>().First().GetFirstChild<TableGrid>()!.Elements<GridColumn>().Select(g => Math.Round(int.Parse(g.Width!.Value!, System.Globalization.CultureInfo.InvariantCulture) * 25.4 / 1440))];

    [Fact]
    public void Specification_has_frame_form_5_on_first_sheet_and_form_6_on_the_others()
    {
        var (first, others, frame, title) = Open(p => SpecificationDocx.Write(p, Takeoff, Stamp), d =>
        {
            var section = d.MainDocumentPart!.Document.Body!.GetFirstChild<SectionProperties>()!;
            Footer Footer(HeaderFooterValues type) => (Footer)((FooterPart)d.MainDocumentPart.GetPartById(section.Elements<FooterReference>().Single(f => f.Type! == type).Id!)).Footer.CloneNode(true);
            var header = d.MainDocumentPart.HeaderParts.First();
            return (Footer(HeaderFooterValues.First), Footer(HeaderFooterValues.Default), header.Header.InnerXml, section.GetFirstChild<TitlePage>() is not null);
        });
        title.ShouldBeTrue();
        frame.ShouldContain("margin-left:20mm");
        Widths(first).Sum().ShouldBe(185);
        Widths(others).Sum().ShouldBe(185);
        first.InnerText.ShouldContain("123-ПТ.СО");
        first.InnerText.ShouldContain("Стадия");
        first.InnerText.ShouldContain("Иванов");
        first.InnerText.ShouldContain("Инв. № подл.");
        others.InnerText.ShouldNotContain("Стадия");
        others.InnerText.ShouldContain("Лист");
    }

    [Fact]
    public void Specification_table_repeats_the_form_1_header()
    {
        var (header, text) = Open(p => SpecificationDocx.Write(p, Takeoff, Stamp), d =>
        {
            var table = d.MainDocumentPart!.Document.Body!.GetFirstChild<Table>()!;
            return (table.Elements<TableRow>().First().TableRowProperties!.GetFirstChild<TableHeader>() is not null, table.InnerText);
        });
        header.ShouldBeTrue();
        text.ShouldContain("Масса единицы, кг");
        text.ShouldContain("Оросители");
        text.ShouldContain("4,62");
    }

    [Fact]
    public void General_data_sheet_gets_form_3_with_sheet_count()
    {
        var data = new GeneralData("Общие данные", [new("1", "Общие данные", "")], [], [new("Расход", "л/с", "30")], ["Указание."]);
        var footer = Open(p => GeneralDataDocx.Write(p, data, Stamp with { SheetCount = 6 }), d => d.MainDocumentPart!.FooterParts.Select(f => f.Footer.InnerText).ToList());
        footer.ShouldContain(t => t.Contains("Листов", StringComparison.Ordinal) && t.Contains('6', StringComparison.Ordinal));
        footer.ShouldAllBe(t => t.Contains("Подп.", StringComparison.Ordinal));
    }
}
