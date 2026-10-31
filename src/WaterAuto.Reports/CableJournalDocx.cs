using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>
/// Кабельный журнал АПС для выпуска: docx A3 альбомный в рамке, графы — по форме 7 ГОСТ 21.613-2014 (п. 6.5.4, кабельный
/// журнал для прокладки методом трасс): обозначение кабеля; трасса — начало, конец; участок трассы; кабель по проекту —
/// марка, количество и сечение жил, длина. Группа граф «проложен» (данные исполнительной документации) заменена графой
/// «Примечание» той же ширины — отступление по п. 6.1.8 и 6.1.10 (вопрос А-14). Ширина формы 395 мм — рабочее поле A3.
/// Шапка двухъярусная по 8 мм и ярус граф 20 мм, повторяется на каждом листе; строки — не менее 8 мм. Первый лист — с
/// основной надписью по форме 3, последующие — по форме 6 (ГОСТ Р 21.101-2020, п. 5.2.3), как в <see cref="SpecificationDocx"/>.
/// </summary>
public static partial class CableJournalDocx
{
    private const string Size = "22";
    private const string HeaderSize = "18";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Нижний ярус шапки — графы формы 7 (без группы «проложен»).</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["Обозначение кабеля, провода", "Начало", "Конец", "Участок трассы кабеля, провода", "Марка", "Кол., число и сечение жил", "Длина, м", "Примечание"];

    /// <summary>Ширины граф, мм (форма 7 ГОСТ 21.613-2014: 25, 60, 60, 110, 20, 35, 15; «проложен» 20+35+15 → «Примечание» 70).</summary>
    internal static readonly double[] WidthsMm = [25, 60, 60, 110, 20, 35, 15, 70];

    /// <summary>Примечание о нормативном основании выбора кабелей (внизу журнала).</summary>
    public const string Basis = "Кабели — с медными жилами, не распространяющие горение, огнестойкие по области применения прил. Г СП 6.13130.2025 (п. 6.2); время работоспособности — п. 6.17, табл. 6.2. Длины — по плану с запасом на изгибы, повороты и отходы (ГОСТ 21.613-2014, п. 6.5.6), уточняются по трассам.";

    public static void Write(string path, IReadOnlyList<CableLine> lines, TitleBlock stamp, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(stamp);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font }, new FontSize { Val = Size }, new FontSizeComplexScript { Val = Size }, new Languages { Val = "ru-RU" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto, After = "0", Before = "0" }))));

        var table = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = Mm(WidthsMm.Sum()) },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 12 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 12 }, new RightBorder { Val = BorderValues.Single, Size = 12 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 12 }),
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TableCellLeftMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa })),
            new TableGrid(WidthsMm.Select(w => new GridColumn { Width = Mm(w) })));

        // Шапка формы 7: три яруса (8 + 8 + 20 мм). Графы «Обозначение», «Участок трассы» и «Примечание» объединены по вертикали,
        // «Трасса» — над началом и концом, «Кабель, провод» → «по проекту» — над маркой, числом жил и длиной.
        table.Append(HeaderRow(8,
        [
            Cell(0, "Обозначение кабеля, провода", MergeStart), Cell(1, "Трасса", span: 2), Cell(3, "Участок трассы кабеля, провода", MergeStart),
            Cell(4, "Кабель, провод", span: 3), Cell(7, "Примечание", MergeStart),
        ]));
        table.Append(HeaderRow(8,
        [
            Cell(0, "", MergeContinue), Cell(1, "", MergeContinue, span: 2), Cell(3, "", MergeContinue),
            Cell(4, "по проекту", span: 3), Cell(7, "", MergeContinue),
        ]));
        table.Append(HeaderRow(20,
        [
            Cell(0, "", MergeContinue), Cell(1, "Начало"), Cell(2, "Конец"), Cell(3, "", MergeContinue),
            Cell(4, "Марка"), Cell(5, "Кол., число и сечение жил"), Cell(6, "Длина, м"), Cell(7, "", MergeContinue),
        ]));

        foreach (var l in lines)
        {
            var (mark, cores) = SplitCable(l.Cable);
            table.Append(DataRow([l.Id, l.From, l.To, l.Purpose, mark, cores, Length(l.LengthM), l.Note]));
        }

        table.Append(DataRow(["", "", "", "Итого", "", "", Length(lines.Sum(l => l.LengthM)), ""]));
        body.Append(table);
        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "120" }), new Run(new Text("Примечание. " + Basis) { Space = SpaceProcessingModeValues.Preserve })));

        var section = new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)Twips(420), Height = (UInt32Value)(uint)Twips(297), Orient = PageOrientationValues.Landscape },
            new PageMargin { Left = (UInt32Value)(uint)Twips(20), Right = (UInt32Value)(uint)Twips(5), Top = Twips(5), Bottom = Twips(5 + GostFrame.Height(StampForm.Form6)), Header = 0, Footer = 0, Gutter = 0 });
        body.Append(section);
        GostFrame.Apply(main, section, 420, 297, stamp, StampForm.Form3, StampForm.Form6);
        DocxFont.Apply(main, font);
        main.Document.Save();
    }

    /// <summary>
    /// Марка кабеля и «количество × число × сечение жил» из строки каталога вида «КПСнг(А)-FRLS 1×2×0,5»: последнее слово,
    /// содержащее знак умножения (× или x), — жилы, остальное — марка. Без такого слова вся строка — марка.
    /// </summary>
    public static (string Mark, string Cores) SplitCable(string cable)
    {
        var m = CoresPattern().Match(cable ?? "");
        return m.Success ? (m.Groups["mark"].Value.Trim(), m.Groups["cores"].Value) : ((cable ?? "").Trim(), "");
    }

    private static string Length(double m) => m > 0 ? m.ToString("0.#", Ru) : "—";

    [GeneratedRegex(@"^(?<mark>.*\S)\s+(?<cores>\d[\d,\.]*(?:\s*[×xх]\s*\d[\d,\.]*)+)\s*$")]
    private static partial Regex CoresPattern();

    private const int MergeStart = 1;
    private const int MergeContinue = 2;

    private static TableCell Cell(int column, string text, int merge = 0, int span = 1)
    {
        var width = WidthsMm.Skip(column).Take(span).Sum();
        var props = new TableCellProperties(new TableCellWidth { Width = Mm(width), Type = TableWidthUnitValues.Dxa });
        if (span > 1)
        {
            props.Append(new GridSpan { Val = span });
        }

        if (merge != 0)
        {
            props.Append(new VerticalMerge { Val = merge == MergeStart ? MergedCellValues.Restart : MergedCellValues.Continue });
        }

        props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
        return new TableCell(props, new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new FontSize { Val = HeaderSize }), new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static TableRow HeaderRow(double heightMm, TableCell[] cells) => new(
    [
        new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(heightMm), HeightType = HeightRuleValues.Exact }, new CantSplit(), new TableHeader()),
        .. cells,
    ]);

    private static TableRow DataRow(string[] cells) => new(
    [
        new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(8), HeightType = HeightRuleValues.AtLeast }, new CantSplit()),
        .. cells.Select((c, k) => new TableCell(
            new TableCellProperties(new TableCellWidth { Width = Mm(WidthsMm[k]), Type = TableWidthUnitValues.Dxa }, new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
            new Paragraph(
                // Обозначение, марка, жилы и длина — по центру; трасса, участок и примечание — слева.
                new ParagraphProperties(new Justification { Val = k is 0 or 4 or 5 or 6 ? JustificationValues.Center : JustificationValues.Left }),
                new Run(new Text(c) { Space = SpaceProcessingModeValues.Preserve })))),
    ]);

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Mm(double mm) => Twips(mm).ToString(CultureInfo.InvariantCulture);
}
