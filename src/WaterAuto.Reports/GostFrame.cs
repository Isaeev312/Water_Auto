using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WaterAuto.Core;
using Vml = DocumentFormat.OpenXml.Vml;

namespace WaterAuto.Reports;

/// <summary>Форма основной надписи ГОСТ Р 21.101: 3 — чертежи; 5 — первый лист текстового документа; 6 — последующие листы; None — только рамка и дополнительные графы (титульный лист).</summary>
public enum StampForm
{
    None,
    Form3,
    Form5,
    Form6,
}

/// <summary>
/// Рамка листа и основная надпись по ГОСТ Р 21.101 в docx: рамка — 20 мм слева, 5 мм с остальных сторон (ГОСТ 2.301);
/// основная надпись — в правом нижнем углу, дополнительные графы 20–22 — на поле для подшивки.
/// Рамка — фигура в верхнем колонтитуле, надпись — плавающие таблицы в нижнем; положение — от края листа.
/// </summary>
public static class GostFrame
{
    public const string Font = DocumentStyle.DefaultFont;
    private const int Thick = 12;
    private const int Thin = 4;

    /// <summary>Высота основной надписи, мм: форма 3 — 55, форма 5 — 40, форма 6 — 15.</summary>
    public static double Height(StampForm form) => form switch
    {
        StampForm.Form3 => 55,
        StampForm.Form5 => 40,
        StampForm.Form6 => 15,
        _ => 0,
    };

    /// <summary>Рамка и основные надписи раздела: <paramref name="first"/> — на первом листе, <paramref name="others"/> — на остальных.</summary>
    public static void Apply(MainDocumentPart main, SectionProperties section, double pageWidthMm, double pageHeightMm, TitleBlock block, StampForm first, StampForm others)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(block);

        foreach (var type in new[] { HeaderFooterValues.Default, HeaderFooterValues.First })
        {
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(FrameShape(pageWidthMm, pageHeightMm))));
            header.Header.AddNamespaceDeclaration("v", "urn:schemas-microsoft-com:vml");
            section.PrependChild(new HeaderReference { Type = type, Id = main.GetIdOfPart(header) });

            var form = type == HeaderFooterValues.First ? first : others;
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = form == StampForm.None
                ? new Footer(Additional(pageHeightMm), Tiny())
                : new Footer(
                    Stamp(form, block, pageWidthMm, pageHeightMm, type == HeaderFooterValues.First),
                    Tiny(),
                    Additional(pageHeightMm),
                    Tiny());
            section.InsertAfter(new FooterReference { Type = type, Id = main.GetIdOfPart(footer) }, section.Elements<HeaderReference>().Last());
        }

        if (section.GetFirstChild<TitlePage>() is null)
        {
            section.Append(new TitlePage());
        }
    }

    // Внутренняя рамка: 20 мм от левого края, 5 мм от остальных.
    private static Picture FrameShape(double w, double h) => new(new Vml.Rectangle
    {
        Style = Inv($"position:absolute;margin-left:{20}mm;margin-top:{5}mm;width:{w - 25}mm;height:{h - 10}mm;z-index:-1;mso-position-horizontal-relative:page;mso-position-vertical-relative:page"),
        Filled = false,
        StrokeWeight = "1.5pt",
    });

    // Основная надпись: сетка граф в мм и ячейки (строка, графа, число строк, число граф, текст).
    private static Table Stamp(StampForm form, TitleBlock b, double w, double h, bool first)
    {
        var height = Height(form);
        var x = w - 5 - 185;
        var y = h - 5 - height;
        double[] columns = form == StampForm.Form6 ? [10, 10, 10, 10, 15, 10, 110, 10] : [10, 10, 10, 10, 15, 10, 70, 15, 15, 20];
        var boxes = new List<Box>();
        double[] rows;
        string[] head = ["Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата"];
        OpenXmlElement sheet = b.Sheet is { } s ? Text(s) : Field("PAGE");
        OpenXmlElement sheets = b.SheetCount is { } n ? Text(n.ToString(CultureInfo.InvariantCulture)) : Field("NUMPAGES");

        switch (form)
        {
            case StampForm.Form3:
                rows = [.. Enumerable.Repeat(5.0, 11)];
                ChangeRows(boxes, 0, 3);
                Header(boxes, 3, head);
                Signatures(boxes, 4, 7, b.Signatures);
                boxes.Add(new(0, 6, 2, 4, Text(b.Designation, 12)));
                boxes.Add(new(2, 6, 3, 4, Text(b.Complex)));
                boxes.Add(new(5, 6, 3, 1, Text(b.Building)));
                boxes.Add(new(5, 7, 1, 1, Text("Стадия", 7)));
                boxes.Add(new(5, 8, 1, 1, Text("Лист", 7)));
                boxes.Add(new(5, 9, 1, 1, Text("Листов", 7)));
                boxes.Add(new(6, 7, 2, 1, Text(b.Stage)));
                boxes.Add(new(6, 8, 2, 1, sheet));
                boxes.Add(new(6, 9, 2, 1, first ? sheets : Text("")));
                boxes.Add(new(8, 6, 3, 1, Text(b.Name)));
                boxes.Add(new(8, 7, 3, 3, Text(b.Organization)));
                break;
            case StampForm.Form5:
                rows = [.. Enumerable.Repeat(5.0, 8)];
                ChangeRows(boxes, 0, 1);
                Header(boxes, 1, head);
                Signatures(boxes, 2, 6, b.Signatures);
                boxes.Add(new(0, 6, 3, 4, Text(b.Designation, 12)));
                boxes.Add(new(3, 6, 5, 1, Text(b.Name)));
                boxes.Add(new(3, 7, 1, 1, Text("Стадия", 7)));
                boxes.Add(new(3, 8, 1, 1, Text("Лист", 7)));
                boxes.Add(new(3, 9, 1, 1, Text("Листов", 7)));
                boxes.Add(new(4, 7, 1, 1, Text(b.Stage)));
                boxes.Add(new(4, 8, 1, 1, sheet));
                boxes.Add(new(4, 9, 1, 1, sheets));
                boxes.Add(new(5, 7, 3, 3, Text(b.Organization)));
                break;
            default:
                // Форма 6: слева две строки изменений и шапка по 5 мм; справа графа «Лист» 7 + 8 мм.
                rows = [5, 2, 3, 5];
                for (var c = 0; c < 6; c++)
                {
                    boxes.Add(new(0, c, 1, 1, Text(""), ThinRows: true));
                    boxes.Add(new(1, c, 2, 1, Text(""), ThinRows: true));
                }

                Header(boxes, 3, head);
                boxes.Add(new(0, 6, 4, 1, Text(b.Designation, 12)));
                boxes.Add(new(0, 7, 2, 1, Text("Лист", 7)));
                boxes.Add(new(2, 7, 2, 1, sheet));
                break;
        }

        var table = Grid(columns, rows, boxes, x, y);
        // Под рамкой — формат листа (графа 26).
        table.Append(new TableRow(
            new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(4.5), HeightType = HeightRuleValues.Exact }),
            new TableCell(
                new TableCellProperties(
                    new GridSpan { Val = columns.Length },
                    new TableCellBorders(new TopBorder { Val = BorderValues.Single, Size = Thick }, new LeftBorder { Val = BorderValues.Nil }, new RightBorder { Val = BorderValues.Nil }, new BottomBorder { Val = BorderValues.Nil })),
                Para(Text($"Формат А{(w > 300 || h > 300 ? 3 : 4)}", 7), JustificationValues.Right))));
        return table;
    }

    // Дополнительные графы 20–22 на поле для подшивки: снизу вверх «Инв. № подл.» 25, «Подп. и дата» 35, «Взам. инв. №» 25.
    private static Table Additional(double h)
    {
        var boxes = new List<Box>
        {
            new(0, 0, 1, 1, Text("Взам. инв. №", 7), Vertical: true),
            new(1, 0, 1, 1, Text("Подп. и дата", 7), Vertical: true),
            new(2, 0, 1, 1, Text("Инв. № подл.", 7), Vertical: true),
            new(0, 1, 1, 1, Text("")),
            new(1, 1, 1, 1, Text("")),
            new(2, 1, 1, 1, Text("")),
        };
        return Grid([5, 7], [25, 35, 25], boxes, 8, h - 5 - 85);
    }

    private static void ChangeRows(List<Box> boxes, int from, int count)
    {
        for (var r = from; r < from + count; r++)
        {
            for (var c = 0; c < 6; c++)
            {
                boxes.Add(new(r, c, 1, 1, Text(""), ThinRows: true));
            }
        }
    }

    private static void Header(List<Box> boxes, int row, string[] head)
    {
        for (var c = 0; c < head.Length; c++)
        {
            boxes.Add(new(row, c, 1, 1, Text(head[c], 7)));
        }
    }

    // Строки подписей: «Н. контр.» и «Утв.» — в нижних строках (как на формах 3 и 5), остальные — сверху по порядку.
    private static void Signatures(List<Box> boxes, int from, int count, IReadOnlyList<TitleSignature> signatures)
    {
        string[] bottom = ["Н. контр.", "Утв."];
        var top = signatures.Where(s => !bottom.Contains(s.Role)).ToList();
        var tail = signatures.Where(s => bottom.Contains(s.Role)).ToList();
        var list = new TitleSignature?[count];
        for (var k = 0; k < top.Count && k < count - tail.Count; k++)
        {
            list[k] = top[k];
        }

        for (var k = 0; k < tail.Count && k < count; k++)
        {
            list[count - tail.Count + k] = tail[k];
        }

        for (var k = 0; k < count; k++)
        {
            var s = list[k];
            boxes.Add(new(from + k, 0, 1, 2, Text(s?.Role ?? "", 8), ThinRows: true, Left: true));
            boxes.Add(new(from + k, 2, 1, 2, Text(s?.Name ?? "", 8), ThinRows: true, Left: true));
            boxes.Add(new(from + k, 4, 1, 1, Text(""), ThinRows: true));
            boxes.Add(new(from + k, 5, 1, 1, Text(""), ThinRows: true));
        }
    }

    // Плавающая таблица точно по сетке: граф и строк в мм, положение от левого верхнего угла листа.
    private static Table Grid(double[] columns, double[] rows, List<Box> boxes, double xMm, double yMm)
    {
        var table = new Table(
            new TableProperties(
                new TablePositionProperties
                {
                    HorizontalAnchor = HorizontalAnchorValues.Page,
                    VerticalAnchor = VerticalAnchorValues.Page,
                    TablePositionX = Twips(xMm),
                    TablePositionY = Twips(yMm),
                    LeftFromText = 0,
                    RightFromText = 0,
                    TopFromText = 0,
                    BottomFromText = 0,
                },
                new TableOverlap { Val = TableOverlapValues.Overlap },
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = Twips(columns.Sum()).ToString(CultureInfo.InvariantCulture) },
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 28, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 28, Type = TableWidthValues.Dxa })),
            new TableGrid(columns.Select(c => new GridColumn { Width = Twips(c).ToString(CultureInfo.InvariantCulture) })));

        for (var r = 0; r < rows.Length; r++)
        {
            var row = new TableRow(new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Twips(rows[r]), HeightType = HeightRuleValues.Exact }, new CantSplit()));
            for (var c = 0; c < columns.Length;)
            {
                var box = boxes.FirstOrDefault(b => b.Row <= r && r < b.Row + b.Rows && b.Col == c)
                    ?? throw new InvalidOperationException($"Ячейка {r}:{c} основной надписи не задана.");
                var thin = box.ThinRows ? Thin : Thick;
                var props = new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = Twips(columns.Skip(c).Take(box.Cols).Sum()).ToString(CultureInfo.InvariantCulture) });
                if (box.Cols > 1)
                {
                    props.Append(new GridSpan { Val = box.Cols });
                }

                if (box.Rows > 1)
                {
                    props.Append(new VerticalMerge { Val = r == box.Row ? MergedCellValues.Restart : MergedCellValues.Continue });
                }

                props.Append(new TableCellBorders(
                    new TopBorder { Val = BorderValues.Single, Size = (uint)thin },
                    new LeftBorder { Val = BorderValues.Single, Size = Thick },
                    new BottomBorder { Val = BorderValues.Single, Size = (uint)thin },
                    new RightBorder { Val = BorderValues.Single, Size = Thick }));
                if (box.Vertical)
                {
                    props.Append(new TextDirection { Val = TextDirectionValues.BottomToTopLeftToRight });
                }

                props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                var content = r == box.Row ? box.Content : Text("");
                row.Append(new TableCell(props, Para(content, box.Left ? JustificationValues.Left : JustificationValues.Center)));
                c += box.Cols;
            }

            table.Append(row);
        }

        return table;
    }

    // Межстрочный интервал 0,85 — текст 10 пт помещается в строку высотой 5 мм без обрезки.
    private static Paragraph Para(OpenXmlElement content, JustificationValues align) => new(
        new ParagraphProperties(
            new SpacingBetweenLines { Before = "0", After = "0", Line = "204", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = align }),
        content);

    private static Run Text(string text, double pt = 10) => new(
        new RunProperties(
            new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font },
            new FontSize { Val = ((int)(pt * 2)).ToString(CultureInfo.InvariantCulture) },
            new FontSizeComplexScript { Val = ((int)(pt * 2)).ToString(CultureInfo.InvariantCulture) }),
        new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static SimpleField Field(string name) => new(Text("1")) { Instruction = $" {name} " };

    // Разделитель между плавающими таблицами (иначе Word склеит их), минимальной высоты.
    private static Paragraph Tiny() => new(new ParagraphProperties(
        new SpacingBetweenLines { Before = "0", After = "0", Line = "20", LineRule = LineSpacingRuleValues.Exact },
        new ParagraphMarkRunProperties(new FontSize { Val = "2" })));

    private static int Twips(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private sealed record Box(int Row, int Col, int Rows, int Cols, OpenXmlElement Content, bool ThinRows = false, bool Left = false, bool Vertical = false);
}
