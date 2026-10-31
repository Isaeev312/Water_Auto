using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports.Tests;

public class CableJournalDocxTests
{
    private static readonly TitleBlock Stamp = new(
        "123-АПС.КЖ",
        "Подземная автостоянка",
        "Автостоянка",
        "Кабельный журнал",
        "Р",
        "Организация",
        [new("Разраб.", "Иванов"), new("Н. контр.", "Петров")]);

    private static readonly IReadOnlyList<CableLine> Lines =
    [
        new("ДПЛС1", "С2000-КДЛ, пост", "С2000-АР8 №1", "Адресная линия", "КПСнг(А)-FRLS 1×2×0,5", 123.4, "", 112.2),
        new("ДПЛС1.1", "КДЛ1", "ДИП1", "", "КПСнг(А)-FRLS 1×2×0,5", 80, "", 72.7),
        new("ДПЛС1.2", "ДИП1", "ДИП2", "", "КПСнг(А)-FRLS 1×2×0,5", 43.4, "", 39.5),
        new("П1", "РИП-12", "С2000-КДЛ", "Питание 12 В", "КПСнг(А)-FRLS 1×2×1,5", 0, "в посту"),
        new("С1", "ШУН", "С2000-АР8 №2", "Сигналы АУПТ", "КПСЭнг(А)-FRLS 2×2×0,75", 26.6, ""),
    ];

    private static T Open<T>(Func<WordprocessingDocument, T> read, IReadOnlyList<CableLine>? lines = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-kj-{Guid.NewGuid():N}.docx");
        CableJournalDocx.Write(path, lines ?? Lines, Stamp);
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

    private static Table Journal(WordprocessingDocument d) => d.MainDocumentPart!.Document.Body!.Elements<Table>().Single();

    private static List<string[]> Rows(IReadOnlyList<CableLine>? lines = null) => Open(d => Journal(d).Elements<TableRow>()
        .Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToArray()).ToList(), lines);

    [Fact]
    public void Page_is_A4_portrait_with_frame_margins()
    {
        var section = (SectionProperties)Open(d => d.MainDocumentPart!.Document.Body!.GetFirstChild<SectionProperties>()!.CloneNode(true));
        var size = section.GetFirstChild<PageSize>()!;
        Math.Round(size.Width!.Value * 25.4 / 1440).ShouldBe(210);
        Math.Round(size.Height!.Value * 25.4 / 1440).ShouldBe(297);
        (size.Orient is null || size.Orient.Value == PageOrientationValues.Portrait).ShouldBeTrue();
        var margin = section.GetFirstChild<PageMargin>()!;
        Math.Round(margin.Left!.Value * 25.4 / 1440).ShouldBe(20);
        Math.Round(margin.Right!.Value * 25.4 / 1440).ShouldBe(5);
        Math.Round(margin.Top!.Value * 25.4 / 1440).ShouldBe(5);
    }

    [Fact]
    public void Header_has_the_columns_of_form_7_GOST_21_613_and_repeats_on_every_sheet()
    {
        var rows = Open(d => Journal(d).Elements<TableRow>().Select(r => r.CloneNode(true)).Cast<TableRow>().ToList());
        var header = rows.Where(r => r.TableRowProperties!.GetFirstChild<TableHeader>() is not null).ToList();
        header.Count.ShouldBe(2);
        rows.Take(2).ShouldBe(header);
        var text = string.Join(" | ", header.Select(r => r.InnerText));
        foreach (var column in new[] { "Обозначение кабеля, провода", "Трасса", "Начало", "Конец", "Кабель, провод по проекту", "Марка, количество и сечение жил", "Длина по плану, м", "Длина с запасом, м", "Кабель, провод проложенный", "Примечание" })
        {
            text.ShouldContain(column);
        }

        // Ширина формы — 185 мм: рабочее поле A4 книжного между рамкой (20 мм) и правым полем (5 мм).
        var grid = Open(d => Journal(d).GetFirstChild<TableGrid>()!.Elements<GridColumn>().Sum(g => int.Parse(g.Width!.Value!, System.Globalization.CultureInfo.InvariantCulture)));
        Math.Round(grid * 25.4 / 1440).ShouldBe(185);
        Open(d => Journal(d).GetFirstChild<TableGrid>()!.Elements<GridColumn>().Count()).ShouldBe(CableJournalDocx.Columns.Count);
    }

    [Fact]
    public void All_lines_are_present_with_cable_dash_for_unknown_length_and_purpose_in_remark()
    {
        var rows = Rows();
        var loop = rows.Single(r => r[0] == "ДПЛС1");
        loop.ShouldBe(["ДПЛС1", "С2000-КДЛ, пост", "С2000-АР8 №1", "КПСнг(А)-FRLS 1×2×0,5", "112,2", "123,4", "", "Адресная линия"]);
        rows.Single(r => r[0] == "ДПЛС1.2").ShouldBe(["ДПЛС1.2", "ДИП1", "ДИП2", "КПСнг(А)-FRLS 1×2×0,5", "39,5", "43,4", "", ""]);
        var power = rows.Single(r => r[0] == "П1");
        power[4].ShouldBe("—");
        power[5].ShouldBe("—");
        power[6].ShouldBe("");
        power[7].ShouldBe("Питание 12 В; в посту; длина по трассе");
        rows.Count(r => Lines.Any(l => l.Id == r[0])).ShouldBe(Lines.Count);
    }

    [Fact]
    public void Totals_per_mark_and_overall_and_basis_note_are_present()
    {
        var rows = Rows();
        var total = rows.Last();
        // Строка-заголовок ДПЛС1 (123,4) в итоги не входит — считаются её участки 80 + 43,4 и С1 26,6.
        total[1].ShouldBe("Итого");
        total[5].ShouldBe("150");
        rows.Single(r => r[1] == "Итого КПСнг(А)-FRLS")[5].ShouldBe("123,4");
        rows.Single(r => r[1] == "Итого КПСЭнг(А)-FRLS")[5].ShouldBe("26,6");
        var text = Open(d => d.MainDocumentPart!.Document.Body!.InnerText);
        text.ShouldContain("Примечание. Кабели — с медными жилами");
        text.ShouldContain("СП 6.13130.2025");
    }

    [Fact]
    public void Single_mark_has_only_the_overall_total()
    {
        var rows = Rows([Lines[0], Lines[1], Lines[2], Lines[3]]);
        rows.Count(r => r[1].StartsWith("Итого", StringComparison.Ordinal)).ShouldBe(1);
        rows.Last()[5].ShouldBe("123,4");
    }

    [Fact]
    public void Summary_row_of_a_loop_is_bold_and_its_segments_are_not()
    {
        var bold = Open(d => Journal(d).Elements<TableRow>()
            .ToDictionary(r => r.Elements<TableCell>().First().InnerText + "/" + r.Elements<TableCell>().Skip(1).First().InnerText, r => r.Descendants<Bold>().Any()));
        bold["ДПЛС1/С2000-КДЛ, пост"].ShouldBeTrue();
        bold["ДПЛС1.1/КДЛ1"].ShouldBeFalse();
        bold["С1/ШУН"].ShouldBeFalse();
        CableJournalDocx.IsSummary(Lines[0], Lines).ShouldBeTrue();
        CableJournalDocx.IsSummary(Lines[1], Lines).ShouldBeFalse();
    }

    [Fact]
    public void Frame_is_in_the_header_and_first_sheet_has_form_5_the_others_form_6()
    {
        var (first, others, frames) = Open(d =>
        {
            var section = d.MainDocumentPart!.Document.Body!.GetFirstChild<SectionProperties>()!;
            Footer Footer(HeaderFooterValues type) => ((FooterPart)d.MainDocumentPart.GetPartById(section.Elements<FooterReference>().Single(f => f.Type! == type).Id!)).Footer;
            var frames = section.Elements<HeaderReference>()
                .Select(h => ((HeaderPart)d.MainDocumentPart.GetPartById(h.Id!)).Header.Descendants<DocumentFormat.OpenXml.Vml.Rectangle>().Count()).ToList();
            return (Footer(HeaderFooterValues.First), Footer(HeaderFooterValues.Default), frames);
        });

        // Рамка — прямоугольник в колонтитуле первого и последующих листов.
        frames.ShouldBe([1, 1]);

        // Форма 5: обозначение, наименование, стадия, организация, подписи; высота 40 мм.
        foreach (var s in new[] { "123-АПС.КЖ", "Кабельный журнал", "Р", "Организация", "Разраб.", "Иванов", "Н. контр.", "Петров", "Стадия", "Лист", "Листов", "Формат А4" })
        {
            first.InnerText.ShouldContain(s);
        }

        MmRounded(first.Descendants<Table>().First().Elements<TableRow>().Take(8).Sum(r => (int)r.TableRowProperties!.GetFirstChild<TableRowHeight>()!.Val!.Value)).ShouldBe(40);

        // Форма 6: обозначение и лист, без наименования и подписей; высота 15 мм.
        others.InnerText.ShouldContain("123-АПС.КЖ");
        others.InnerText.ShouldContain("Лист");
        others.InnerText.ShouldNotContain("Кабельный журнал");
        others.InnerText.ShouldNotContain("Разраб.");
        MmRounded(others.Descendants<Table>().First().Elements<TableRow>().Take(4).Sum(r => (int)r.TableRowProperties!.GetFirstChild<TableRowHeight>()!.Val!.Value)).ShouldBe(15);
    }

    [Theory]
    [InlineData("КПСнг(А)-FRLS 1×2×0,5", "КПСнг(А)-FRLS", "1×2×0,5")]
    [InlineData("ВВГнг(А)-FRLS 3x1,5", "ВВГнг(А)-FRLS", "3x1,5")]
    [InlineData("КПСнг(А)-FRLS", "КПСнг(А)-FRLS", "")]
    public void SplitCable_separates_mark_from_cores(string cable, string mark, string cores) =>
        CableJournalDocx.SplitCable(cable).ShouldBe((mark, cores));

    private static int MmRounded(int twips) => (int)Math.Round(twips * 25.4 / 1440);
}
