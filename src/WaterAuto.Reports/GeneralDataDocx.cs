using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Строка ведомости рабочих чертежей: лист, наименование, примечание.</summary>
public sealed record SheetRow(string Sheet, string Name, string Note);

/// <summary>Строка ведомости ссылочных и прилагаемых документов: обозначение, наименование, примечание.</summary>
public sealed record ReferenceRow(string Designation, string Name, string Note);

/// <summary>Основной показатель: наименование, единица, значение.</summary>
public sealed record IndicatorRow(string Name, string Unit, string Value);

/// <summary>Содержание листа «Общие данные» (ГОСТ Р 21.101): ведомости, основные показатели, общие указания.</summary>
public sealed record GeneralData(
    string Title,
    IReadOnlyList<SheetRow> Sheets,
    IReadOnlyList<ReferenceRow> References,
    IReadOnlyList<IndicatorRow> Indicators,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<ReferenceRow>? Attached = null);

/// <summary>
/// Лист «Общие данные по рабочим чертежам» основного комплекта (ГОСТ Р 21.101) в docx A3 альбомный:
/// ведомость рабочих чертежей (форма 1), ведомость ссылочных и прилагаемых документов (форма 2), основные показатели,
/// общие указания. Шрифт — шрифт проекта (по умолчанию GOST Common), как на чертежах; рамка и основная надпись по форме 3 (<see cref="GostFrame"/>).
/// </summary>
public static class GeneralDataDocx
{
    private const string Font = GostFrame.Font;
    private const string Size = "24";
    private static readonly string[] ReferencesCaption = ["", "Ссылочные документы", ""];
    private static readonly string[] AttachedCaption = ["", "Прилагаемые документы", ""];

    public static void Write(string path, GeneralData data, TitleBlock? stamp = null, string font = GostFrame.Font)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font }, new FontSize { Val = Size }, new FontSizeComplexScript { Val = Size }, new Languages { Val = "ru-RU" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "0" }))));

        body.Append(Heading(data.Title));
        body.Append(Heading("Ведомость рабочих чертежей основного комплекта"));
        body.Append(Table([20, 130, 35], ["Лист", "Наименование", "Примечание"], data.Sheets.Select(s => new[] { s.Sheet, s.Name, s.Note })));
        body.Append(Heading("Ведомость ссылочных и прилагаемых документов"));
        body.Append(Table([50, 100, 35], ["Обозначение", "Наименование", "Примечание"], [
            ReferencesCaption,
            .. data.References.Select(r => new[] { r.Designation, r.Name, r.Note }),
            .. data.Attached is { Count: > 0 } attached ? [AttachedCaption, .. attached.Select(r => new[] { r.Designation, r.Name, r.Note })] : Array.Empty<string[]>(),
        ]));
        body.Append(Heading("Основные показатели"));
        body.Append(Table([110, 30, 45], ["Наименование показателя", "Единица измерения", "Значение"], data.Indicators.Select(i => new[] { i.Name, i.Unit, i.Value })));
        body.Append(Heading("Общие указания"));
        body.Append(data.Instructions.Select((t, k) => new Paragraph(
            new ParagraphProperties(new Indentation { FirstLine = Mm(12.5) }, new Justification { Val = JustificationValues.Both }),
            new Run(new Text($"{k + 1} {t}") { Space = SpaceProcessingModeValues.Preserve }))));

        // A3 альбомный; поля: внутри рамки (20 мм слева, 5 мм с остальных сторон), внизу — над основной надписью (форма 3, 55 мм).
        var section = new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)Twips(420), Height = (UInt32Value)(uint)Twips(297), Orient = PageOrientationValues.Landscape },
            new PageMargin { Left = (UInt32Value)(uint)Twips(25), Right = (UInt32Value)(uint)Twips(10), Top = Twips(10), Bottom = Twips(65), Header = 0, Footer = 0, Gutter = 0 },
            new Columns { ColumnCount = 2, Space = Mm(10) });
        body.Append(section);
        if (stamp is not null)
        {
            GostFrame.Apply(main, section, 420, 297, stamp, StampForm.Form3, StampForm.Form3);
        }

        DocxFont.Apply(main, font);

        main.Document.Save();
    }

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Mm(double mm) => Twips(mm).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Paragraph Heading(string text) => new(
        new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" }, new Justification { Val = JustificationValues.Center }),
        new Run(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    // Таблица формы ГОСТ Р 21.101: ширины граф в мм, шапка по центру, строки по 8 мм.
    private static Table Table(int[] widthsMm, string[] header, IEnumerable<string[]> rows)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 8 }, new BottomBorder { Val = BorderValues.Single, Size = 8 },
            new LeftBorder { Val = BorderValues.Single, Size = 8 }, new RightBorder { Val = BorderValues.Single, Size = 8 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 8 });
        var table = new Table(
            new TableProperties(border, new TableWidth { Type = TableWidthUnitValues.Dxa, Width = Mm(widthsMm.Sum()) }, new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(new TableCellLeftMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa }, new TableCellRightMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa })),
            new TableGrid(widthsMm.Select(w => new GridColumn { Width = Mm(w) })));

        TableRow Row(string[] cells, bool head) => new(
            [
                new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(8), HeightType = HeightRuleValues.AtLeast }, new CantSplit()),
                .. cells.Select((c, k) => new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = Mm(widthsMm[k]), Type = TableWidthUnitValues.Dxa }, new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
                    new Paragraph(
                        // Шапка и последняя графа (значение, примечание) — по центру, остальное — слева.
                        new ParagraphProperties(new Justification { Val = head || k == cells.Length - 1 ? JustificationValues.Center : JustificationValues.Left }),
                        new Run(head ? new RunProperties(new Bold()) : new RunProperties(), new Text(c) { Space = SpaceProcessingModeValues.Preserve })))),
            ]);

        table.Append(Row(header, true));
        table.Append(rows.Select(r => Row(r, false)));
        return table;
    }
}
