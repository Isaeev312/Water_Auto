using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>
/// Спецификация по форме 1 ГОСТ 21.110 для выпуска: docx A3 альбомный в рамке, первый лист — основная надпись по форме 5,
/// последующие — по форме 6 (ГОСТ Р 21.101). Графы, разделы и порядок позиций — как в <see cref="SpecificationXlsx"/>;
/// шапка таблицы повторяется на каждом листе, строки — не менее 8 мм.
/// </summary>
public static class SpecificationDocx
{
    private const string Size = "22";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static void Write(string path, MaterialTakeoff takeoff, TitleBlock stamp)
    {
        ArgumentNullException.ThrowIfNull(takeoff);
        ArgumentNullException.ThrowIfNull(stamp);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        var font = GostFrame.Font;
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font }, new FontSize { Val = Size }, new FontSizeComplexScript { Val = Size }, new Languages { Val = "ru-RU" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto, After = "0", Before = "0" }))));

        var widths = SpecificationXlsx.WidthsMm;
        var table = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = Mm(widths.Sum()) },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 12 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 12 }, new RightBorder { Val = BorderValues.Single, Size = 12 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 12 }),
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TableCellLeftMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = (short)Twips(1), Type = TableWidthValues.Dxa })),
            new TableGrid(widths.Select(w => new GridColumn { Width = Mm(w) })));

        // Шапка формы 1 — 15 мм, повторяется на каждом листе.
        table.Append(Row(SpecificationXlsx.Columns.ToArray(), 15, HeightRuleValues.Exact, header: true));
        foreach (var (group, title) in SpecificationXlsx.Sections)
        {
            var items = takeoff.Items.Where(i => i.Group == group).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            table.Append(Row(["", title, "", "", "", "", "", "", ""], 8, HeightRuleValues.AtLeast, underline: true));
            var position = 0;
            foreach (var item in items)
            {
                table.Append(Row(
                    [
                        (++position).ToString(CultureInfo.InvariantCulture),
                        item.Name,
                        item.Designation ?? "",
                        "",
                        item.Supplier ?? "",
                        item.Unit,
                        item.Quantity.ToString(Math.Abs(item.Quantity - Math.Round(item.Quantity)) < 1e-9 ? "0" : "0.00", Ru),
                        item.UnitMass is { } m ? m.ToString("0.00", Ru) : "",
                        item.Note ?? "",
                    ],
                    8,
                    HeightRuleValues.AtLeast));
            }
        }

        var total = takeoff.Items.Where(i => i.UnitMass is not null).Sum(i => i.UnitMass!.Value * i.Quantity);
        if (total > 0)
        {
            table.Append(Row(["", "Итого масса позиций с указанной массой, кг", "", "", "", "", "", total.ToString("0.0", Ru), ""], 8, HeightRuleValues.AtLeast));
        }

        body.Append(table);
        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Line = "20", LineRule = LineSpacingRuleValues.Exact })));

        // A3 альбомный; поля — по рамке (20 мм слева, 5 мм с остальных сторон), внизу — над основной надписью формы 5 (40 мм).
        var section = new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)Twips(420), Height = (UInt32Value)(uint)Twips(297), Orient = PageOrientationValues.Landscape },
            new PageMargin { Left = (UInt32Value)(uint)Twips(20), Right = (UInt32Value)(uint)Twips(5), Top = Twips(5), Bottom = Twips(5 + GostFrame.Height(StampForm.Form5)), Header = 0, Footer = 0, Gutter = 0 });
        body.Append(section);
        GostFrame.Apply(main, section, 420, 297, stamp, StampForm.Form5, StampForm.Form6);
        main.Document.Save();
    }

    private static TableRow Row(string[] cells, double heightMm, HeightRuleValues rule, bool header = false, bool underline = false)
    {
        var widths = SpecificationXlsx.WidthsMm;
        var props = new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(heightMm), HeightType = rule }, new CantSplit());
        if (header)
        {
            props.Append(new TableHeader());
        }

        return new TableRow(
        [
            props,
            .. cells.Select((c, k) =>
            {
                var cell = new TableCellProperties(new TableCellWidth { Width = Mm(widths[k]), Type = TableWidthUnitValues.Dxa });
                if (header)
                {
                    cell.Append(new TableCellBorders(new BottomBorder { Val = BorderValues.Single, Size = 12 }));
                }

                cell.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

                // Шапка, позиция и числовые графы — по центру; наименования и обозначения — слева.
                var center = header || underline || k is 0 or 5 or 6 or 7;
                var run = new Run(new Text(c) { Space = SpaceProcessingModeValues.Preserve });
                if (underline)
                {
                    run.PrependChild(new RunProperties(new Underline { Val = UnderlineValues.Single }));
                }
                else if (header)
                {
                    run.PrependChild(new RunProperties(new FontSize { Val = "18" }));
                }

                return new TableCell(cell, new Paragraph(new ParagraphProperties(new Justification { Val = center ? JustificationValues.Center : JustificationValues.Left }), run));
            }),
        ]);
    }

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Mm(double mm) => Twips(mm).ToString(CultureInfo.InvariantCulture);
}
