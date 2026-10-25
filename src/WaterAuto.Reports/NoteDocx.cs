using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports;

/// <summary>
/// Пояснительная записка в docx по ГОСТ 7.32-2017 и ГОСТ 2.105-2019: A4, поля 30/15/20/20 мм, единый шрифт Times New Roman
/// (текст 14 пт, таблицы 12 пт — п. 6.6 ГОСТ 7.32 допускает меньший кегль в таблицах), интервал 1,5, абзацный отступ 1,25 см,
/// выравнивание по ширине, номер страницы внизу по центру (на титульном листе не ставится).
/// Титульный лист, СОДЕРЖАНИЕ (поле оглавления, обновляется при открытии), структурные элементы прописными по центру
/// с новой страницы, разделы «N Заголовок» с новой страницы, подразделы «N.M Заголовок» — стили заголовков для оглавления.
/// Таблицы нумеруются по порядку появления, название «Таблица N — …» над таблицей слева, шапка повторяется на каждой странице.
/// </summary>
public static class NoteDocx
{
    private const string Font = "Times New Roman";
    private const string TextSize = "28";
    private const string TableSize = "24";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex SubsectionPattern = new(@"^\*\*(\d+\.\d+) (.+)\*\*$", RegexOptions.Compiled);
    private static readonly Regex CaptionPattern = new(@"^Таблица \d+ — ", RegexOptions.Compiled);
    private static readonly Regex NumberCell = new(@"^[−\-+]?\d[\d\s]*([,.]\d+)?$|^DN\d+$|^\d+×\d+$", RegexOptions.Compiled);

    public static void Write(string path, NoteDocument note)
    {
        ArgumentNullException.ThrowIfNull(note);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        AddStyles(main);
        AddSettings(main);
        AddTheme(main);

        WriteTitlePage(body, note.Title);
        WriteContents(body);
        var tableNo = 0;
        foreach (var section in note.Sections)
        {
            body.Append(Heading(section));
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case NoteParagraph p when SubsectionPattern.Match(p.Text) is { Success: true } m:
                        body.Append(Subheading($"{m.Groups[1].Value} {m.Groups[2].Value}"));
                        break;
                    case NoteParagraph p:
                        body.Append(Text(p.Text));
                        break;
                    case NoteList l:
                        body.Append(l.Items.Select(ListItem));
                        break;
                    case NotePending p:
                        body.Append(Text(p.Text, highlight: true));
                        break;
                    case NoteFormula f:
                        body.Append(Formula(f));
                        body.Append(f.Legend.Select(l => Text(l, indent: false)));
                        break;
                    case NoteTable t:
                        tableNo++;
                        body.Append(Caption(CaptionPattern.Replace(t.Caption, $"Таблица {tableNo} — ")));
                        body.Append(Table(t));
                        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto })));
                        break;
                }
            }
        }

        body.Append(Heading(new NoteSection("", "Список использованных источников", [])));
        body.Append(note.References.Select((r, i) => Text($"{i + 1} {Escape(r)}")));

        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new SimpleField { Instruction = "PAGE" }));
        body.Append(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new PageSize { Width = (UInt32Value)(uint)Mm(210), Height = (UInt32Value)(uint)Mm(297) },
            new PageMargin { Left = (UInt32Value)(uint)Mm(30), Right = (UInt32Value)(uint)Mm(15), Top = Mm(20), Bottom = Mm(20), Header = 709, Footer = 709, Gutter = 0 },
            new TitlePage()));
        main.Document.Save();
    }

    /// <summary>Звёздочка в обозначениях документов («СНиП 21-02-99*») — как текст, а не разметка курсива.</summary>
    public static string Escape(string text) => text.Replace("*", "\\*", StringComparison.Ordinal);

    private static int Mm(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static string Twips(double mm) => Mm(mm).ToString(CultureInfo.InvariantCulture);

    private static RunFonts Fonts() => new() { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font };

    private static void AddStyles(MainDocumentPart main)
    {
        Style Paragraph(string id, string name, int? outline, bool bold, JustificationValues align, string? firstLine, string? size = null, bool pageBreak = false)
        {
            var pPr = new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "240", Line = "360", LineRule = LineSpacingRuleValues.Auto }, new Justification { Val = align });
            if (pageBreak)
            {
                pPr.Append(new PageBreakBefore());
            }

            if (firstLine is not null)
            {
                pPr.Append(new Indentation { FirstLine = firstLine });
            }

            if (outline is { } o)
            {
                pPr.Append(new OutlineLevel { Val = o });
            }

            var rPr = new StyleRunProperties(Fonts(), new FontSize { Val = size ?? TextSize });
            if (bold)
            {
                rPr.Append(new Bold());
            }

            return new Style(new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(), pPr, rPr)
            {
                Type = StyleValues.Paragraph,
                StyleId = id,
            };
        }

        Style Toc(string id, string name, string indent) => new(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Leader = TabStopLeaderCharValues.Dot, Position = Mm(165) }),
                new Indentation { Left = indent, FirstLine = "0" },
                new Justification { Val = JustificationValues.Left }),
            new StyleRunProperties(Fonts(), new FontSize { Val = TextSize }, new FontSizeComplexScript { Val = TextSize }))
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
        };

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(Fonts(), new FontSize { Val = TextSize }, new FontSizeComplexScript { Val = TextSize }, new Languages { Val = "ru-RU" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto, After = "0" },
                    new Justification { Val = JustificationValues.Both },
                    new WidowControl()))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Paragraph("Heading1", "heading 1", 0, true, JustificationValues.Left, Twips(12.5), pageBreak: true),
            Paragraph("Heading2", "heading 2", 1, true, JustificationValues.Left, Twips(12.5)),
            Paragraph("Structural", "Структурный элемент", 0, true, JustificationValues.Center, null, pageBreak: true),
            Paragraph("ContentsTitle", "Заголовок содержания", null, true, JustificationValues.Center, null),
            new Style(new StyleName { Val = "Hyperlink" }, new StyleRunProperties(Fonts(), new FontSize { Val = TextSize })) { Type = StyleValues.Character, StyleId = "Hyperlink" },
            Toc("TOC1", "toc 1", "0"),
            Toc("TOC2", "toc 2", Twips(5)));
    }

    // Тема со шрифтами Times New Roman: всё, что Word оформляет «шрифтом темы» (строки оглавления и т. п.), — тем же шрифтом.
    private static void AddTheme(MainDocumentPart main)
    {
        var theme = main.AddNewPart<ThemePart>();
        using var stream = theme.GetStream(FileMode.Create);
        using var writer = new StreamWriter(stream);
        writer.Write(ThemeXml);
    }

    private const string ThemeXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Water_Auto">
          <a:themeElements>
            <a:clrScheme name="Water_Auto">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="000000"/></a:dk2><a:lt2><a:srgbClr val="FFFFFF"/></a:lt2>
              <a:accent1><a:srgbClr val="000000"/></a:accent1><a:accent2><a:srgbClr val="000000"/></a:accent2>
              <a:accent3><a:srgbClr val="000000"/></a:accent3><a:accent4><a:srgbClr val="000000"/></a:accent4>
              <a:accent5><a:srgbClr val="000000"/></a:accent5><a:accent6><a:srgbClr val="000000"/></a:accent6>
              <a:hlink><a:srgbClr val="000000"/></a:hlink><a:folHlink><a:srgbClr val="000000"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Water_Auto">
              <a:majorFont><a:latin typeface="Times New Roman"/><a:ea typeface=""/><a:cs typeface="Times New Roman"/><a:font script="Cyrl" typeface="Times New Roman"/></a:majorFont>
              <a:minorFont><a:latin typeface="Times New Roman"/><a:ea typeface=""/><a:cs typeface="Times New Roman"/><a:font script="Cyrl" typeface="Times New Roman"/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Water_Auto">
              <a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
              <a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
              <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
              <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;

    // При открытии Word предлагает обновить поля — оглавление заполнится номерами страниц.
    private static void AddSettings(MainDocumentPart main)
    {
        var settings = main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
    }

    private static Run R(string text, bool bold = false, string? size = null, bool caps = false)
    {
        var props = new RunProperties();
        if (bold)
        {
            props.Append(new Bold());
        }

        if (caps)
        {
            props.Append(new Caps());
        }

        if (size is not null)
        {
            props.Append(new FontSize { Val = size });
        }

        return new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph Line(JustificationValues align, double beforeMm, params Run[] runs) =>
        new([new ParagraphProperties(new SpacingBetweenLines { Before = Twips(beforeMm), After = "0", Line = "360", LineRule = LineSpacingRuleValues.Auto }, new Justification { Val = align }), .. runs]);

    // Титульный лист (ГОСТ 7.32, разд. 5.1): организация вверху, наименование документа в центре, подписи справа, город и год внизу.
    private static void WriteTitlePage(Body body, NoteTitle t)
    {
        body.Append(Line(JustificationValues.Center, 0, R(t.Organization, caps: true)));
        body.Append(Line(JustificationValues.Center, 60, R(t.Title, bold: true, size: "32", caps: true)));
        body.Append(Line(JustificationValues.Center, 4, R(t.Subtitle, size: "32")));
        if (t.ObjectName.Length > 0)
        {
            body.Append(Line(JustificationValues.Center, 4, R($"Объект: {t.ObjectName}")));
        }

        // Подписи — таблица без рамок: должность, место подписи, фамилия.
        var signatures = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                new TableBorders(new TopBorder { Val = BorderValues.None }, new BottomBorder { Val = BorderValues.None }, new LeftBorder { Val = BorderValues.None }, new RightBorder { Val = BorderValues.None }, new InsideHorizontalBorder { Val = BorderValues.None }, new InsideVerticalBorder { Val = BorderValues.None })),
            new TableGrid(new GridColumn { Width = Twips(55) }, new GridColumn { Width = Twips(55) }, new GridColumn { Width = Twips(55) }));
        foreach (var (role, name) in new[] { ("Выполнил", t.Author), ("Руководитель", t.Supervisor) })
        {
            TableCell Cell(string text, JustificationValues align) => new(new Paragraph(new ParagraphProperties(new Justification { Val = align }, new SpacingBetweenLines { Before = Twips(4), Line = "360", LineRule = LineSpacingRuleValues.Auto }), R(text)));
            signatures.Append(new TableRow(Cell(role, JustificationValues.Left), Cell("____________", JustificationValues.Center), Cell(name, JustificationValues.Right)));
        }

        body.Append(Line(JustificationValues.Center, 50));
        body.Append(signatures);
        body.Append(Line(JustificationValues.Center, 30, R(t.CityYear)));
        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    private static void WriteContents(Body body)
    {
        body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ContentsTitle" }), R("СОДЕРЖАНИЕ")));
        body.Append(new Paragraph(
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(@" TOC \o ""1-2"" \h \z \u ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            R("Оглавление обновится при открытии документа (или клавиша F9)."),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
    }

    private static Paragraph Heading(NoteSection s)
    {
        var structural = s.Number.Length == 0;
        return structural
            ? new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Structural" }), R(s.Heading.ToUpper(Ru)))
            : new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), R($"{s.Number} {s.Heading}"));
    }

    private static Paragraph Subheading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }), R(text));

    private static Paragraph Text(string text, bool highlight = false, bool indent = true)
    {
        var props = new ParagraphProperties();
        if (indent)
        {
            props.Append(new Indentation { FirstLine = Twips(12.5) });
        }

        var paragraph = new Paragraph(props);
        paragraph.Append(Runs(text, highlight, null));
        return paragraph;
    }

    // Перечисление (ГОСТ 7.32, п. 6.2.4): тире с абзацного отступа, перенос строки — с левого поля.
    private static Paragraph ListItem(string text)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new Indentation { FirstLine = Twips(12.5) }));
        paragraph.Append(Runs($"– {text}", false, null));
        return paragraph;
    }

    // Название таблицы: над таблицей слева, без абзацного отступа, не отрывается от таблицы.
    private static Paragraph Caption(string text)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "120", After = "60", Line = "360", LineRule = LineSpacingRuleValues.Auto }, new Justification { Val = JustificationValues.Left }));
        paragraph.Append(Runs(text, false, null));
        return paragraph;
    }

    // Формула: табуляция по центру поля текста и номер у правого края (ширина поля A4 при полях 30 и 15 мм — 165 мм).
    private static Paragraph Formula(NoteFormula f)
    {
        var width = Mm(165);
        var props = new ParagraphProperties(
            new KeepNext(),
            new Tabs(
                new TabStop { Val = TabStopValues.Center, Position = width / 2 },
                new TabStop { Val = TabStopValues.Right, Position = width }),
            new SpacingBetweenLines { Before = "120", After = "120", Line = "360", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = JustificationValues.Left });
        var paragraph = new Paragraph(props, new Run(new TabChar()));
        paragraph.Append(Runs(f.Expression, false, null));
        paragraph.Append(new Run(new TabChar()), new Run(new Text($"({f.Number})")));
        return paragraph;
    }

    /// <summary>Разбор разметки: **полужирный**, *курсив*, _{нижний}, ^{верхний}, \n — перенос строки, \* — звёздочка.</summary>
    private static IEnumerable<Run> Runs(string text, bool highlight, string? size)
    {
        var bold = false;
        var italic = false;
        var buffer = new System.Text.StringBuilder();
        Run Make(string value, VerticalPositionValues? position)
        {
            var props = new RunProperties();
            if (bold)
            {
                props.Append(new Bold());
            }

            if (italic)
            {
                props.Append(new Italic());
            }

            if (highlight)
            {
                props.Append(new Highlight { Val = HighlightColorValues.Yellow });
            }

            if (size is not null)
            {
                props.Append(new FontSize { Val = size }, new FontSizeComplexScript { Val = size });
            }

            if (position is { } v)
            {
                props.Append(new VerticalTextAlignment { Val = v });
            }

            return new Run(props, new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }

        IEnumerable<Run> Flush()
        {
            if (buffer.Length > 0)
            {
                yield return Make(buffer.ToString(), null);
                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '*')
            {
                buffer.Append('*');
                i++;
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                bold = !bold;
                i++;
            }
            else if (c == '*')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                italic = !italic;
            }
            else if ((c == '_' || c == '^') && i + 1 < text.Length && text[i + 1] == '{' && text.IndexOf('}', i) is var end and > 0)
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                yield return Make(text[(i + 2)..end], c == '_' ? VerticalPositionValues.Subscript : VerticalPositionValues.Superscript);
                i = end;
            }
            else if (c == '\n')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                yield return new Run(new Break());
            }
            else
            {
                buffer.Append(c);
            }
        }

        foreach (var r in Flush())
        {
            yield return r;
        }
    }

    // Таблица (ГОСТ 7.32, п. 6.6): на всю ширину поля, шапка полужирная по центру и повторяется на новой странице,
    // числа по центру, текст слева; поля ячеек 1,5 мм; строки не разрываются между страницами.
    private static Table Table(NoteTable t)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 6 },
            new BottomBorder { Val = BorderValues.Single, Size = 6 },
            new LeftBorder { Val = BorderValues.Single, Size = 6 },
            new RightBorder { Val = BorderValues.Single, Size = 6 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
        var margins = new TableCellMarginDefault(
            new TopMargin { Width = Twips(0.5), Type = TableWidthUnitValues.Dxa },
            new TableCellLeftMargin { Width = (short)Mm(1.5), Type = TableWidthValues.Dxa },
            new BottomMargin { Width = Twips(0.5), Type = TableWidthUnitValues.Dxa },
            new TableCellRightMargin { Width = (short)Mm(1.5), Type = TableWidthValues.Dxa });
        var table = new Table(new TableProperties(border, new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }, new TableLayout { Type = TableLayoutValues.Autofit }, margins));

        TableCell Cell(string text, bool header)
        {
            var center = header || NumberCell.IsMatch(text.Trim());
            var paragraph = new Paragraph(new ParagraphProperties(
                new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto, Before = "0", After = "0" },
                new Justification { Val = center ? JustificationValues.Center : JustificationValues.Left }));
            paragraph.Append(Runs(header ? $"**{text}**" : text, false, TableSize));
            return new TableCell(new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }), paragraph);
        }

        table.Append(new TableRow([new TableRowProperties(new TableHeader(), new CantSplit()), .. t.Header.Select(h => Cell(h, true))]));
        table.Append(t.Rows.Select(r => new TableRow([new TableRowProperties(new CantSplit()), .. r.Select(c => Cell(c, false))])));
        return table;
    }
}
