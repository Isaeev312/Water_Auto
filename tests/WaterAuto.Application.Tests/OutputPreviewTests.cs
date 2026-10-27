using System.IO;
using WaterAuto.Application;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Предпросмотр выпуска в приложении: читатели xlsx/docx для окна предпросмотра. Файлы создаются теми же
/// писателями Reports, что и настоящий выпуск (спецификация xlsx, пояснительная записка docx), поэтому тест
/// проверяет наличие данных для пользователя, а не конкретное форматирование документа.
/// </summary>
public sealed class OutputPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Спецификация с тремя разделами: установка, труба и оросители — как в реальном выпуске ПТ.СО.
    private static MaterialTakeoff Takeoff() => new(
    [
        new TakeoffItem(
            TakeoffGroup.PumpStation,
            "Установка пожаротушения Hydro MX-A 1/2 CR 15-3",
            null,
            1,
            "компл.",
            "коллекторы DN80",
            Supplier: "Grundfos",
            Role: "pumpSet"),
        new TakeoffItem(TakeoffGroup.Pipes, "Труба стальная DN50", "ГОСТ 3262-75", 12.5, "м", "подводящий трубопровод"),
        new TakeoffItem(TakeoffGroup.Sprinklers, "Ороситель СВВ-К 0,47", "ТУ 4854-001", 24, "шт.", UnitMass: 0.2),
    ], []);

    [Fact]
    public void Xlsx_specification_is_read_as_a_table()
    {
        var path = Path.Combine(_dir, "specification.xlsx");
        SpecificationXlsx.Write(path, Takeoff(), new SpecificationHeader("Спецификация оборудования, изделий и материалов"));

        var table = OutputPreview.Read(path).ShouldBeOfType<PreviewTable>();
        table.Rows.ShouldNotBeEmpty();
        table.Rows.Count.ShouldBeGreaterThan(5);
        var text = string.Join("\n", table.Rows.Select(row => string.Join(" | ", row)));
        text.ShouldContain("Спецификация");
        text.ShouldContain("Наименование и техническая характеристика");
        text.ShouldContain("Установка пожаротушения Hydro MX-A 1/2 CR 15-3");
        text.ShouldContain("Труба стальная DN50");
        text.ShouldContain("Ороситель СВВ-К 0,47");
        text.Any(char.IsDigit).ShouldBeTrue("в таблице должны быть числа (количество, позиции)");
        // Первая строка — заголовок выпуска в первой ячейке (объединённая A1:I1), остальные ячейки пустые.
        table.Rows[0][0].ShouldContain("Спецификация");
    }

    [Fact]
    public void Docx_note_is_read_as_paragraphs()
    {
        var path = Path.Combine(_dir, "note.docx");
        WriteNote(path);

        var preview = OutputPreview.Read(path).ShouldBeOfType<PreviewText>();
        preview.Lines.ShouldNotBeEmpty();
        var text = string.Join("\n", preview.Lines);
        text.ShouldContain("Пояснительная записка");
        text.ShouldContain("Насосная станция");
        text.ShouldContain("Hydro MX-A 1/2 CR 15-3");
        // Пустые абзацы сохраняются как пустые строки — по ним в окне видны интервалы.
        preview.Lines.Any(string.IsNullOrEmpty).ShouldBeTrue("пустые абзацы должны сохраняться для интервалов");
    }

    [Fact]
    public void CanPreview_only_for_xlsx_and_docx()
    {
        OutputPreview.CanPreview("specification.xlsx").ShouldBeTrue();
        OutputPreview.CanPreview("note.docx").ShouldBeTrue();
        OutputPreview.CanPreview(Path.Combine(_dir, "a.XLSX")).ShouldBeTrue("расширение сравнивается без учёта регистра");
        OutputPreview.CanPreview("plan.dxf").ShouldBeFalse();
        OutputPreview.CanPreview("issues.txt").ShouldBeFalse();
        OutputPreview.CanPreview("").ShouldBeFalse();
        OutputPreview.CanPreview("   ").ShouldBeFalse();
    }

    [Fact]
    public void Reading_unsupported_file_throws_invalid_data()
    {
        var path = Path.Combine(_dir, "issues.txt");
        File.WriteAllText(path, "замечаний нет");
        OutputPreview.CanPreview(path).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => OutputPreview.Read(path));
    }

    [Fact]
    public void Broken_xlsx_is_reported_as_invalid_data()
    {
        var path = Path.Combine(_dir, "broken.xlsx");
        File.WriteAllText(path, "это не xlsx");
        var exception = Should.Throw<InvalidDataException>(() => OutputPreview.Read(path));
        exception.Message.ShouldNotBeEmpty();
    }

    [Fact]
    public void Missing_file_throws_file_not_found()
    {
        var path = Path.Combine(_dir, "nope.xlsx");
        Should.Throw<FileNotFoundException>(() => OutputPreview.Read(path));
    }

    // Записка: титул + раздел «Насосная станция» — ключевые слова должны попасть в предпросмотр.
    private static void WriteNote(string path)
    {
        var note = new NoteDocument(
            new NoteTitle(
                "[Наименование организации]",
                "Пояснительная записка",
                "Расчёт водяных автоматических установок пожаротушения",
                "[ФИО]",
                "[ФИО]",
                "[Город, год]",
                "Паркинг №2"),
            [
                new NoteSection("1", "Насосная станция", [
                    new NoteParagraph("Принята комплектная установка Hydro MX-A 1/2 CR 15-3: 1 основных и 2 резервных насосов."),
                    new NoteParagraph(""),
                    new NoteParagraph("Расход и напор — по расчёту."),
                ]),
            ],
            []);
        NoteDocx.Write(path, note);
    }
}
