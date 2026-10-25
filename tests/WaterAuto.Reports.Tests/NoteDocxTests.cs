using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports.Tests;

public class NoteDocxTests
{
    private static readonly NoteDocument Note = new(
        new NoteTitle("[Наименование вуза]", "Автоматическая установка водяного пожаротушения", "Пояснительная записка", "[ФИО студента]", "[ФИО руководителя]", "[Город] 2027"),
        [
            new NoteSection("", "Введение", [new NoteParagraph("Цель работы — проект АУПТ.")]),
            new NoteSection("1", "Исходные данные", [
                new NoteList(["группа помещений — 2", "класс конструкций — К0"]),
                new NoteTable("Таблица 1 — Параметры установки", ["Параметр", "Значение", "Основание"], [["Интенсивность", "0,12 л/(с·м²)", "табл. 6.1"]]),
            ]),
            new NoteSection("2", "Гидравлический расчёт", [
                new NotePending("Раздел формируется после решения вопросов В-01, В-02, В-03."),
                new NoteParagraph("Расход **диктующего** оросителя *q*_{1} по формуле Б.1:\nвторая строка, м^{2}."),
                new NoteFormula("*q* = 1,5·*i*·*s* = 1,5·0,12·12 = 2,16 л/с", "5.1", ["где *i* — интенсивность орошения, л/(с·м^{2});", "*s* — площадь, м^{2}."]),
            ]),
        ],
        ["СП 485.1311500.2020. Установки пожаротушения автоматические."]);

    private static WordprocessingDocument WriteAndOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.docx");
        NoteDocx.Write(path, Note);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return WordprocessingDocument.Open(new MemoryStream(bytes), false);
    }

    private static List<string> Paragraphs(WordprocessingDocument doc) =>
        doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();

    [Fact]
    public void Title_page_sections_and_references_are_in_order()
    {
        using var doc = WriteAndOpen();
        var text = Paragraphs(doc);
        text.ShouldContain("[Наименование вуза]");
        text.ShouldContain("Пояснительная записка");
        var intro = text.IndexOf("ВВЕДЕНИЕ");
        var s1 = text.IndexOf("1 Исходные данные");
        var s2 = text.IndexOf("2 Гидравлический расчёт");
        var refs = text.IndexOf("СПИСОК ИСПОЛЬЗОВАННЫХ ИСТОЧНИКОВ");
        intro.ShouldBeGreaterThan(0);
        s1.ShouldBeGreaterThan(intro);
        s2.ShouldBeGreaterThan(s1);
        refs.ShouldBeGreaterThan(s2);
        text.ShouldContain("1 СП 485.1311500.2020. Установки пожаротушения автоматические.");
    }

    [Fact]
    public void Tables_lists_and_pending_parts_are_written()
    {
        using var doc = WriteAndOpen();
        var body = doc.MainDocumentPart!.Document.Body!;
        var table = body.Elements<Table>().Where(x => x.Descendants<TableHeader>().Any()).ShouldHaveSingleItem(); // без таблицы подписей титульного листа
        table.Elements<TableRow>().Count().ShouldBe(2);
        table.Elements<TableRow>().Last().InnerText.ShouldContain("0,12 л/(с·м²)");
        Paragraphs(doc).ShouldContain("Таблица 1 — Параметры установки");
        Paragraphs(doc).ShouldContain(p => p.StartsWith('–') && p.Contains("группа помещений — 2", StringComparison.Ordinal));

        // Незаполненное выделено, чтобы его нельзя было пропустить.
        var pending = body.Descendants<Run>().Single(r => r.InnerText.Contains("В-01", StringComparison.Ordinal));
        pending.RunProperties!.Highlight!.Val!.Value.ShouldBe(HighlightColorValues.Yellow);
    }

    [Fact]
    public void Markup_gives_bold_italic_subscript_superscript_and_line_breaks()
    {
        using var doc = WriteAndOpen();
        var body = doc.MainDocumentPart!.Document.Body!;
        var p = body.Elements<Paragraph>().Single(x => x.InnerText.StartsWith("Расход диктующего", StringComparison.Ordinal));
        p.InnerText.ShouldBe("Расход диктующего оросителя q1 по формуле Б.1:вторая строка, м2.");
        var runs = p.Elements<Run>().ToList();
        runs.Single(r => r.InnerText == "диктующего").RunProperties!.Bold.ShouldNotBeNull();
        runs.Single(r => r.InnerText == "q").RunProperties!.Italic.ShouldNotBeNull();
        runs.Single(r => r.InnerText == "1").RunProperties!.VerticalTextAlignment!.Val!.Value.ShouldBe(VerticalPositionValues.Subscript);
        runs.Single(r => r.InnerText == "2").RunProperties!.VerticalTextAlignment!.Val!.Value.ShouldBe(VerticalPositionValues.Superscript);
        p.Descendants<Break>().Count().ShouldBe(1);
        p.InnerText.ShouldNotContain("*");
    }

    [Fact]
    public void Formula_is_centred_with_its_number_and_legend()
    {
        using var doc = WriteAndOpen();
        var body = doc.MainDocumentPart!.Document.Body!;
        var f = body.Elements<Paragraph>().Single(x => x.InnerText.Contains("= 2,16 л/с", StringComparison.Ordinal));
        f.InnerText.ShouldEndWith("(5.1)");
        f.ParagraphProperties!.Tabs!.Elements<TabStop>().Select(t => t.Val!.Value).ShouldBe([TabStopValues.Center, TabStopValues.Right]);
        var legend = body.Elements<Paragraph>().SkipWhile(x => x != f).Skip(1).Take(2).Select(x => x.InnerText).ToList();
        legend[0].ShouldStartWith("где i — интенсивность");
        legend[1].ShouldStartWith("s — площадь");
    }

    [Fact]
    public void Page_is_A4_with_GOST_7_32_margins_and_font()
    {
        using var doc = WriteAndOpen();
        var section = doc.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().Single();
        var size = section.GetFirstChild<PageSize>()!;
        ((int)size.Width!.Value).ShouldBe(11906); // 210 мм в twip
        ((int)size.Height!.Value).ShouldBe(16838);
        var margin = section.GetFirstChild<PageMargin>()!;
        ((int)margin.Left!.Value).ShouldBe(1701); // 30 мм
        ((int)margin.Right!.Value).ShouldBe(850); // 15 мм
        ((int)margin.Top!.Value).ShouldBe(1134); // 20 мм
        var defaults = doc.MainDocumentPart.StyleDefinitionsPart!.Styles!.DocDefaults!.RunPropertiesDefault!.RunPropertiesBaseStyle!;
        defaults.RunFonts!.Ascii!.Value.ShouldBe("Times New Roman");
        defaults.FontSize!.Val!.Value.ShouldBe("28"); // 14 пт
        doc.MainDocumentPart.FooterParts.ShouldNotBeEmpty(); // номер страницы
    }
}
