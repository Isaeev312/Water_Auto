using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Строка перечня сертификатов: позиция спецификации, наименование, марка, изготовитель и документ о соответствии.</summary>
public sealed record CertificateRow(string Position, string Name, string Designation, string Manufacturer, string Certificate);

/// <summary>
/// Перечень документов о подтверждении соответствия (сертификатов) к спецификации — отдельный документ тома, чтобы графа
/// «Примечание» формы 1 ГОСТ 21.110 не перегружалась. docx A4 книжный в рамке ГОСТ Р 21.101: первый лист — основная надпись
/// по форме 5, последующие — по форме 6 (п. 5.2.3), как <see cref="CableJournalDocx"/>; шапка повторяется на каждом листе.
/// </summary>
public static class CertificateListDocx
{
    private const double PageWidthMm = 210;
    private const double PageHeightMm = 297;
    private const string Size = "18";

    public static readonly IReadOnlyList<string> Columns =
        ["Поз.", "Наименование", "Тип, марка", "Изготовитель", "Документ о подтверждении соответствия (номер, орган, срок действия)"];

    /// <summary>Ширины граф, мм; сумма — 185 мм (рабочее поле A4 книжного).</summary>
    internal static readonly double[] WidthsMm = [10, 50, 28, 27, 70];

    public static void Write(string path, IReadOnlyList<CertificateRow> rows, TitleBlock stamp, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(stamp);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font }, new FontSize { Val = Size }, new FontSizeComplexScript { Val = Size }, new Languages { Val = "ru-RU" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto, After = "0", Before = "0" }))));

        body.Append(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }, new SpacingBetweenLines { After = "120" }),
            new Run(new RunProperties(new FontSize { Val = "24" }), new Text("Перечень документов о подтверждении соответствия"))));

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
        table.Append(Row([.. Columns], header: true));
        foreach (var r in rows)
        {
            table.Append(Row([r.Position, r.Name, r.Designation, r.Manufacturer, r.Certificate]));
        }

        body.Append(table);
        var section = new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)Twips(PageWidthMm), Height = (UInt32Value)(uint)Twips(PageHeightMm), Orient = PageOrientationValues.Portrait },
            new PageMargin { Left = (UInt32Value)(uint)Twips(20), Right = (UInt32Value)(uint)Twips(5), Top = Twips(5), Bottom = Twips(5 + GostFrame.Height(StampForm.Form6)), Header = 0, Footer = 0, Gutter = 0 });
        body.Append(section);
        GostFrame.Apply(main, section, PageWidthMm, PageHeightMm, stamp, StampForm.Form5, StampForm.Form6);
        DocxFont.Apply(main, font);
        main.Document.Save();
    }

    private static TableRow Row(string[] cells, bool header = false)
    {
        var props = new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(header ? 15 : 8), HeightType = header ? HeightRuleValues.Exact : HeightRuleValues.AtLeast }, new CantSplit());
        if (header)
        {
            props.Append(new TableHeader());
        }

        return new TableRow(
        [
            props,
            .. cells.Select((c, k) => new TableCell(
                new TableCellProperties(new TableCellWidth { Width = Mm(WidthsMm[k]), Type = TableWidthUnitValues.Dxa }, new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
                new Paragraph(
                    new ParagraphProperties(new Justification { Val = header || k == 0 ? JustificationValues.Center : JustificationValues.Left }),
                    new Run(new Text(c) { Space = SpaceProcessingModeValues.Preserve })))),
        ]);
    }

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Mm(double mm) => Twips(mm).ToString(CultureInfo.InvariantCulture);
}
