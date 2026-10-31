using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>
/// Кабельный журнал АПС для выпуска: docx A4 книжный в рамке ГОСТ Р 21.101 (20 мм слева, 5 мм с остальных сторон).
/// Графы — по форме 7 ГОСТ 21.613-2014 (п. 6.5.4): обозначение кабеля; трасса — начало, конец; кабель по проекту —
/// марка с числом и сечением жил, длина по плану и с запасом; кабель проложенный (заполняет монтаж, графа пустая); примечание. Графа «Участок
/// трассы» в ширину A4 не помещается — назначение линии перенесено в примечание (отступление по п. 6.1.8 и 6.1.10, вопрос А-14).
/// Ширина формы 185 мм — рабочее поле A4 между рамкой и правым полем. Шапка двухъярусная (8 + 15 мм) повторяется на каждом
/// листе; строки — не менее 8 мм, текст в них переносится. В конце — итоги длин (с запасом) по каждой марке и общий итог;
/// строка-заголовок линии (её обозначение — префикс обозначений участков «ДПЛС1» → «ДПЛС1.1») в итоги не входит, показана жирным.
/// Первый лист — с основной надписью по форме 5 (185×40), последующие — по форме 6 (185×15) (ГОСТ Р 21.101-2020, п. 5.2.3),
/// как в <see cref="NoteDocx"/>; рамка — в колонтитуле, поэтому есть на каждом листе.
/// </summary>
public static partial class CableJournalDocx
{
    private const double PageWidthMm = 210;
    private const double PageHeightMm = 297;
    private const string Size = "18";
    private const string HeaderSize = "18";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Нижний ярус шапки — графы формы 7.</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["Обозначение кабеля, провода", "Начало", "Конец", "Марка, количество и сечение жил", "Длина по плану, м", "Длина с запасом, м", "Кабель, провод проложенный", "Примечание"];

    /// <summary>Ширины граф, мм; сумма — 185 мм (рабочее поле A4 книжного: 210 − 20 − 5).</summary>
    internal static readonly double[] WidthsMm = [22, 29, 29, 44, 14, 14, 16, 17];

    /// <summary>Примечание о нормативном основании выбора кабелей (внизу журнала).</summary>
    public const string Basis = "Кабели — с медными жилами, не распространяющие горение, огнестойкие по области применения прил. Г СП 6.13130.2025 (п. 6.2); время работоспособности — п. 6.17, табл. 6.2. Длины — по плану с запасом на изгибы, повороты и отходы (ГОСТ 21.613-2014, п. 6.5.6), уточняются по трассам. Графу «Кабель, провод проложенный» заполняет монтажная организация.";

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
                    new TopBorder { Val = BorderValues.Single, Size = 12 }, new BottomBorder { Val = BorderValues.Single, Size = 12 },
                    new LeftBorder { Val = BorderValues.Single, Size = 12 }, new RightBorder { Val = BorderValues.Single, Size = 12 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 12 }),
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TableCellLeftMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa })),
            new TableGrid(WidthsMm.Select(w => new GridColumn { Width = Mm(w) })));

        // Шапка формы 7: два яруса (8 + 15 мм). «Обозначение», «Проложенный» и «Примечание» объединены по вертикали,
        // «Трасса» — над началом и концом, «Кабель, провод по проекту» — над маркой с жилами и длиной.
        table.Append(HeaderRow(8,
        [
            Cell(0, Columns[0], MergeStart), Cell(1, "Трасса", span: 2), Cell(3, "Кабель, провод по проекту", span: 3),
            Cell(6, Columns[6], MergeStart), Cell(7, Columns[7], MergeStart),
        ]));
        table.Append(HeaderRow(15,
        [
            Cell(0, "", MergeContinue), Cell(1, Columns[1]), Cell(2, Columns[2]), Cell(3, Columns[3]), Cell(4, Columns[4]), Cell(5, Columns[5]),
            Cell(6, "", MergeContinue), Cell(7, "", MergeContinue),
        ]));

        foreach (var l in lines)
        {
            table.Append(DataRow([l.Id, l.From, l.To, l.Cable, Length(l.BaseLengthM ?? 0), Length(l.LengthM), "", Remark(l)], total: IsSummary(l, lines)));
        }

        // Итоги (длина с запасом): по каждой марке (в порядке первого появления) и общий; строки-заголовки линий и строки без длины не входят.
        var counted = lines.Where(l => !IsSummary(l, lines)).ToList();
        var marks = lines.Select(l => SplitCable(l.Cable).Mark).Distinct().ToList();
        if (marks.Count > 1)
        {
            foreach (var mark in marks)
            {
                var sum = counted.Where(l => SplitCable(l.Cable).Mark == mark).Sum(l => l.LengthM);
                table.Append(DataRow(["", "Итого " + mark, "", "", "", Length(sum), "", ""], total: true));
            }
        }

        table.Append(DataRow(["", "Итого", "", "", "", Length(counted.Sum(l => l.LengthM)), "", ""], total: true));
        body.Append(table);
        body.Append(new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "120" }, new Justification { Val = JustificationValues.Both }),
            new Run(new Text("Примечание. " + Basis) { Space = SpaceProcessingModeValues.Preserve })));

        // A4 книжный; поля — по рамке (20 мм слева, 5 мм с остальных сторон), внизу — над основной надписью формы 6 (15 мм);
        // на первом листе место под форму 5 (40 мм) освобождает колонтитул (GostFrame).
        var section = new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)Twips(PageWidthMm), Height = (UInt32Value)(uint)Twips(PageHeightMm), Orient = PageOrientationValues.Portrait },
            new PageMargin { Left = (UInt32Value)(uint)Twips(20), Right = (UInt32Value)(uint)Twips(5), Top = Twips(5), Bottom = Twips(5 + GostFrame.Height(StampForm.Form6)), Header = 0, Footer = 0, Gutter = 0 });
        body.Append(section);
        GostFrame.Apply(main, section, PageWidthMm, PageHeightMm, stamp, StampForm.Form5, StampForm.Form6);
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

    /// <summary>Строка-заголовок линии: её обозначение — префикс («ДПЛС1» → «ДПЛС1.1») обозначений других строк журнала.</summary>
    public static bool IsSummary(CableLine line, IReadOnlyList<CableLine> lines)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(o => !ReferenceEquals(o, line) && o.Id.StartsWith(line.Id + ".", StringComparison.Ordinal));
    }

    /// <summary>Примечание строки: назначение линии и заметка через точку с запятой; длина не определена — «длина по трассе».</summary>
    public static string Remark(CableLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = new[] { line.Purpose, line.Note, line.LengthM > 0 ? "" : "длина по трассе" }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim());
        return string.Join("; ", parts);
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

        props.Append(new TableCellBorders(new BottomBorder { Val = BorderValues.Single, Size = 12 }));
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

    private static TableRow DataRow(string[] cells, bool total = false) => new(
    [
        new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(8), HeightType = HeightRuleValues.AtLeast }, new CantSplit()),
        .. cells.Select((c, k) => new TableCell(
            new TableCellProperties(new TableCellWidth { Width = Mm(WidthsMm[k]), Type = TableWidthUnitValues.Dxa }, new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
            new Paragraph(
                // Обозначение и длины — по центру; трасса, марка и примечание — слева. Итоги и заголовки линий — жирным.
                new ParagraphProperties(new Justification { Val = k is 0 or 4 or 5 ? JustificationValues.Center : JustificationValues.Left }),
                new Run(total ? new RunProperties(new Bold()) : new RunProperties(), new Text(c) { Space = SpaceProcessingModeValues.Preserve })))),
    ]);

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Mm(double mm) => Twips(mm).ToString(CultureInfo.InvariantCulture);
}
