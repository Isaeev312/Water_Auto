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
        new("ДПЛС1", "С2000-КДЛ, пост", "С2000-АР8 №1", "Адресная линия", "КПСнг(А)-FRLS 1×2×0,5", 123.4, ""),
        new("П1", "РИП-12", "С2000-КДЛ", "Питание 12 В", "КПСнг(А)-FRLS 1×2×1,5", 0, "в посту"),
        new("С1", "ШУН", "С2000-АР8 №2", "Сигналы АУПТ", "КПСЭнг(А)-FRLS 2×2×0,75", 26.6, ""),
    ];

    private static T Open<T>(Func<WordprocessingDocument, T> read)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-kj-{Guid.NewGuid():N}.docx");
        CableJournalDocx.Write(path, Lines, Stamp);
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

    private static List<string[]> Rows() => Open(d => d.MainDocumentPart!.Document.Body!.Elements<Table>().Single().Elements<TableRow>()
        .Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToArray()).ToList());

    [Fact]
    public void Header_has_the_columns_of_form_7_GOST_21_613_and_repeats_on_every_sheet()
    {
        var rows = Open(d => d.MainDocumentPart!.Document.Body!.Elements<Table>().Single().Elements<TableRow>().ToList());
        var header = rows.Where(r => r.TableRowProperties!.GetFirstChild<TableHeader>() is not null).ToList();
        header.Count.ShouldBe(3);
        var text = string.Join(" | ", header.Select(r => r.InnerText));
        foreach (var column in new[] { "Обозначение кабеля, провода", "Трасса", "Начало", "Конец", "Участок трассы кабеля, провода", "Кабель, провод", "по проекту", "Марка", "Кол., число и сечение жил", "Длина, м", "Примечание" })
        {
            text.ShouldContain(column);
        }

        // Ширина формы — 395 мм: рабочее поле A3 между рамкой (20 мм) и правым полем (5 мм).
        var grid = rows[0].Parent!.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Sum(g => int.Parse(g.Width!.Value!, System.Globalization.CultureInfo.InvariantCulture));
        Math.Round(grid * 25.4 / 1440).ShouldBe(395);
    }

    [Fact]
    public void Rows_split_cable_into_mark_and_cores_and_dash_for_unknown_length()
    {
        var rows = Rows();
        var loop = rows.Single(r => r[0] == "ДПЛС1");
        loop.ShouldBe(["ДПЛС1", "С2000-КДЛ, пост", "С2000-АР8 №1", "Адресная линия", "КПСнг(А)-FRLS", "1×2×0,5", "123,4", ""]);
        var power = rows.Single(r => r[0] == "П1");
        power[6].ShouldBe("—");
        power[7].ShouldBe("в посту");
    }

    [Fact]
    public void Total_length_and_basis_note_are_present()
    {
        var rows = Rows();
        var total = rows.Last();
        total[3].ShouldBe("Итого");
        total[6].ShouldBe("150");
        var text = Open(d => d.MainDocumentPart!.Document.Body!.InnerText);
        text.ShouldContain("Примечание. Кабели — с медными жилами");
        text.ShouldContain("СП 6.13130.2025");
    }

    [Fact]
    public void First_sheet_has_form_3_and_the_others_form_6()
    {
        var (first, others) = Open(d =>
        {
            var section = d.MainDocumentPart!.Document.Body!.GetFirstChild<SectionProperties>()!;
            Footer Footer(HeaderFooterValues type) => ((FooterPart)d.MainDocumentPart.GetPartById(section.Elements<FooterReference>().Single(f => f.Type! == type).Id!)).Footer;
            return (Footer(HeaderFooterValues.First).InnerText, Footer(HeaderFooterValues.Default).InnerText);
        });
        first.ShouldContain("Подземная автостоянка");
        first.ShouldContain("123-АПС.КЖ");
        others.ShouldContain("123-АПС.КЖ");
        others.ShouldNotContain("Подземная автостоянка");
    }

    [Theory]
    [InlineData("КПСнг(А)-FRLS 1×2×0,5", "КПСнг(А)-FRLS", "1×2×0,5")]
    [InlineData("ВВГнг(А)-FRLS 3x1,5", "ВВГнг(А)-FRLS", "3x1,5")]
    [InlineData("КПСнг(А)-FRLS", "КПСнг(А)-FRLS", "")]
    public void SplitCable_separates_mark_from_cores(string cable, string mark, string cores) =>
        CableJournalDocx.SplitCable(cable).ShouldBe((mark, cores));
}
